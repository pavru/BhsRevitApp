using System.IO;
using BHS.Settings;

namespace BHS.Revit.Abstractions;

/// <summary>One button, as an edition declared it and the SDK wrote it down.</summary>
/// <remarks>
/// <para>
/// Every field is a string, and the class names especially so. A <c>Type</c> here would mean the type
/// had been loaded to put it there, which loads the feature assembly at ribbon-build time - measured,
/// and the whole reason a manifest exists rather than a list in code.
/// </para>
/// <para>
/// Settable rather than <c>init</c>, and that is the TFM talking: <c>init</c> compiles on
/// <c>net48-revit2024</c> only with an <c>IsExternalInit</c> polyfill, and a polyfill is an assembly
/// - or a shim - in the AppDomain Revit 2024 shares with every other vendor. The SDK opens the
/// syntax; it does not conjure the runtime type. Not worth one keyword.
/// </para>
/// <para>
/// Not called <c>RibbonButton</c>: <c>Autodesk.Revit.UI</c> has one of those, and a type of ours
/// sharing a simple name with a type of Revit's is a compiler error in the lucky case and the wrong
/// type in the unlucky one. The same instinct as the <c>BHS.</c> prefix on every assembly.
/// </para>
/// </remarks>
public sealed class FeatureButton
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The tab the button goes on - in an edition's own manifest, and only there.</summary>
    /// <remarks>
    /// In an edition's own manifest - and the probe's - empty means Revit's own Add-Ins tab, which is
    /// where a button goes unless asked. In a feature's Entry manifest the value is ignored: the edition
    /// chooses the tab, through <c>RevitAddInApplication.RibbonTab</c>, and a tab written there fails
    /// the build with <c>RVTENT003</c> and is logged and overruled if it arrives anyway.
    /// </remarks>
    public string Tab { get; set; } = string.Empty;

    public string Panel { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string ToolTip { get; set; } = string.Empty;

    public string LongDescription { get; set; } = string.Empty;

    /// <summary>The class Revit constructs. Must carry <c>[Transaction]</c>; checked by RefCheck.</summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>Optional, and must live in the same assembly - checked, because it is a dialog.</summary>
    public string AvailabilityClassName { get; set; } = string.Empty;

    /// <summary>Where it sits on its panel. Absent sorts first, then by this, then by name.</summary>
    public int Order { get; set; }

    /// <summary>
    /// The name of the pane in the same manifest this button shows and hides, or empty for an ordinary
    /// command. Its class then derives from <c>PaneEntryPoint</c>; RefCheck's <c>RVTPAN004</c> checks both
    /// directions.
    /// </summary>
    public string Pane { get; set; } = string.Empty;
}

/// <summary>One dockable pane, as a feature declared it and the SDK wrote it down.</summary>
/// <remarks>
/// <para>
/// Strings for the reason every field of <see cref="FeatureButton"/> is a string: the content class
/// lives in the feature's assembly, which must stay unloaded until Revit first asks for the pane, and a
/// <c>Type</c> here would have loaded it to put it here.
/// </para>
/// <para>
/// The defaults are the ones CLAUDE.md gives, each with its reason there: hidden until asked, docked on
/// the right at the width of Revit's Properties palette, and dismissed inside Revit's editors.
/// </para>
/// </remarks>
public sealed class FeaturePane
{
    public const int DefaultMinimumWidth = 320;

    public string Name { get; set; } = string.Empty;

    /// <summary>The GUID Revit keeps the pane's place in the dock layout under. Fixed forever.</summary>
    public Guid Id { get; set; }

    /// <summary>The caption. Fixed at registration on 2024-2026: <c>DockablePane.SetTitle</c> exists only on 2027.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The simple name of the assembly the content class lives in, beside the manifest.</summary>
    public string ContentAssembly { get; set; } = string.Empty;

    /// <summary>The full name of an <c>IPaneContent</c> with a public parameterless constructor.</summary>
    public string ContentClassName { get; set; } = string.Empty;

    /// <summary>Left, Right, Top or Bottom. Empty means Right.</summary>
    public string DockPosition { get; set; } = string.Empty;

    /// <summary>In device-independent units; zero means <see cref="DefaultMinimumWidth"/>.</summary>
    public int MinimumWidth { get; set; }

    /// <summary>In device-independent units; zero means Revit's own default.</summary>
    public int MinimumHeight { get; set; }

    /// <summary>Dismiss or KeepAlive. Empty means Dismiss.</summary>
    public string EditorInteraction { get; set; } = string.Empty;

    /// <summary>Whether Revit shows it the very first time it is registered. False unless the manifest says true.</summary>
    public bool VisibleByDefault { get; set; }
}

