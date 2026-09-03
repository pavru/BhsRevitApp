namespace BHS.Settings;

/// <summary>A flat, colon-separated store of settings, or a section of one.</summary>
/// <remarks>
/// Flat and stringly-typed on purpose: that is what a settings store is, and it is the shape
/// <c>Microsoft.Extensions.Configuration</c> keeps internally too, so the adapter in
/// <c>BHS.Settings.Configuration</c> is a copy rather than a translation.
/// </remarks>
public interface ISettings
{
    /// <summary>The value for a key, or null when it is absent or explicitly cleared.</summary>
    string? this[string key] { get; }

    /// <summary>Every key this store holds, fully qualified.</summary>
    IEnumerable<string> Keys { get; }

    /// <summary>A view of everything under one prefix.</summary>
    ISettings Section(string name);
}

/// <summary>How a side wants its settings assembled.</summary>
public sealed class SettingsOptions
{
    /// <summary>Which side is reading. Decides which side-specific file is layered in.</summary>
    public ProcessSide Side { get; set; } = ProcessSide.WinSide;

    /// <summary>The Revit release, for the release-specific layer. Ignored on Win-side.</summary>
    public int? Release { get; set; }

    /// <summary>Where the shipped defaults are, or null for the directory this assembly sits in.</summary>
    public string? ProductDirectory { get; set; }

    /// <summary>Watch the files and reload when they change.</summary>
    public bool ReloadOnChange { get; set; } = true;

    /// <summary>Read environment variables that start with <see cref="EnvironmentPrefix"/>.</summary>
    public bool IncludeEnvironment { get; set; } = true;

    /// <summary>
    /// The environment variable prefix.
    /// </summary>
    /// <remarks>
    /// <c>BHS_LAUNCH__SHUTDOWNTIMEOUT</c> becomes <c>Launch:ShutdownTimeout</c>: the prefix is
    /// dropped and a double underscore becomes the separator, the same spelling the stock
    /// environment provider uses, because an environment variable name may not contain a colon.
    /// </remarks>
    public string EnvironmentPrefix { get; set; } = "BHS_";

    /// <summary>Values the host sets itself, above everything read from disk.</summary>
    public IDictionary<string, string?> Overrides { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The layered settings, read from disk.
/// </summary>
/// <remarks>
/// This is what makes startup order stop mattering. Each side reads its own settings and runs with
/// no channel at all, so neither waits for the other; what travels over the channel afterwards is
/// only what one side alone can know - which Revit, which document, where it was installed.
/// <para>
/// Layers are merged into one map rather than searched in turn. Merging is what makes a null a
/// useful value: a deeper layer that says <c>"Timeout": null</c> clears what a shallower one set,
/// which searching in turn could not express.
/// </para>
/// </remarks>
public sealed class LayeredSettings : ISettings, IDisposable
{
    private readonly List<SettingsFile> _files = new();
    private readonly SettingsOptions _options;
    private readonly object _gate = new();

    private Dictionary<string, string?> _merged = new(StringComparer.OrdinalIgnoreCase);

    private LayeredSettings(SettingsOptions options)
    {
        _options = options;

        foreach (var layer in SettingsLayout.Files(options.Side, options.Release, options.ProductDirectory))
        {
            var file = new SettingsFile(layer, options.ReloadOnChange);
            file.Changed += (_, _) => Merge(raise: true);
            _files.Add(file);
        }

        Merge(raise: false);
    }

    /// <summary>Reads everything, in order, and starts watching if asked to.</summary>
    public static LayeredSettings Read(SettingsOptions options) =>
        new(options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>The files that were looked at, in order, whether or not they exist.</summary>
    public IReadOnlyList<SettingsLayer> Layers
    {
        get
        {
            var layers = _files.Select(file => file.Layer).ToList();

            if (_options.IncludeEnvironment)
                layers.Add(new SettingsLayer(SettingsLayerKind.Process, _options.EnvironmentPrefix + "* (environment)"));

            if (_options.Overrides.Count > 0)
                layers.Add(new SettingsLayer(SettingsLayerKind.Process, "host overrides"));

            return layers;
        }
    }

    /// <summary>Anything that went wrong reading a file, by layer.</summary>
    public IEnumerable<KeyValuePair<SettingsLayer, Exception>> Errors =>
        _files.Where(file => file.Error is not null)
              .Select(file => new KeyValuePair<SettingsLayer, Exception>(file.Layer, file.Error!));

    public string? this[string key]
    {
        get
        {
            lock (_gate)
                return _merged.TryGetValue(key, out var value) ? value : null;
        }
    }

    public IEnumerable<string> Keys
    {
        get { lock (_gate) return _merged.Keys.ToList(); }
    }

    public ISettings Section(string name) => new SettingsSection(this, name);

    /// <summary>Raised after any layer has been re-read.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads every layer, whether or not anything changed.</summary>
    public void Reload()
    {
        foreach (var file in _files)
            file.Load();

        Merge(raise: true);
    }

    private void Merge(bool raise)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in _files)
        {
            foreach (var pair in file.Values)
                merged[pair.Key] = pair.Value;
        }

        if (_options.IncludeEnvironment)
        {
            foreach (var pair in ReadEnvironment(_options.EnvironmentPrefix))
                merged[pair.Key] = pair.Value;
        }

        foreach (var pair in _options.Overrides)
            merged[pair.Key] = pair.Value;

        lock (_gate)
            _merged = merged;

        if (raise)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Environment variables, as the last layer.
    /// </summary>
    /// <remarks>
    /// Read at each merge rather than watched, because the environment of a running process does
    /// not change under it. Note what this deliberately does not read: the correlation token, which
    /// travels in the environment too, but under its own name and as nobody's setting.
    /// </remarks>
    private static IEnumerable<KeyValuePair<string, string?>> ReadEnvironment(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            yield break;

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = name.Substring(prefix.Length).Replace("__", ":");

            if (key.Length > 0)
                yield return new KeyValuePair<string, string?>(key, entry.Value as string);
        }
    }

    public void Dispose()
    {
        foreach (var file in _files)
            file.Dispose();
    }

    /// <summary>Everything under one prefix, addressed without it.</summary>
    private sealed class SettingsSection : ISettings
    {
        private readonly ISettings _parent;
        private readonly string _prefix;

        public SettingsSection(ISettings parent, string name)
        {
            _parent = parent;
            _prefix = name.Length == 0 ? string.Empty : name + ":";
        }

        public string? this[string key] => _parent[_prefix + key];

        public IEnumerable<string> Keys =>
            _parent.Keys.Where(key => key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(key => key.Substring(_prefix.Length));

        public ISettings Section(string name) => new SettingsSection(_parent, _prefix + name);
    }
}
