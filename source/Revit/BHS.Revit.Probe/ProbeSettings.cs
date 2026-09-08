using System.Globalization;
using BHS.Revit.Abstractions;
using BHS.Settings;

namespace BHS.Revit.Probe;

/// <summary>
/// What a Revit-side assembly read from disk, reported from inside Revit.
/// </summary>
/// <remarks>
/// It no longer reads anything. The host does that before the probe's own startup runs, which is
/// itself part of what this reports on: the settings a feature sees are the ones the framework
/// assembled, not a second set read alongside them.
/// <para>
/// The claim being measured is still the one the framework rests on - each side reads its own
/// layered files and starts with no channel at all - and the part only a live Revit can answer is
/// where the product layer resolves to. The entry assembly here is <c>Revit.exe</c>, and so is
/// <c>AppContext.BaseDirectory</c>; if the layer came out anywhere but the add-in's own folder,
/// nothing outside the process would have noticed.
/// </para>
/// </remarks>
internal sealed class ProbeSettings
{
    private readonly LayeredSettings? _settings;
    private readonly ISettings? _view;

    public ProbeSettings(LayeredSettings? settings, IFeatureServices? services)
    {
        _settings = settings;
        _view = services?.Settings;
    }

    /// <summary>The layers, whether or not they exist, and everything they came to.</summary>
    public IDictionary<string, string> Report()
    {
        var report = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The layer that was actually read, not the static default.
            //
            // Measured, and it failed the check that exists for it: SettingsLayout.ProductDirectory
            // resolves relative to BHS.Settings.dll, and with two of our add-ins installed that is
            // whichever copy won the simple name. On three releases it was the probe's own and the
            // answer looked right; on Revit 2027 the edition's copy won, and the probe reported the
            // edition's folder as its product layer while reading its own.
            //
            // The host stopped trusting that default when it started naming its directory itself.
            // Reporting it here would have gone on being wrong quietly, which is worse than the bug
            // it was covering.
            ["settings:product"] = ProductDirectory(),
            ["settings:machine"] = SettingsLayout.MachineDirectory,
            ["settings:user"] = SettingsLayout.UserDirectory,
        };

        if (_settings is null || _view is null)
        {
            report["settings:error"] = "the host did not read any settings";
            return report;
        }

        var index = 0;

        foreach (var layer in _settings.Layers)
        {
            report["layer:" + index.ToString("D2", CultureInfo.InvariantCulture)] =
                (layer.Exists ? "read   " : "absent ") + layer.Path;
            index++;
        }

        foreach (var key in _view.Keys)
        {
            if (_view[key] is { } value)
                report["value:" + key] = value;
        }

        return report;
    }

    /// <summary>The directory the product layer was actually read from.</summary>
    /// <remarks>
    /// Taken from the layer list rather than from <c>SettingsLayout.ProductDirectory</c>, which is
    /// a static resolved relative to <c>BHS.Settings.dll</c> - and with two add-ins installed, that
    /// assembly may belong to the other one. The host names its own directory now; this reports
    /// what the host did rather than what the default would have said.
    ///
    /// Falls back to the static only when there is nothing to read from, where it is the same
    /// answer the layers would have given.
    /// </remarks>
    private string ProductDirectory()
    {
        var product = _settings?.Layers.FirstOrDefault(layer => layer.Kind == SettingsLayerKind.Product);

        if (product is null)
            return SettingsLayout.ProductDirectory;

        return System.IO.Path.GetDirectoryName(product.Path) ?? SettingsLayout.ProductDirectory;
    }
}
