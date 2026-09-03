using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Probe;

/// <summary>A command that does nothing but say it ran. It exists to hang a ribbon button on.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ProbeCommand : IExternalCommand
{
    private static int _runs;

    public static int Runs => System.Threading.Volatile.Read(ref _runs);

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        System.Threading.Interlocked.Increment(ref _runs);
        Log.For<ProbeCommand>().Info("command ran");
        return Result.Succeeded;
    }
}
