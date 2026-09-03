using System.Globalization;
using BHS.Settings;

namespace BHS.Revit.Probe;

/// <summary>
/// What a Revit-side assembly reads from disk, read from inside Revit.
/// </summary>
/// <remarks>
/// The claim being measured is the one the framework rests on: each side reads its own layered
/// configuration and starts with no channel at all, so neither waits for the other. Outside Revit
/// that is easy to believe. Inside it the interesting part is whether the product layer resolves to
/// the add-in's own folder rather than to Revit's installation directory - the entry assembly here
/// is <c>Revit.exe</c>, and so is <c>AppContext.BaseDirectory</c>.
/// <para>
/// It was also the check that caught the assembly this add-in may not carry. An earlier version read
/// its settings through <c>Microsoft.Extensions.Configuration</c> and did not load on Revit 2026 at
/// all, because DynamoForRevit had loaded <c>Configuration.Abstractions</c> 2.0.0.0 out of Revit's
/// own directory first.
/// </para>
/// <para>
/// Built once, at startup, on the API thread, and answered from a pool thread afterwards - the
/// same discipline as <see cref="ProbeFacts"/>, and for the same reason.
/// </para>
/// </remarks>
internal sealed class ProbeSettings
{
    private readonly LayeredSettings? _settings;
    private readonly string? _failure;

    public ProbeSettings(int release)
    {
        try
        {
            _settings = LayeredSettings.Read(new SettingsOptions
            {
                Side = ProcessSide.Revit,
                Release = release,
            });
        }
        catch (Exception error)
        {
            // A probe that throws out of OnStartup takes the measurement with it.
            _failure = error.GetType().Name + ": " + error.Message;
            ProbeLog.Write("settings: could not be read", error);
        }
    }

    /// <summary>The layers, whether or not they exist, and everything they came to.</summary>
    public IDictionary<string, string> Report()
    {
        var report = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["settings:product"] = SettingsLayout.ProductDirectory,
            ["settings:machine"] = SettingsLayout.MachineDirectory,
            ["settings:user"] = SettingsLayout.UserDirectory,
        };

        if (_failure is not null)
        {
            report["settings:error"] = _failure;
            return report;
        }

        var index = 0;

        foreach (var layer in _settings!.Layers)
        {
            report["layer:" + index.ToString("D2", CultureInfo.InvariantCulture)] =
                (layer.Exists ? "read   " : "absent ") + layer.Path;
            index++;
        }

        foreach (var key in _settings.Keys)
        {
            if (_settings[key] is { } value)
                report["value:" + key] = value;
        }

        return report;
    }
}
