using System.Collections.Concurrent;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;

namespace BHS.Revit.Abstractions;

/// <summary>
/// The command behind a button that shows and hides a dockable pane. A feature's Entry assembly
/// derives one empty, sealed class per pane from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-generic, and the class name is the key.</b> Revit tells a command nothing about which button
/// was pressed, so one class cannot serve two panes; and a generic name on a button Revit 2024 refuses
/// (measured). So the Entry assembly carries one empty class per pane, and this base finds its pane by
/// that class's own name in <see cref="PaneRegistry"/>, where the host put it at startup from the
/// manifest. The pane's GUID therefore exists once, in the feature's project file - not as a literal
/// there and a second one in code.
/// </para>
/// <para>
/// A type argument would buy nothing: when the edition does not declare the feature, the host builds
/// neither the pane nor its button, so there is no press to explain. And with no type argument, the
/// press can load nothing beyond this assembly - <c>RVTENT002</c> has nothing to check.
/// </para>
/// <para>
/// <b>It toggles</b>, the way Revit's own Properties button does - decision of the owner. The derived
/// class carries <c>[Transaction(TransactionMode.ReadOnly)]</c>: showing a pane touches no document,
/// and <c>RVTRIB003</c> still requires the attribute on the class Revit constructs.
/// </para>
/// <para>
/// <b>And it keeps what was selected</b>, through <see cref="PaneSelectionKeeper"/>: pressing it cost the
/// selection, measured by hand on 2026-09-20. Here rather than in any one pane, so that every pane that
/// ever gets a button is opened by the same gesture without paying for it.
/// </para>
/// </remarks>
public abstract class PaneEntryPoint : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var log = Log.For(EntryPoints.LogCategory);
        var type = GetType();
        var pane = PaneRegistry.Find(type);

        if (pane is null)
        {
            log.Error("no dockable pane is registered for {0}; the add-in did not finish starting, or the pane was refused - see the lines about panes at startup",
                type.FullName);

            message = "This pane is not available: its add-in did not register it. See the log.";
            return Result.Failed;
        }

        // Read before the pane is touched: here is an API context, and what becomes of this reading is the
        // whole of PaneSelectionKeeper. Outside the try, because a press that fails costs the selection too.
        var selection = PaneSelectionKeeper.Take(commandData.Application, pane, log);

        try
        {
            pane.RecordToggle();

            var dockable = commandData.Application.GetDockablePane(new DockablePaneId(pane.Id));

            if (dockable.IsShown())
            {
                dockable.Hide();
                log.Info("pane {0} hidden by its button", pane.Name);
            }
            else
            {
                dockable.Show();
                log.Info("pane {0} shown by its button", pane.Name);
            }

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException error)
        {
            // GetDockablePane throws for a pane "not created yet", by its documentation. When that can
            // happen for a registered pane is not measured; the answer is a sentence, not a dialog.
            log.Error(error, "pane {0} ({1}) could not be reached", pane.Name, pane.Id);

            message = "This pane is not ready yet. Try again in a moment; if it persists, see the log.";
            return Result.Failed;
        }
        catch (Exception error)
        {
            log.Error(error, "pane {0} ({1}) could not be toggled", pane.Name, pane.Id);

            message = error.Message;
            return Result.Failed;
        }
        finally
        {
            selection.PutBack();
        }
    }
}

