using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace BHS.Revit.Host;

/// <summary>
/// What an edition derives from when it has no user interface.
/// </summary>
/// <remarks>
/// <para>
/// The same one class as the interface form:
/// </para>
/// <code>
/// public sealed class BhsExportApplication : RevitDbAddInApplication
/// {
///     protected override Guid AddInId =&gt; new("...");
///     protected override string Name =&gt; "BHS Export";
/// }
/// </code>
/// <para>
/// <b>What it gets is everything except the way onto the API thread.</b> Settings read from disk,
/// logging with the journal sink attached, the host registry, a document's own settings, modules -
/// all of it is in <see cref="RevitAddInHost"/> and needs nothing but a
/// <c>ControlledApplication</c>. What it does not get is a pump, because <c>ExternalEvent</c> lives
/// in <c>RevitAPIUI</c>; a module that needs one asks through
/// <see cref="Abstractions.FeatureServicesExtensions.Ui"/> and is told, in a sentence, that this
/// host has no interface.
/// </para>
/// <para>
/// Ordinary interactive Revit loads a <c>DBApplication</c> manifest as readily as an
/// <c>Application</c> one, so this form is not only for a headless engine - it is also how an
/// edition offers the part of itself that does not need a window.
/// </para>
/// <para>
/// <b>The result type has two values, not three.</b> Checked against the metadata of all four
/// releases: <c>ExternalDBApplicationResult</c> is <c>Succeeded</c> or <c>Failed</c>, with no
/// equivalent of <c>Result.Cancelled</c>. We return <c>Succeeded</c> either way, for the same reason
/// the interface form returns <c>Result.Succeeded</c> after a failed start: an add-in that refuses
/// to load is a dialog in front of nobody, while a start that failed is a line in the log.
/// </para>
/// </remarks>
public abstract class RevitDbAddInApplication : RevitAddInHost, IExternalDBApplication
{
    public ExternalDBApplicationResult OnStartup(ControlledApplication application)
    {
        // The same first statement as the interface form, and true for the same reason: Revit calls
        // this on the thread it will accept API calls from, and nothing later can find out which.
        Logging.LogRouter.PrimaryThreadId = Environment.CurrentManagedThreadId;

        try
        {
            Start(application);
        }
        catch (Exception error)
        {
            ReportFailedStart(error);
        }

        return ExternalDBApplicationResult.Succeeded;
    }

    public ExternalDBApplicationResult OnShutdown(ControlledApplication application)
    {
        try
        {
            Stop(application);
        }
        catch (Exception error)
        {
            ReportFailedStop(error);
        }

        return ExternalDBApplicationResult.Succeeded;
    }
}
