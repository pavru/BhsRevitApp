using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe.Feature;

/// <summary>
/// What a feature writes: a command that is given what it needs.
/// </summary>
/// <remarks>
/// It implements <see cref="IFeatureCommand"/> rather than <c>IExternalCommand</c>, and the
/// difference is the whole point of the arrangement: Revit constructs an external command from a
/// class name and can pass it nothing, while this one is constructed by the entry point, which
/// already holds the host.
/// <para>
/// It records what it saw through the environment rather than a field, so that the edition can read
/// the answer without referencing this assembly - which is exactly the reference the measurement is
/// about.
/// </para>
/// </remarks>
public sealed class PingCommand : IFeatureCommand
{
    public const string RanVariable = "BHS_PROBE_PING_RAN";
    public const string ServicesVariable = "BHS_PROBE_PING_SERVICES";

    public Result Execute(IFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message)
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable(RanVariable), out var previous) ? previous : 0;
        Environment.SetEnvironmentVariable(RanVariable, (count + 1).ToString());

        // The question the registry exists to answer: did a command Revit built itself find its own
        // host? Recorded as what it found, not as a yes, so a wrong answer is visible as a wrong
        // answer rather than as a silence.
        Environment.SetEnvironmentVariable(ServicesVariable, services.Revit.AddInId.ToString());

        services.Log.Info("ping ran on Revit {0}, add-in {1}", services.Revit.Release, services.Revit.AddInId);
        return Result.Succeeded;
    }
}
