using System.Threading;
using BHS.Settings;

namespace BHS.Logging;

/// <summary>
/// Holds the sinks and hands out an <see cref="ILog"/> per category.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Default"/> exists from the first line of code, before anything has been configured
/// and before there is a channel to anywhere. That is not a convenience: inside Revit the most
/// interesting failures happen during <c>OnStartup</c>, and a logger that has to be built first
/// records none of them.
/// </para>
/// <para>
/// <b>Additive and idempotent, on purpose.</b> Revit 2024 loads every add-in into one AppDomain,
/// so two of our own editions share one <c>BHS.Logging.dll</c> and therefore one
/// <see cref="Default"/>. Whoever configures second must not undo the first: <see cref="Add"/>
/// adds a sink, <see cref="Apply"/> recomputes levels, and neither takes anything away. This is
/// the same hazard that kept the static host of the previous generation off the list of things
/// worth carrying over - it assumed it was alone, and on 2024 nothing is.
/// </para>
/// <para>
/// A host that genuinely needs its own arrangement builds its own <see cref="LogRouter"/> and
/// hands out <see cref="ILog"/> from that one instead. Then the sharing question does not arise.
/// </para>
/// </remarks>
public sealed class LogRouter : IDisposable
{
    private static int _primaryThreadId;

    private readonly object _gate = new();
    private readonly Dictionary<string, LogLevel> _categoryLevels = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _openFileByDefault;

    private ILogSink[] _sinks = Array.Empty<ILogSink>();
    private LogLevel _defaultLevel = LogLevel.Information;
    private long _dropped;
    private bool _openedByDefault;
    private bool _disposed;

    /// <summary>The router every static <see cref="Log"/> call goes through.</summary>
    /// <remarks>
    /// Alone among routers, this one opens a log file by itself the first time anybody writes to it
    /// and nobody has attached a sink. That is what makes the very first line of <c>OnStartup</c>
    /// work, which is where the failures worth catching happen - before there are settings, before
    /// there is a channel, sometimes before the add-in has finished loading at all.
    /// </remarks>
    public static LogRouter Default { get; } = new(openFileByDefault: true);

    /// <summary>A router of its own, for a host that wants its sinks to itself.</summary>
    public LogRouter()
        : this(openFileByDefault: false)
    {
    }

    private LogRouter(bool openFileByDefault) => _openFileByDefault = openFileByDefault;

    /// <summary>
    /// The host's main thread, so a record can say whether it was written on it.
    /// </summary>
    /// <remarks>
    /// Revit-side sets this to the API thread during <c>OnStartup</c>, which is the only place that
    /// thread can be identified. Left at zero it simply means "unknown", and every record says it
    /// was not on the primary thread - which is honest, because nobody said which one that is.
    /// </remarks>
    public static int PrimaryThreadId
    {
        get => Volatile.Read(ref _primaryThreadId);
        set => Volatile.Write(ref _primaryThreadId, value);
    }

    /// <summary>Records that were written but could not be delivered.</summary>
    /// <remarks>
    /// Not an error counter: a sink that throws is caught and this is incremented, because the one
    /// thing logging may never do is take down the caller. Worth reporting somewhere visible - a
    /// number climbing here means records are being lost silently, which is the failure mode a log
    /// is least able to report about itself.
    /// </remarks>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The sinks currently attached.</summary>
    public IReadOnlyList<ILogSink> Sinks => Volatile.Read(ref _sinks);

    /// <summary>A log for one category. Cheap enough to call at every use.</summary>
    public ILog For(string category) => new CategoryLog(this, category ?? string.Empty);

    /// <summary>A log named after a type, which is the usual case.</summary>
    public ILog For<T>() => For(typeof(T).FullName ?? typeof(T).Name);

