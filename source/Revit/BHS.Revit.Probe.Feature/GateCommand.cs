using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe.Feature;

/// <summary>
/// The command behind the probe's Entry button: says it ran, which host it was handed, and what Revit
/// said was executing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same command as <see cref="PingCommand"/>, reached a different way.</b> Ping is constructed
/// through an entry point in the probe's own assembly and finds its host by that assembly. This one is
/// constructed through <c>BHS.Revit.Probe.Entry</c>, which belongs to no edition, and its entry point
/// finds the host by the feature it names - the probe's <c>Modules</c> list declares
/// <c>ProbeGateFeature</c>, and exactly one host with a user interface should answer. Ping stays as it
/// is, as the control.
/// </para>
/// <para>
/// Recorded through the environment rather than a field, for Ping's reason: the probe reads the answer
/// without referencing this assembly, which is the very load the measurement is about. The host id and
/// the raw id go in first and the run count last, so that a reader waiting on the count never sees it
/// ahead of the values it announces.
/// </para>
/// </remarks>
public sealed class GateCommand : IFeatureCommand
{
    public const string RanVariable = "BHS_PROBE_GATE_RAN";
    public const string ServicesVariable = "BHS_PROBE_GATE_SERVICES";
    public const string ActiveAddInVariable = "BHS_PROBE_GATE_ACTIVE_ADDIN";

    public Result Execute(IUiFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message)
    {
        var active = ActiveAddIn.Describe(data);

        Environment.SetEnvironmentVariable(ActiveAddInVariable, active);
        Environment.SetEnvironmentVariable(ServicesVariable, services.Revit.AddInId.ToString());

        var count = int.TryParse(Environment.GetEnvironmentVariable(RanVariable), out var previous) ? previous : 0;
        Environment.SetEnvironmentVariable(RanVariable, (count + 1).ToString());

        services.Log.Info("gate ran on Revit {0}, host {1}, Revit names {2} as executing",
            services.Revit.Release, services.Revit.AddInId, active);

        return Result.Succeeded;
    }
}