/// <summary>
/// What was selected when a pane's button was pressed, and putting it back when the press costs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why at all.</b> Measured by hand on a live Revit 2026 on 2026-09-20: an element selected, the pane's
/// button pressed, the pane opens - and nothing is selected any more. Opening a pane to look at the element
/// in front of you is the most ordinary gesture there is, and it wiped the thing it was opened for. Why a
/// command costs the selection is Revit's; that a button of ours must not is ours.
/// </para>
/// <para>
/// <b>Which side of the command's return Revit clears on is not measured, so this does not guess.</b> The
/// selection is read at entry and again just before the command returns - the second reading is what answers
/// the question, and both go onto <see cref="RegisteredPane"/> for the probe to report. Then both worlds are
/// covered: gone already, and it is put back here, where the API context is free and the pane never sees the
/// emptiness; gone afterwards, and it is put back by the pump, whose external event runs on the first idle
/// after the command has returned. Whichever acts, the other finds nothing to do and says so.
/// </para>
/// <para>
/// <b>It only ever fills an emptiness it may have caused.</b> A selection standing when the pump runs belongs
/// to somebody - a person quick enough to click in that gap, or another add-in - and is left alone. Ids whose
/// elements have gone are dropped; and a document that is no longer the one the press was made in stops the
/// restore altogether, since the same id in another document is another element.
/// </para>
/// <para>
/// <b>It holds a <c>Document</c> for the length of one press</b>, which nothing else outside the pump does,
/// and only to compare identity - guarded by <c>IsValidObject</c>, and never read from. The alternatives are
/// a title, which is not identity, or no check at all, which would move a selection into a document that
/// never had it.
/// </para>
/// </remarks>
internal sealed class PaneSelectionKeeper
{
    private static readonly IList<ElementId> None = new List<ElementId>();

    private readonly RegisteredPane _pane;
    private readonly ILog _log;
    private readonly UIApplication _application;
    private readonly Document? _document;
    private readonly IList<ElementId> _ids;

    private PaneSelectionKeeper(RegisteredPane pane, ILog log, UIApplication application, Document? document, IList<ElementId> ids)
    {
        _pane = pane;
        _log = log;
        _application = application;
        _document = document;
        _ids = ids;
    }

    /// <summary>Reads what is selected now. On the API thread, inside the command.</summary>
    public static PaneSelectionKeeper Take(UIApplication application, RegisteredPane pane, ILog log)
    {
        Document? document = null;
        var ids = None;

        try
        {
            if (application.ActiveUIDocument is { } view)
            {
                document = view.Document;
                ids = view.Selection.GetElementIds().ToList();
            }
        }
        catch (Exception error)
        {
            // Not fatal to the press: the pane still opens, and the selection is simply not kept.
            log.Warn(error, "pane {0}: what was selected could not be read, so a press cannot put it back", pane.Name);
        }

        pane.RecordSelectionAtPress(Spell(ids));
        return new PaneSelectionKeeper(pane, log, application, document, ids);
    }

    /// <summary>Reads the selection again, puts it back if it has already gone, and asks the pump for later.</summary>
    public void PutBack()
    {
        if (_ids.Count == 0)
        {
            _pane.RecordSelectionRestore("nothing was selected when the button was pressed");
            return;
        }

        var standing = Selected(_application);
        _pane.RecordSelectionAfterPress(Spell(standing));

        var inCommand = Same(standing, _ids)
            ? "the selection was still there when the command returned"
            : "the selection was gone when the command returned, and the command " + Restore(_application);

        Later(inCommand);
    }

    /// <summary>Asks the pump to look again once the command has returned and Revit is idle.</summary>
    private void Later(string inCommand)
    {
        if (HostRegistry.Find(_pane.AddInId) is not IUiFeatureServices services)
        {
            // A pane is registered by a host with an interface, so this is not reachable by design; said
            // out loud rather than assumed, because a silent half-restore is exactly this defect again.
            _pane.RecordSelectionRestore(inCommand + "; the pump could not be asked: no host with an interface is registered under this pane's add-in id");
            return;
        }

        // Written before the pump is asked, and overwritten when it answers: a pump closed by a Revit on
        // its way out never runs this work, and an empty record would read as a button never pressed.
        _pane.RecordSelectionRestore(inCommand + "; the pump has been asked to look again");

        services.Pump.Post("pane button: put back what was selected", session =>
        {
            var application = session.Application;
            var standing = Selected(application);

            var outcome = Same(standing, _ids)
                ? "the pump found it standing"
                : standing.Count > 0
                    ? "another selection stands, left alone"
                    : "the pump " + Restore(application);

            _pane.RecordSelectionRestore(inCommand + "; " + outcome);
        });
    }