/// <summary>
/// What one assembly contributes to the ribbon, read from the file beside it.
/// </summary>
/// <remarks>
/// <para>
/// Read with <see cref="JsonSettings"/> - the same hand-written reader the layered settings use, which
/// flattens any JSON into <c>key -> string</c> with <c>:</c> between levels and arrays indexed by
/// ordinal. That is not thrift for its own sake: a manifest is read inside Revit, and
/// <c>System.Text.Json</c> is an assembly Revit ships in no version, which means nobody pins it and
/// every second vendor carries one. Reusing the reader costs zero assemblies.
/// </para>
/// <para>
/// A manifest that cannot be read is reported and skipped. One edition's broken file must not cost
/// another edition its ribbon, and inside Revit there is nobody to show an exception to.
/// </para>
/// </remarks>
public sealed class FeatureManifest
{
    public const string Extension = ".features.json";

    /// <summary>
    /// How a feature's Entry manifest ends: <c>&lt;P&gt;.Entry.features.json</c>, beside
    /// <c>&lt;P&gt;.Entry.dll</c>.
    /// </summary>
    /// <remarks>
    /// The name is the contract, because the file is all a host can look at without loading anything.
    /// It arrives in an edition's folder as a related file of the Entry assembly the edition
    /// references, so it is named after that assembly by the same SDK task that names every manifest.
    /// </remarks>
    public const string EntryExtension = ".Entry" + Extension;

    /// <summary>
    /// The assembly an edition has to have declared a module from before an Entry manifest counts:
    /// <c>&lt;P&gt;.Declaration</c>.
    /// </summary>
    public const string DeclarationSuffix = ".Declaration";

    private FeatureManifest(string path, string assembly, IReadOnlyList<FeatureButton> buttons, IReadOnlyList<FeaturePane> panes)
    {
        Path = path;
        Assembly = assembly;
        Buttons = buttons;
        Panes = panes;
        EntryFeature = EntryFeatureOf(path);
    }

    public string Path { get; }

    /// <summary>The assembly file the buttons name, as written at build time.</summary>
    public string Assembly { get; }

    public IReadOnlyList<FeatureButton> Buttons { get; }

    /// <summary>The dockable panes, from manifest version 2. Empty in a version 1 manifest.</summary>
    public IReadOnlyList<FeaturePane> Panes { get; }

    /// <summary>The culture whose overlay was laid over the neutral text, or empty for none.</summary>
    public string OverlayCulture { get; private set; } = string.Empty;

    /// <summary>How many strings the overlay replaced. Zero when there was none, or it translated nothing here.</summary>
    public int OverlaidStrings { get; private set; }

    /// <summary>
    /// The <c>P</c> of <c>&lt;P&gt;.Entry.features.json</c>, or empty when this is not a feature's
    /// Entry manifest - an edition's own, or the probe's.
    /// </summary>
    public string EntryFeature { get; }

    /// <summary>Whether this is a feature's Entry manifest rather than an edition's own.</summary>
    public bool IsEntry => EntryFeature.Length > 0;

