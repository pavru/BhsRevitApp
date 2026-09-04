using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Probe;

/// <summary>
/// Presses the probe's own ribbon button, from inside Revit.
/// </summary>
/// <remarks>
/// Needed because two questions have no other answer: whether a command Revit constructed finds its
/// host, and what <c>ActiveAddInId</c> returns from inside one. Neither is reachable without Revit
/// actually running the command.
/// <para>
/// Behind the same switch as showing the tab, and for the same measured reason: bringing the ribbon
/// forward during a model load makes Revit ask whether to cancel the operation, which in an
/// unattended sweep is a hang. Asked for deliberately, it is a measurement; asked for by default, it
/// is a trap.
/// </para>
/// </remarks>
internal sealed class PressButtonHandler : IExternalEventHandler
{
    /// <summary>Command id spellings tried in order. Revit's form for add-in buttons is undocumented.</summary>
    private static readonly string[] Candidates =
    {
        "CustomCtrl_%CustomCtrl_%Add-Ins%BHS Probe%BHS.Probe.Ping",
        "CustomCtrl_%Add-Ins%BHS Probe%BHS.Probe.Ping",
    };

    public void Execute(UIApplication application)
    {
        var log = Log.For<PressButtonHandler>();

        foreach (var candidate in Candidates)
        {
            try
            {
                var id = RevitCommandId.LookupCommandId(candidate);

                if (id is null)
                    continue;

                log.Info("pressing '{0}'", candidate);
                application.PostCommand(id);
                return;
            }
            catch (Exception error)
            {
                log.Warn(error, "could not press '{0}'", candidate);
            }
        }

        log.Warn("no command id matched the ping button; tried {0} spellings", Candidates.Length);
    }

    public string GetName() => "BHS probe: press the ping button";
}