    /// <returns>A sentence saying what was done, for the record the probe reads.</returns>
    private string Restore(UIApplication? application)
    {
        try
        {
            if (application?.ActiveUIDocument is not { } view)
                return "found no active document to put it back into";

            if (_document is not { IsValidObject: true } || view.Document is not { } now || !now.Equals(_document))
                return "did not put it back: another document is current";

            var alive = _ids.Where(id => now.GetElement(id) is not null).ToList();

            if (alive.Count == 0)
                return "did not put it back: none of those elements is in the model any more";

            view.Selection.SetElementIds(alive);

            return alive.Count == _ids.Count
                ? "put back " + Count(alive.Count)
                : "put back " + Count(alive.Count) + " of " + Count(_ids.Count) + "; the rest are gone from the model";
        }
        catch (Exception error)
        {
            _log.Warn(error, "pane {0}: what was selected could not be put back", _pane.Name);
            return "could not put it back: " + error.GetType().Name + ": " + error.Message;
        }
    }

    private static IList<ElementId> Selected(UIApplication? application)
    {
        try
        {
            return application?.ActiveUIDocument is { } view ? view.Selection.GetElementIds().ToList() : None;
        }
        catch
        {
            // Asked twice on every press, in a finally: a reading that throws is a missing reading, not a
            // failed press. The one that mattered - the reading at entry - already logged its own failure.
            return None;
        }
    }

    private static bool Same(IList<ElementId> left, IList<ElementId> right) =>
        left.Count == right.Count && Spell(left) == Spell(right);

    /// <summary>Ids ascending, as the probe and <see cref="PaneSelection"/> spell them.</summary>
    private static string Spell(IList<ElementId> ids) =>
        string.Join("; ", ids.Select(id => id.Value).OrderBy(value => value).Select(value => value.ToString(CultureInfo.InvariantCulture)));

    private static string Count(int many) => many.ToString(CultureInfo.InvariantCulture) + " id(s)";
}

/// <summary>One pane the host registered with Revit.</summary>
public sealed class RegisteredPane
{
    public RegisteredPane(string name, Guid id, Guid addInId)
    {
        Name = name;
        Id = id;
        AddInId = addInId;
    }

    /// <summary>The pane's name in its manifest.</summary>
    public string Name { get; }

    /// <summary>The GUID Revit knows it by.</summary>
    public Guid Id { get; }

    /// <summary>The add-in whose host registered it.</summary>
    public Guid AddInId { get; }

    // Counters rather than a log line each, because every question below is one Revit answers by
    // when and how often it calls us - and none of the answers is measured yet. They are what the probe
    // reports, and what a person reads off the log line the host writes on each call.
    private int _setupCalls;
    private int _creatorCalls;
    private int _toggles;
    private int _firstSetupThread;
    private int _firstCreatorThread;
    private int _selectionChanges;
    private string _selectionAtPress = string.Empty;
    private string _selectionAfterPress = string.Empty;
    private string _selectionRestore = string.Empty;

    /// <summary>How many times Revit called <c>SetupDockablePane</c>.</summary>
    public int SetupCalls => Volatile.Read(ref _setupCalls);

    /// <summary>How many times Revit called <c>CreateFrameworkElement</c> - its documentation allows "each time".</summary>
    public int CreatorCalls => Volatile.Read(ref _creatorCalls);

    /// <summary>How many times a press of its button reached <see cref="PaneEntryPoint"/>.</summary>
    /// <remarks>
    /// A press is a request Revit may drop, and a second press of a toggle undoes the first - so whoever
    /// presses again has to know whether the first one ran. This is how.
    /// </remarks>
    public int Toggles => Volatile.Read(ref _toggles);

    /// <summary>How many selection changes the host passed on to this pane, created or not.</summary>
    public int SelectionChanges => Volatile.Read(ref _selectionChanges);

    /// <summary>The managed thread of the first setup call; zero before it.</summary>
    public int FirstSetupThread => Volatile.Read(ref _firstSetupThread);

    /// <summary>The managed thread of the first creator call; zero before it.</summary>
    public int FirstCreatorThread => Volatile.Read(ref _firstCreatorThread);

    /// <summary>What was selected when its button was last pressed; empty for nothing, or for never pressed.</summary>
    public string SelectionAtPress => Volatile.Read(ref _selectionAtPress);

