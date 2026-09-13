using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Probe;

/// <summary>
/// The control for the availability measurement: same class, same button, same assembly as the
/// command.
/// </summary>
/// <remarks>
/// Without it the experiment has no answer. A foreign availability class that is never called can
/// mean the limit is real, or it can mean Revit simply never asked - and those look identical from
/// outside. Two buttons side by side, differing in one thing, tell them apart.
/// </remarks>
public sealed class LocalAvailability : IExternalCommandAvailability
{
    private static int _calls;
    private static string? _firstActiveAddInId;
    private static string _latestActiveAddInId = "(never asked)";

    public static int Calls => System.Threading.Volatile.Read(ref _calls);

    /// <summary>What <c>ActiveAddInId</c> said on the first call, raw.</summary>
    public static string FirstActiveAddInId => System.Threading.Volatile.Read(ref _firstActiveAddInId) ?? "(never asked)";

    /// <summary>What <c>ActiveAddInId</c> said on the latest call, raw.</summary>
    public static string LatestActiveAddInId => System.Threading.Volatile.Read(ref _latestActiveAddInId);

    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        // What Revit names as the executing add-in while it asks availability - which nobody knows,
        // because nothing is executing. Recorded for the control because GateAvailability may not
        // record anything: an Entry class carries no logic, and GateRule is never shown the
        // UIApplication. First and latest, since the answer may well change once a command has run.
        var active = DescribeActiveAddIn(applicationData);
        System.Threading.Interlocked.CompareExchange(ref _firstActiveAddInId, active, null);
        System.Threading.Volatile.Write(ref _latestActiveAddInId, active);

        if (System.Threading.Interlocked.Increment(ref _calls) == 1)
            Log.For<LocalAvailability>().Info("availability was called from the command's own assembly");

        return true;
    }

    private static string DescribeActiveAddIn(UIApplication? application)
    {
        try
        {
            var id = application?.ActiveAddInId;
            return id is null ? "(null)" : id.GetGUID().ToString();
        }
        catch (Exception error)
        {
            return "(threw " + error.GetType().Name + ")";
        }
    }
}
