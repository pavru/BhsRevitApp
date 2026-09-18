using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Probe;

/// <summary>
/// Presses one of the probe's own ribbon buttons, from inside Revit.
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
/// <para>
/// One instance per button, because an external event can only be created during startup and carries
/// its handler for the session - so the button is fixed when the event is made, not chosen per raise.
/// </para>
/// </remarks>
internal sealed class PressButtonHandler : IExternalEventHandler
{
    /// <summary>Command id spellings for the ping button, tried in order.</summary>
    /// <remarks>
    /// Revit's form for add-in buttons is undocumented. The button moved to a tab of its own, so its id
    /// did too - the id encodes the tab and the panel it sits on. Both spellings of both placements are
    /// kept, and the old ones cost nothing to try.
    /// </remarks>
    public static readonly string[] Ping =
    {
        "CustomCtrl_%CustomCtrl_%BHS%Probe feature%BHS.Probe.Ping",
        "CustomCtrl_%BHS%Probe feature%BHS.Probe.Ping",
        "CustomCtrl_%CustomCtrl_%Add-Ins%BHS Probe%BHS.Probe.Ping",
        "CustomCtrl_%Add-Ins%BHS Probe%BHS.Probe.Ping",
    };

    /// <summary>Command id spellings for the Entry button, tried in order.</summary>
    /// <remarks>
    /// <b>Only the placement the edition's tab gives it</b>, and that is deliberate rather than tidy. The
    /// Entry manifest names no tab; the probe names <c>BHS</c> through <c>RibbonTab</c>. Were the
    /// Add-Ins spelling tried as well, a host that ignored the edition's tab would still get its button
    /// pressed, and the one symptom of that defect would be pressed away.
    /// </remarks>
    public static readonly string[] Gate =
    {
        "CustomCtrl_%CustomCtrl_%" + ProbeApplication.OwnTabName + "%" + ProbeApplication.OwnPanelTitle + "%BHS.Probe.Gate",
        "CustomCtrl_%" + ProbeApplication.OwnTabName + "%" + ProbeApplication.OwnPanelTitle + "%BHS.Probe.Gate",
    };

    /// <summary>
    /// The pane's toggle, which sits on the Entry button's panel: the same two spellings as Gate. Pressed
    /// again only when the previous press did not reach PaneEntryPoint - a second press that did would
    /// hide what the first showed.
    /// </summary>
    public static readonly string[] PaneToggle =
    {
        "CustomCtrl_%CustomCtrl_%" + ProbeApplication.OwnTabName + "%" + ProbeApplication.OwnPanelTitle + "%BHS.Probe.PaneToggle",
        "CustomCtrl_%" + ProbeApplication.OwnTabName + "%" + ProbeApplication.OwnPanelTitle + "%BHS.Probe.PaneToggle",
    };

    /// <summary>The outcome of a press for which no spelling named a command id.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>The prefix of an outcome that reached <c>PostCommand</c>.</summary>
    public const string Posted = "posted ";

    /// <summary>
    /// The outcome of a press whose command id was found and which Revit refused to post. Measured on 2024
    /// and 2025: "Revit does not support more than one command are posted", with the previous button's
    /// command still queued. A busy Revit, not a missing button - so the runner presses again.
    /// </summary>
    public const string Refused = "refused ";

    private readonly string _button;
    private readonly string[] _candidates;

    private string _lastOutcome = string.Empty;
    private int _presses;
    private int _reportedUnmatched;

    public PressButtonHandler(string button, string[] candidates)
    {
        _button = button;
        _candidates = candidates;
    }

    /// <summary>
    /// What the last press came to, read by the channel from a pool thread.
    /// </summary>
    /// <remarks>
    /// <b>Recorded, so that "posted and never ran" is not read as "never posted".</b> A posted command
    /// that Revit drops leaves no trace anywhere, and neither does a refusal Revit raises before our code
    /// runs; with the outcome beside the run counter the runner can tell a press that never left from
    /// one that left and went nowhere, and stop pressing a button that has no command id at all.
    /// </remarks>
    public string LastOutcome => Volatile.Read(ref _lastOutcome);

    public void Execute(UIApplication application)
    {
        var log = Log.For<PressButtonHandler>();
        var press = Interlocked.Increment(ref _presses);

        foreach (var candidate in _candidates)
        {
            try
            {
                var id = RevitCommandId.LookupCommandId(candidate);

                if (id is null)
                    continue;

                // Info once, Debug after. The runner presses again while nothing runs, every second or
                // two for up to a minute and a half, and a polling check that writes a line per
                // iteration is how a log once filled with a hundred and fifty of them.
                if (press == 1)
                    log.Info("pressing '{0}'", candidate);
                else
                    log.Debug("pressing '{0}' again, press {1}", candidate, press);

                application.PostCommand(id);
                Volatile.Write(ref _lastOutcome, Posted + candidate);
                return;
            }
            catch (Exception error)
            {
                log.Warn(error, "could not press '{0}'", candidate);
                Volatile.Write(ref _lastOutcome, Refused + error.GetType().Name);
                return;
            }
        }

        Volatile.Write(ref _lastOutcome, Unmatched);

        // Once per handler, and below Warning on repeats. For the Entry button this is the designed red
        // path - a host that ignored the edition's tab puts the button where no spelling here looks - and
        // Warning and above from the API thread also go to the Revit journal, which is no place for a line
        // repeated at every press.
        if (Interlocked.Exchange(ref _reportedUnmatched, 1) == 0)
            log.Warn("no command id matched the button {0}; tried {1} spellings", _button, _candidates.Length);
        else
            log.Debug("still no command id for the button {0}", _button);
    }

    public string GetName() => "BHS probe: press the button " + _button;
}
