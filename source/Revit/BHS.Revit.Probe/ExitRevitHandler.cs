using Autodesk.Revit.UI;

namespace BHS.Revit.Probe;

/// <summary>
/// Closes Revit from the inside, on the API thread, at Revit's own invitation.
/// </summary>
/// <remarks>
/// Closing from outside was measured and does not work: <c>CloseMainWindow</c> was honoured once
/// in four releases - on a completely idle Revit 2025 - and ignored otherwise. The supported route
/// is to post the command Revit posts to itself, and posting requires a <c>UIApplication</c>,
/// which only exists inside an API context.
/// <para>
/// Hence an external event rather than a direct call: the RPC arrives on a pool thread, and the
/// only thing a pool thread may do with the Revit API is ask to be called back on the right one.
/// The event fires when Revit next goes idle, which for a probe that opened nothing is at once.
/// </para>
/// </remarks>
internal sealed class ExitRevitHandler : IExternalEventHandler
{
    public void Execute(UIApplication application)
    {
        try
        {
            var command = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);

            if (command is null)
            {
                ProbeLog.Write("exit: ExitRevit has no command id on this release");
                return;
            }

            ProbeLog.Write("exit: posting ExitRevit");
            application.PostCommand(command);
        }
        catch (Exception error)
        {
            // Killing the process is the runner's backstop; log why it will be needed.
            ProbeLog.Write("exit: failed", error);
        }
    }

    public string GetName() => "BHS probe: exit Revit";
}
