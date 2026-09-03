using System.Globalization;
using System.Text;
using BHS.Settings;

namespace BHS.Logging;

/// <summary>
/// The log file. The only sink that is always there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Under <c>%LocalAppData%</c>, not <c>%AppData%</c>.</b> Settings roam with the profile and
/// should; logs are about one machine and must not. And not Revit's own temp directory either -
/// that has a session GUID in its name, so a log written there cannot be found from outside by
/// anyone who never got a registration, which is precisely the case a log exists for. The probe's
/// own logger reached the same conclusion the same way.
/// </para>
/// <para>
/// <b>One file per process, not one per day.</b> Several Revit releases and a Win-side host run at
/// once; a shared file would need cross-process locking - open, append, close, on every line, which
/// is what <c>..\BHS</c> configured and it is the slowest path there is. With a file of its own the
/// handle stays open, nothing locks, and correlation comes from the header instead.
/// </para>
/// <para>
/// <b>Written synchronously and flushed every line.</b> The interesting failures are the ones where
/// the process is about to be killed - by the runner's budget, or by Revit itself - and a buffered
/// log loses exactly those. The cost is that the writing thread waits for the disk, which locally
/// is microseconds. It stops being microseconds if the directory is on a network share, so while
/// this sink is synchronous the log directory must stay local.
/// </para>
/// </remarks>
public sealed class FileLogSink : ILogSink, IConfigurableLogSink
{
    /// <summary>Where logs go when nothing says otherwise.</summary>
    public static string DefaultDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BHS", "Logs");

    public const long DefaultMaxSize = 8L * 1024 * 1024;
    public const int DefaultRetain = 200;
    public const int DefaultRetainDays = 14;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _stem;

    private StreamWriter? _writer;
    private long _written;
    private long _maxSize = DefaultMaxSize;
    private int _part = 1;
    private bool _disposed;

    /// <summary>
    /// Opens the file for this process.
    /// </summary>
    /// <param name="name">
    /// What this process is, for the file name: <c>revit2026</c>, <c>winside</c>. It goes in the
    /// name rather than in a column because the first question anyone asks of a log directory is
    /// which file belongs to which process.
    /// </param>
    /// <param name="directory">Where to write, or null for <see cref="DefaultDirectory"/>.</param>
    public FileLogSink(string name, string? directory = null)
    {
        _directory = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory!;

        _stem = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-{1}-{2:yyyyMMdd-HHmmss}",
            string.IsNullOrEmpty(name) ? "bhs" : name,
            CurrentProcessId,
            DateTime.Now);

        Open();
    }

    public LogLevel Minimum { get; private set; } = LogLevel.Information;

    /// <summary>The file currently being written, or empty when it could not be opened.</summary>
    public string Path { get; private set; } = string.Empty;

    private static int CurrentProcessId =>
#if NET48
        System.Diagnostics.Process.GetCurrentProcess().Id;
#else
        Environment.ProcessId;
#endif

    public void Configure(ISettings settings)
    {
        var section = settings.Section("Log").Section("File");

        Minimum = section.Flag("Enabled", true) ? LogLevel.Trace : LogLevel.None;
        _maxSize = Math.Max(64 * 1024, section.Number("MaxSize", (int)DefaultMaxSize));
    }

    /// <summary>Writes an opening block: who we are, and which assemblies actually got loaded.</summary>
    /// <remarks>
    /// The second half is the standing requirement in <c>CLAUDE.md</c> - log the assemblies that
    /// were really loaded, with path and version - and it is the header rather than a separate file
    /// because the answer is only meaningful next to the run it belongs to.
    /// </remarks>
    public void WriteHeader(IEnumerable<KeyValuePair<string, string>> facts)
    {
        lock (_gate)
        {
            if (_writer is null)
                return;

            try
            {
                foreach (var fact in facts)
                    _writer.WriteLine("# " + fact.Key + ": " + fact.Value);

                _writer.WriteLine("#");
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
        }
    }

    public void Emit(in LogEntry entry)
    {
        // Built outside the lock: formatting is the expensive half, and every thread in the
        // process may be here at once.
        var line = Render(in entry);

        lock (_gate)
        {
            if (_writer is null || _disposed)
                return;

            _writer.Write(line);
            _written += line.Length;

            if (_written >= _maxSize)
                Roll();
        }
    }

    private static string Render(in LogEntry entry)
    {
        var text = new StringBuilder(entry.Message.Length + 96);

        text.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        text.Append("  ").Append(Abbreviate(entry.Level)).Append("  [");
        text.Append(entry.ThreadId.ToString(CultureInfo.InvariantCulture).PadLeft(3));

        // Costs nothing and answers the mistake this project makes most: a call arriving over the
        // channel is never on Revit's API thread, and telling the two apart in the file is the
        // difference between a puzzle and a diagnosis.
        text.Append(entry.OnPrimaryThread ? '*' : ' ').Append("]  ");
        text.Append(entry.Category.PadRight(28)).Append("  ").Append(entry.Message);
        text.AppendLine();

        for (var error = entry.Error; error is not null; error = error.InnerException)
        {
            text.Append("        ").Append(error.GetType().FullName).Append(": ").AppendLine(error.Message);

            if (!string.IsNullOrEmpty(error.StackTrace))
                text.AppendLine(error.StackTrace);
        }

        return text.ToString();
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    /// <summary>
    /// Starts the next part of this run's log.
    /// </summary>
    /// <remarks>
    /// A new file rather than a rename of the open one. Renaming a file you hold open is awkward on
    /// Windows and buys nothing here, because the name already carries the process and the moment
    /// it started - there is no fixed "current" name that a reader would be following.
    /// </remarks>
    private void Roll()
    {
        var previous = Path;

        Close();
        _part++;
        Open();

        if (_writer is not null && !string.IsNullOrEmpty(previous))
            _writer.WriteLine("# continued from " + previous);
    }

    private void Open()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var name = _part == 1
                ? _stem + ".log"
                : _stem + "-" + _part.ToString(CultureInfo.InvariantCulture) + ".log";

            Path = System.IO.Path.Combine(_directory, name);

            // Shared for reading and deleting: Win-side reads the log of a live Revit this way, and
            // in v1 that is the only way it sees one at all. Deletion has to be allowed too, or
            // housekeeping cannot touch a file whose process is still running.
            var stream = new FileStream(
                Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
            _written = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A log that cannot be opened must not stop what it was going to describe.
            _writer = null;
            Path = string.Empty;
        }
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }

        _writer = null;
    }

    /// <summary>
    /// Deletes what is too old or too plentiful.
    /// </summary>
    /// <remarks>
    /// Called on a pool thread, never during startup. Enumerating a directory of a few hundred
    /// files is quick, but the startup budget it would join is the one that already reaches thirty
    /// seconds to registration on Revit 2024, and nothing about housekeeping deserves a share of it.
    /// </remarks>
    public static void Sweep(string? directory = null, int retain = DefaultRetain, int retainDays = DefaultRetainDays)
    {
        var folder = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory!;

        try
        {
            if (!Directory.Exists(folder))
                return;

            var files = new DirectoryInfo(folder).GetFiles("*.log");
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, retainDays));
            var keep = Math.Max(1, retain);

            var ordered = files.OrderByDescending(file => file.LastWriteTime).ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                var file = ordered[index];

                if (index < keep && file.LastWriteTime >= cutoff)
                    continue;

                try
                {
                    file.Delete();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Almost certainly a log held open by a live process. Next time.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            Close();
        }
    }
}