    /// <summary>The <c>P</c> of a path named <c>&lt;P&gt;.Entry.features.json</c>, or empty.</summary>
    public static string EntryFeatureOf(string path)
    {
        var name = System.IO.Path.GetFileName(path ?? string.Empty);

        if (!name.EndsWith(EntryExtension, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return name.Substring(0, name.Length - EntryExtension.Length);
    }

    /// <summary>Every manifest in a directory, in a stable order.</summary>
    /// <remarks>
    /// Sorted by file name so that two editions in one folder produce the same ribbon on every start.
    /// Panels appear in the order Revit is first asked for them, and "whatever the file system said
    /// today" is not an order.
    /// </remarks>
    /// <param name="directory">The folder to read.</param>
    /// <param name="onError">Told about a manifest that could not be read, which is then skipped.</param>
    /// <param name="culture">
    /// Revit's language as the host maps it - <c>RevitLanguage.Current</c> - or null for the neutral text.
    /// </param>
    public static IReadOnlyList<FeatureManifest> ReadDirectory(
        string directory,
        Action<string, Exception>? onError = null,
        System.Globalization.CultureInfo? culture = null)
    {
        var manifests = new List<FeatureManifest>();

        if (!Directory.Exists(directory))
            return manifests;

        // Filtered by the ending as well as globbed. A culture overlay, <P>.features.ru-RU.json, does not
        // end in .features.json and the glob already leaves it out; the second test is there so that a
        // change to the pattern cannot quietly start reading overlays as manifests of their own, with
        // no assembly and nothing but text.
        var files = Directory.GetFiles(directory, "*" + Extension)
            .Where(file => file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                manifests.Add(Read(file, culture));
            }
            catch (Exception error)
            {
                onError?.Invoke(file, error);
            }
        }

        return manifests;
    }

    /// <summary>Reads one manifest, and lays the overlay for <paramref name="culture"/> over it if there is one.</summary>
    public static FeatureManifest Read(string path, System.Globalization.CultureInfo? culture = null)
    {
        var values = JsonSettings.Read(path);
        var buttons = new List<FeatureButton>();

        // Walked by ordinal until a name runs out, which is what the flat form gives us: the reader
        // indexes arrays by position, so buttons:0:name, buttons:1:name and so on.
        for (var index = 0; ; index++)
        {
            var prefix = "buttons:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            var name = Text(values, prefix + "name");

            if (name.Length == 0)
                break;

            buttons.Add(new FeatureButton
            {
                Name = name,
                Tab = Text(values, prefix + "tab"),
                Panel = Text(values, prefix + "panel"),
                Text = Text(values, prefix + "text"),
                ToolTip = Text(values, prefix + "toolTip"),
                LongDescription = Text(values, prefix + "longDescription"),
                ClassName = Text(values, prefix + "className"),
                AvailabilityClassName = Text(values, prefix + "availabilityClassName"),
                Order = Number(values, prefix + "order"),
                Pane = Text(values, prefix + "pane"),
            });
        }

        var manifest = new FeatureManifest(path, Text(values, "assembly"), buttons, ReadPanes(values));

        if (culture is not null)
            manifest.Overlay(culture);

        return manifest;
    }

    /// <summary>The overlay for a culture: <c>&lt;P&gt;.features.&lt;culture&gt;.json</c> beside <c>&lt;P&gt;.features.json</c>.</summary>
    public static string OverlayPath(string manifestPath, string culture) =>
        manifestPath.Substring(0, manifestPath.Length - ".json".Length) + "." + culture + ".json";

    /// <summary>
    /// Replaces the neutral text with the culture's, item by item and field by field, where it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>By item name, never by position.</b> The SDK writes an overlay as objects keyed by name for this
    /// reason: the reader flattens arrays by ordinal, and a sparse overlay matched by position would put one
    /// button's text on another the day an item is added in the middle.
    /// </para>
    /// <para>
    /// <b>An overlay that cannot be read costs the translation, not the manifest.</b> It is text laid over
    /// text; the neutral strings are complete by construction, so the buttons still stand, in English, and
    /// the manifest reports no overlay culture - which the host logs.
    /// </para>
    /// <para>
    /// Only the culture's own file: <c>ru-RU</c> does not fall back to <c>ru</c>. The SDK writes exactly the
    /// cultures in <c>RevitDeclarationCultures</c>, and the host asks exactly the ones <c>RevitLanguage</c>
    /// maps, both full names - a parent chain would be a second source of truth about which exist.
    /// </para>
    /// </remarks>
    private void Overlay(System.Globalization.CultureInfo culture)
    {
        var overlay = OverlayPath(Path, culture.Name);

        if (!File.Exists(overlay))
            return;

        IDictionary<string, string?> values;

        try
        {
            values = JsonSettings.Read(overlay);
        }
        catch (Exception)
        {
            return;
        }

        var count = 0;

        foreach (var button in Buttons)
        {
            var prefix = "buttons:" + button.Name + ":";

            button.Text = Over(values, prefix + "text", button.Text, ref count);
            button.ToolTip = Over(values, prefix + "toolTip", button.ToolTip, ref count);
            button.LongDescription = Over(values, prefix + "longDescription", button.LongDescription, ref count);
        }

        foreach (var pane in Panes)
            pane.Title = Over(values, "panes:" + pane.Name + ":title", pane.Title, ref count);

        OverlayCulture = culture.Name;
        OverlaidStrings = count;
    }

    private static string Over(IDictionary<string, string?> values, string key, string neutral, ref int count)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return neutral;

        count++;
        return value;
    }

    /// <summary>The panes, walked the same way as the buttons.</summary>
    /// <remarks>
    /// An id that does not parse is kept as <see cref="Guid.Empty"/> rather than failing the whole
    /// manifest: the SDK refuses to write one (<c>RVTPAN011</c>), so this is a hand-edited file, and the
    /// buttons beside it should still stand. The host refuses that pane by name.
    /// </remarks>
    private static IReadOnlyList<FeaturePane> ReadPanes(IDictionary<string, string?> values)
    {
        var panes = new List<FeaturePane>();

        for (var index = 0; ; index++)
        {
            var prefix = "panes:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            var name = Text(values, prefix + "name");

            if (name.Length == 0)
                break;

            Guid.TryParse(Text(values, prefix + "id"), out var id);

            panes.Add(new FeaturePane
            {
                Name = name,
                Id = id,
                Title = Text(values, prefix + "title"),
                ContentAssembly = Text(values, prefix + "contentAssembly"),
                ContentClassName = Text(values, prefix + "contentClassName"),
                DockPosition = Text(values, prefix + "dockPosition"),
                MinimumWidth = Number(values, prefix + "minimumWidth"),
                MinimumHeight = Number(values, prefix + "minimumHeight"),
                EditorInteraction = Text(values, prefix + "editorInteraction"),
                VisibleByDefault = string.Equals(Text(values, prefix + "visibleByDefault"), "true", StringComparison.OrdinalIgnoreCase),
            });
        }

        return panes;
    }

    private static string Text(IDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && value is not null ? value : string.Empty;

    private static int Number(IDictionary<string, string?> values, string key) =>
        int.TryParse(Text(values, key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
