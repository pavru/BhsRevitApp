using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Settings;

namespace BHS.Revit.Host;

/// <summary>
/// One document's settings, read through the chain its keys belong to.
/// </summary>
/// <remarks>
/// Two chains, chosen per key by its prefix:
/// <list type="bullet">
/// <item><c>Model:…</c> is a project rule - product file, machine file, the model, then the
/// environment. The user's own file is not consulted, which is what "a person cannot work around
/// the project's rules" actually means;</item>
/// <item>everything else is a preference and reads the ordinary chain, in which the model has no
/// say at all.</item>
/// </list>
/// The prefix is deliberate and not a table of declarations: a table is a register somebody has to
/// keep, and it drifts from the code the first inattentive day. A prefix is visible in the file.
/// </remarks>
internal sealed class ModelSettings : IModelSettings
{
    /// <summary>The prefix that marks a key as belonging to the project rather than the person.</summary>
    public const string ProjectPrefix = "Model:";

    private readonly LayeredSettings _process;
    private readonly IDictionary<string, string?> _model;

    public ModelSettings(
        Document document,
        LayeredSettings process,
        IDictionary<string, string?> model,
        ModelSettingsOrigin origin)
    {
        Document = document;
        Origin = origin;
        _process = process;
        _model = model;
    }

    public Document Document { get; }

    public ModelSettingsOrigin Origin { get; }

    public string? this[string key]
    {
        get
        {
            if (!IsProjectScoped(key))
                return _process[key];

            // Above everything, in every chain: the emergency lever and the path automation takes
            // would not be one if some keys were out of its reach.
            if (_process.Process.TryGetValue(key, out var overridden))
                return overridden;

            if (_model.TryGetValue(key, out var declared))
                return declared;

            return _process.BelowUser.TryGetValue(key, out var below) ? below : null;
        }
    }

    public IEnumerable<string> Keys
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in _process.Keys)
            {
                // A user-scoped key from the ordinary chain, or a project-scoped one that the
                // project chain will answer for anyway.
                if (seen.Add(key))
                    yield return key;
            }

            foreach (var key in _model.Keys)
            {
                if (IsProjectScoped(key) && seen.Add(key))
                    yield return key;
            }
        }
    }

    public ISettings Section(string name) => new Section_(this, name);

    private static bool IsProjectScoped(string key) =>
        key.StartsWith(ProjectPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A prefixed view, the same shape the layered settings use.</summary>
    private sealed class Section_ : ISettings
    {
        private readonly ISettings _parent;
        private readonly string _prefix;

        public Section_(ISettings parent, string name)
        {
            _parent = parent;
            _prefix = name.Length == 0 ? string.Empty : name + ":";
        }

        public string? this[string key] => _parent[_prefix + key];

        public IEnumerable<string> Keys =>
            _parent.Keys.Where(key => key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(key => key.Substring(_prefix.Length));

        public ISettings Section(string name) => new Section_(_parent, _prefix + name);
    }
}

/// <summary>
/// Hands out one document's settings, and remembers what it read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read lazily, on the first question, and not when the document opens.</b> Most sessions never
/// ask a project setting, and on Revit 2024 <c>DocumentOpened</c> lands in the most expensive part
/// of startup. The collector that finds the storage is cheap but not free, and whoever asks should
/// be the one paying.
/// </para>
/// <para>
/// The cache is keyed by <see cref="Document"/> itself, which is documented as safe: equality and
/// hash are overridden and are the same for instances standing for one open document. Entries are
/// dropped on <c>DocumentClosing</c> rather than <c>DocumentClosed</c> - checked against the
/// metadata, <c>DocumentClosedEventArgs</c> carries only an int id and no document to drop.
/// </para>
/// </remarks>
internal sealed class ModelSettingsSource : IModelSettingsSource
{
    private readonly ConcurrentDictionary<Document, IModelSettings> _cache = new();
    private readonly ModelSettingsStore _store = new();
    private readonly LayeredSettings _process;
    private readonly ILog _log = Log.For<ModelSettingsSource>();

    public ModelSettingsSource(LayeredSettings process) => _process = process;

    public IModelSettings For(Document document)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        return _cache.GetOrAdd(document, key =>
        {
            var read = _store.TryRead(key, out var values);

            var origin = !read ? ModelSettingsOrigin.Unreadable
                : values.Count > 0 ? ModelSettingsOrigin.Model
                : ModelSettingsOrigin.None;

            return new ModelSettings(key, _process, values, origin);
        });
    }

    public void Write(Document document, IReadOnlyDictionary<string, string?> values)
    {
        _store.Write(document, values);

        // Dropped rather than updated: the next question re-reads, which is also the only way to
        // see what the document really holds after somebody else's write.
        _cache.TryRemove(document, out _);
    }

    /// <summary>Forgets a document. Called while it still exists, on <c>DocumentClosing</c>.</summary>
    public void Forget(Document document)
    {
        if (document is not null && _cache.TryRemove(document, out _))
            _log.Debug("forgot the settings of a closing document");
    }
}
