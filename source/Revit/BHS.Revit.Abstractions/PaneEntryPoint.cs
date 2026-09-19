using System.Collections.Concurrent;
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
    }
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
