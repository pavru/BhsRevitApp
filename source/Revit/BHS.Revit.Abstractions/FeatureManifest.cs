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

    /// <summary>Empty means Revit's own Add-Ins tab, which is where a button goes unless asked.</summary>
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

    private FeatureManifest(string path, string assembly, IReadOnlyList<FeatureButton> buttons)
    {
        Path = path;
        Assembly = assembly;
        Buttons = buttons;
    }

    public string Path { get; }

    /// <summary>The assembly file the buttons name, as written at build time.</summary>
    public string Assembly { get; }

    public IReadOnlyList<FeatureButton> Buttons { get; }

    /// <summary>Every manifest in a directory, in a stable order.</summary>
    /// <remarks>
    /// Sorted by file name so that two editions in one folder produce the same ribbon on every start.
    /// Panels appear in the order Revit is first asked for them, and "whatever the file system said
    /// today" is not an order.
    /// </remarks>
    public static IReadOnlyList<FeatureManifest> ReadDirectory(string directory, Action<string, Exception>? onError = null)
    {
        var manifests = new List<FeatureManifest>();

        if (!Directory.Exists(directory))
            return manifests;

        var files = Directory.GetFiles(directory, "*" + Extension);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                manifests.Add(Read(file));
            }
            catch (Exception error)
            {
                onError?.Invoke(file, error);
            }
        }

        return manifests;
    }

    public static FeatureManifest Read(string path)
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
            });
        }

        return new FeatureManifest(path, Text(values, "assembly"), buttons);
    }

    private static string Text(IDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && value is not null ? value : string.Empty;

    private static int Number(IDictionary<string, string?> values, string key) =>
        int.TryParse(Text(values, key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