    /// <summary>
    /// Attaches a sink. Never replaces one.
    /// </summary>
    /// <remarks>
    /// Copy-on-write rather than a lock around <see cref="Emit"/>: attaching happens a handful of
    /// times at startup, writing happens forever and from every thread.
    /// </remarks>
    public LogRouter Add(ILogSink sink)
    {
        if (sink is null)
            throw new ArgumentNullException(nameof(sink));

        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LogRouter));

            var replacement = new ILogSink[_sinks.Length + 1];
            Array.Copy(_sinks, replacement, _sinks.Length);
            replacement[_sinks.Length] = sink;

            Volatile.Write(ref _sinks, replacement);
        }

        return this;
    }

    /// <summary>
    /// Reads the <c>Log</c> section: the levels, and whatever each sink wants from it.
    /// </summary>
    /// <remarks>
    /// <c>Log:Level</c> is the default; <c>Log:Levels:&lt;category prefix&gt;</c> overrides it for
    /// everything under that prefix, longest prefix winning. Safe to call again on every settings
    /// reload - it recomputes rather than accumulates, and it never detaches a sink, which is what
    /// keeps a second caller from unconfiguring the first.
    /// <para>
    /// <b>A value that cannot be read is complained about, not thrown.</b> This is the one place the
    /// repository's standing rule - an unreadable value is refused rather than defaulted - is
    /// deliberately relaxed, and the owner agreed to the exception. The reason is proportion: a typo
    /// in a log level would otherwise stop an add-in from loading inside Revit, where nobody is
    /// there to read the dialog, and the failure would look nothing like its cause. Everywhere else
    /// the rule stands.
    /// </para>
    /// </remarks>
    public void Apply(ISettings settings)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        var log = For("BHS.Logging");
        var section = settings.Section("Log");
        var levels = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
        var fallback = LogLevel.Information;

        var declared = section["Level"];

        if (!string.IsNullOrEmpty(declared) && !TryParseLevel(declared!, out fallback))
        {
            log.Warn("setting 'Log:Level' is '{0}', which is not a log level - keeping {1}", declared, fallback);
            fallback = LogLevel.Information;
        }

        var perCategory = section.Section("Levels");

        foreach (var key in perCategory.Keys)
        {
            var value = perCategory[key];

            if (string.IsNullOrEmpty(value))
                continue;

            if (TryParseLevel(value!, out var level))
                levels[key] = level;
            else
                log.Warn("setting 'Log:Levels:{0}' is '{1}', which is not a log level - ignored", key, value);
        }

        lock (_gate)
        {
            _categoryLevels.Clear();

            foreach (var pair in levels)
                _categoryLevels[pair.Key] = pair.Value;

            _defaultLevel = fallback;
        }

        foreach (var sink in Volatile.Read(ref _sinks))
        {
            if (sink is not IConfigurableLogSink configurable)
                continue;

            try
            {
                configurable.Configure(settings);
            }
            catch (Exception error)
            {
                log.Warn(error, "sink {0} could not be configured", sink.GetType().Name);
            }
        }
    }

    /// <summary>The level in force for one category.</summary>
    public LogLevel LevelFor(string category)
    {
        lock (_gate)
        {
            if (_categoryLevels.Count == 0)
                return _defaultLevel;

            var best = _defaultLevel;
            var matched = -1;

            // Longest matching prefix wins, so "BHS.Transport" can be quieter than "BHS" without
            // either of them having to know the other exists.
            foreach (var pair in _categoryLevels)
            {
                if (pair.Key.Length > matched &&
                    category.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    matched = pair.Key.Length;
                    best = pair.Value;
                }
            }

            return best;
        }
    }

    internal bool IsEnabled(string category, LogLevel level)
    {
        if (level == LogLevel.None)
            return false;

        EnsureSink();

        var sinks = Volatile.Read(ref _sinks);

        if (sinks.Length == 0 || level < LevelFor(category))
            return false;

        foreach (var sink in sinks)
        {
            if (level >= sink.Minimum)
                return true;
        }

        return false;
    }

    internal void Write(string category, LogLevel level, string message, Exception? error)
    {
        EnsureSink();

        var sinks = Volatile.Read(ref _sinks);

        if (sinks.Length == 0)
            return;

        var threadId = Environment.CurrentManagedThreadId;
        var primary = PrimaryThreadId;

        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            category,
            message ?? string.Empty,
            error,
            threadId,
            primary != 0 && threadId == primary);

        foreach (var sink in sinks)
        {
            if (level < sink.Minimum)
                continue;

            try
            {
                sink.Emit(in entry);
            }
            catch (Exception failure)
            {
                // Deliberately every exception. A sink is a file on a share that went away, or a
                // pipe whose other end just died; none of that is worth taking the caller down for,
                // and the caller is often Revit's API thread.
                if (Interlocked.Increment(ref _dropped) == 1)
                    Complain(sink, failure);
            }
        }
    }

    /// <summary>
    /// Opens the default log file, once, if nobody has attached anything.
    /// </summary>
    /// <remarks>
    /// Named after the process rather than after the Revit release, because at this point nothing
    /// has been told which release this is - the Revit API has not been touched yet and may not be
    /// reachable at all. The release goes into the file header instead, and several Revit versions
    /// running at once stay apart by their process id, which is already in the name.
    /// </remarks>
    private void EnsureSink()
    {
        if (!_openFileByDefault || Volatile.Read(ref _sinks).Length > 0)
            return;

        lock (_gate)
        {
            if (_openedByDefault || _disposed || _sinks.Length > 0)
                return;

            _openedByDefault = true;
        }

        try
        {
            Add(new FileLogSink(ProcessName()));
        }
        catch (Exception)
        {
            // Opening a log must never be the thing that fails. Nothing is attached, every write
            // becomes a no-op, and the process carries on.
        }
    }

    /// <summary>
    /// Says out loud, once, that records have started going missing.
    /// </summary>
    /// <remarks>
    /// Straight to <c>Trace</c> rather than through this router, and that is the whole point: the
    /// reason a record was dropped is almost always the file system, and a complaint written to the
    /// file would be lost the same way. <c>Trace</c> touches no disk, so it survives exactly the
    /// failure worth hearing about.
    /// <para>
    /// Once, on the first one. A directory that has gone away stays gone, and a complaint per record
    /// would bury the machine in the same message. The running total is <see cref="Dropped"/>, which
    /// the hosts report when asked - because an empty log looks identical whether nothing happened
    /// or nothing could be written.
    /// </para>
    /// </remarks>
    private static void Complain(ILogSink sink, Exception failure)
    {
        try
        {
            System.Diagnostics.Trace.WriteLine(
                "BHS.Logging: " + sink.GetType().Name + " is dropping records - " +
                failure.GetType().Name + ": " + failure.Message);
        }
        catch (Exception)
        {
            // There is nowhere left to say it.
        }
    }

    private static string ProcessName()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            return "bhs";
        }
    }

    private static bool TryParseLevel(string value, out LogLevel level)
    {
#if NET48
        try
        {
            level = (LogLevel)Enum.Parse(typeof(LogLevel), value, ignoreCase: true);
            return Enum.IsDefined(typeof(LogLevel), level);
        }
        catch (ArgumentException)
        {
            level = LogLevel.Information;
            return false;
        }
#else
        return Enum.TryParse(value, ignoreCase: true, out level) && Enum.IsDefined(typeof(LogLevel), level);
#endif
    }

    public void Dispose()
    {
        ILogSink[] sinks;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            sinks = _sinks;
            Volatile.Write(ref _sinks, Array.Empty<ILogSink>());
        }

        foreach (var sink in sinks)
        {
            try
            {
                sink.Dispose();
            }
            catch (Exception)
            {
                // Same reasoning as above, and more so: this runs while something is shutting down.
            }
        }
    }

    private sealed class CategoryLog : ILog
    {
        private readonly LogRouter _router;
        private readonly string _category;

        public CategoryLog(LogRouter router, string category)
        {
            _router = router;
            _category = category;
        }

        public bool IsEnabled(LogLevel level) => _router.IsEnabled(_category, level);

        public void Write(LogLevel level, string message, Exception? error) =>
            _router.Write(_category, level, message, error);
    }
}