    /// <summary>
    /// What was selected when that press was about to return - the reading that says which side of the
    /// command's return Revit clears the selection on. Empty means nothing was selected then.
    /// </summary>
    public string SelectionAfterPress => Volatile.Read(ref _selectionAfterPress);

    /// <summary>What became of it, in a sentence: nothing to keep, kept by itself, put back, or left alone.</summary>
    public string SelectionRestore => Volatile.Read(ref _selectionRestore);

    /// <summary>Whether Revit called setup inside <c>RegisterDockablePane</c> itself; set by the host.</summary>
    public bool SetupDuringRegistration { get; set; }

    /// <returns>The call's ordinal, from one.</returns>
    public int RecordSetup()
    {
        Interlocked.CompareExchange(ref _firstSetupThread, Environment.CurrentManagedThreadId, 0);
        return Interlocked.Increment(ref _setupCalls);
    }

    /// <returns>The call's ordinal, from one.</returns>
    public int RecordCreate()
    {
        Interlocked.CompareExchange(ref _firstCreatorThread, Environment.CurrentManagedThreadId, 0);
        return Interlocked.Increment(ref _creatorCalls);
    }

    public void RecordToggle() => Interlocked.Increment(ref _toggles);

    public void RecordSelection() => Interlocked.Increment(ref _selectionChanges);

    /// <summary>A press begins: what it found, and the two answers about it cleared until it has them.</summary>
    public void RecordSelectionAtPress(string ids)
    {
        Volatile.Write(ref _selectionAtPress, ids ?? string.Empty);
        Volatile.Write(ref _selectionAfterPress, string.Empty);
        Volatile.Write(ref _selectionRestore, string.Empty);
    }

    public void RecordSelectionAfterPress(string ids) => Volatile.Write(ref _selectionAfterPress, ids ?? string.Empty);

    public void RecordSelectionRestore(string outcome) => Volatile.Write(ref _selectionRestore, outcome ?? string.Empty);
}

/// <summary>
/// Where a pane's button finds its pane: the same kind of meeting place as <see cref="HostRegistry"/>,
/// and for the same irreducible reason - Revit constructs the button's class from a string.
/// </summary>
/// <remarks>
/// Additive and idempotent, like everything that lives in the AppDomain Revit 2024 shares. A pane id is
/// registered with Revit at most once per process - a second registration throws, and the host asks
/// first - so there is never a second entry to argue with the first. Buttons are kept apart from panes
/// because nothing stops two buttons, on two panels, from showing one pane.
/// </remarks>
public static class PaneRegistry
{
    private static readonly ConcurrentDictionary<Guid, RegisteredPane> Panes = new();

    private static readonly ConcurrentDictionary<string, Guid> Buttons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a pane the host has registered with Revit.</summary>
    public static void Register(RegisteredPane pane)
    {
        if (pane is null)
            throw new ArgumentNullException(nameof(pane));

        Panes[pane.Id] = pane;
    }

    /// <summary>Records that the button class <paramref name="className"/> in <paramref name="assembly"/> shows the pane <paramref name="id"/>.</summary>
    /// <param name="assembly">The simple name of the assembly the button's class lives in.</param>
    /// <param name="className">The full name of the button's class, as the manifest spells it.</param>
    /// <param name="id">The pane.</param>
    public static void RegisterButton(string assembly, string className, Guid id) =>
        Buttons[Key(assembly, className)] = id;

    /// <summary>The pane the button class <paramref name="button"/> shows, or null.</summary>
    public static RegisteredPane? Find(Type button) =>
        Buttons.TryGetValue(Key(button.Assembly.GetName().Name ?? string.Empty, button.FullName ?? string.Empty), out var id)
            ? Find(id)
            : null;

    /// <summary>The pane registered under <paramref name="id"/>, or null.</summary>
    public static RegisteredPane? Find(Guid id) => Panes.TryGetValue(id, out var pane) ? pane : null;

    /// <summary>Every pane registered in this process, for diagnostics.</summary>
    public static IReadOnlyList<RegisteredPane> All =>
        Panes.Values.OrderBy(pane => pane.Name, StringComparer.Ordinal).ToList();

    // Joined by a character no assembly or class name holds, so two names never join into a third.
    private static string Key(string assembly, string className) => assembly + "|" + className;
}
