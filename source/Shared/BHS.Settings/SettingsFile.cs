namespace BHS.Settings;

/// <summary>
/// One settings file, kept in memory and, if asked, watched for changes.
/// </summary>
/// <remarks>
/// Reload is not a nicety. One of the two sides is a Revit process that takes a minute to start and
/// may then run all day, and "restart Revit to change a timeout" is not a real answer.
/// </remarks>
public sealed class SettingsFile : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _delay;

    private IDictionary<string, string?> _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _disposed;

    public SettingsFile(SettingsLayer layer, bool watch, TimeSpan? reloadDelay = null)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));

        // Editors do not write a file once. They write, truncate, rename and touch the timestamp,
        // and a reader that believes the first event reads a half-written file.
        _delay = reloadDelay ?? TimeSpan.FromMilliseconds(250);

        Load();

        if (watch)
            Watch();
    }

    public SettingsLayer Layer { get; }

    /// <summary>What the file said, last time it was read.</summary>
    public IDictionary<string, string?> Values
    {
        get { lock (_gate) return _values; }
    }

    /// <summary>What went wrong the last time it was read, if anything.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Raised after the file has been re-read.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Reads the file, or leaves the last good contents in place.
    /// </summary>
    /// <remarks>
    /// A file that is absent is empty, and that is the whole point of layering: a machine with no
    /// policy and a user who never changed a setting are the normal case. A file that exists and
    /// cannot be read is a mistake, and it is recorded rather than thrown: on a reload the throw
    /// would come from a timer thread with no caller, and inside Revit that ends somebody's session.
    /// The first read is different - there is a caller there, and it should hear about it.
    /// </remarks>
    public void Load()
    {
        try
        {
            var values = File.Exists(Layer.Path)
                ? JsonSettings.Read(Layer.Path)
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            lock (_gate)
                _values = values;

            Error = null;
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Error = error;
        }
    }

    /// <summary>
    /// Starts watching, if there is a directory to watch.
    /// </summary>
    /// <remarks>
    /// A layer whose directory does not exist yet is simply not watched: a watcher cannot be pointed
    /// at an absent directory, and creating one to satisfy it would mean scattering empty folders
    /// through <c>%ProgramData%</c> on every start. The consequence, stated plainly: a layer created
    /// while the process runs is picked up on the next start, not immediately.
    /// </remarks>
    private void Watch()
    {
        var directory = Path.GetDirectoryName(Layer.Path);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        try
        {
            _watcher = new FileSystemWatcher(directory!, Path.GetFileName(Layer.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Deleted += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Watching is an improvement, not a requirement. A directory on a share that refuses
            // change notifications should cost the reload, not the settings.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _debounce ??= new Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounce.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Reload()
    {
        try
        {
            Load();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            // Last line of defence: this runs on a timer thread with no caller, and an exception
            // escaping here ends the process - inside Revit, somebody else's Revit.
            Error = error;
        }
    }

    public override string ToString() => Layer.ToString();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _debounce?.Dispose();
    }
}
