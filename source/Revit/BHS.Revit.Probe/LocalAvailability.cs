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

    public static int Calls => System.Threading.Volatile.Read(ref _calls);

    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        if (System.Threading.Interlocked.Increment(ref _calls) == 1)
            Log.For<LocalAvailability>().Info("availability was called from the command's own assembly");

        return true;
    }
}
