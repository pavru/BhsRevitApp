using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe.Declaration;

/// <summary>
/// The feature the probe's Entry button belongs to, declared so that the host honours its manifest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Listing it is the whole job.</b> The host builds a feature's Entry manifest only when the
/// edition's <c>Modules</c> list names a module from <c>&lt;P&gt;.Declaration</c>, and a command
/// deriving from <c>CommandEntryPoint&lt;TFeature, TCommand&gt;</c> finds its host through the same
/// list. So this module does nothing but exist, and say so once on the way up and once on the way down
/// - the second line is the only trace of its stop, because the channel is gone by then.
/// </para>
/// <para>
/// It lives here and not in the probe for the reason a real feature's module lives in its declaration:
/// the ribbon builder matches the manifest <c>BHS.Revit.Probe.Entry.features.json</c> against the
/// assembly <c>BHS.Revit.Probe.Declaration</c>, by name.
/// </para>
/// </remarks>
public sealed class ProbeGateFeature : IFeatureModule
{
    private ILog _log = Log.For<ProbeGateFeature>();

    public void Start(IFeatureServices services)
    {
        _log = services.Log;
        _log.Info("the gate feature started, so the Entry manifest beside the probe is declared");
    }

    public void Stop() => _log.Info("the gate feature stopped");
}
