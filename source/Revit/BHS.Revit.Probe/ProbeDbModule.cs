using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe;

/// <summary>
/// A module in the DB half, so that the composition path is exercised and not only the settings.
/// </summary>
/// <remarks>
/// <c>IFeatureModule</c> takes the narrow services on purpose - a module can be brought up by either
/// host form, and the one that needs the API thread says so by calling <c>Ui()</c>. This one does
/// not need it, which is what a module reachable from a <c>DBApplication</c> add-in looks like.
/// </remarks>
internal sealed class ProbeDbModule : IFeatureModule
{
    public static bool Started;
    public static string SectionSeen = "(none)";

    private ILog _log = Log.For<ProbeDbModule>();

    public void Start(IFeatureServices services)
    {
        Started = true;
        _log = services.Log;

        // Its own section, narrowed by the host: two modules must not be able to argue over a key.
        SectionSeen = services.Settings["Marker"] ?? "(none)";
        _log.Info("the DB half's module started");
    }

    /// <summary>Written to the log because that is the only place it can be seen from.</summary>
    /// <remarks>
    /// Stopping happens while Revit is leaving, when there is no channel left to ask over. A static
    /// nobody can read afterwards would prove nothing.
    /// </remarks>
    public void Stop() => _log.Info("the DB half's module stopped");
}
