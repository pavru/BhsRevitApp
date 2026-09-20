using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BHS.Revit.Abstractions;
using BHS.Revit.Probe.Declaration;

namespace BHS.Revit.Probe;

/// <summary>
/// What the probe asks about its dockable pane, and the one thing it does to Revit to ask it: switch
/// the theme and put it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every answer here is a measurement, none of it known in advance.</b> When Revit calls setup, when
/// and how often it asks for the element, whether the content assembly stays out until then, whether a
/// theme switch reaches a live pane - each is recorded where it happens and read back by the sweep.
/// </para>
/// <para>
/// <b>The theme switch changes a setting of the person's Revit</b>, which Revit keeps between sessions -
/// decision of the owner, and only in the mode run with somebody at the screen. So it follows the same
/// discipline as the shared parameter file: the original theme is written to a marker before the switch,
/// put back as soon as the check is done and again on shutdown, and a marker still there on the next
/// start - Revit killed in between - is put back then. The window of damage is "until this Revit starts
/// again with the probe", not "forever".
/// </para>
/// </remarks>
internal static class PaneProbe
{
    /// <summary>The probe pane's id: the same GUID as in BHS.Revit.Probe.Entry's project file.</summary>
    public static readonly Guid PaneId = new("3c0f5d7e-9a41-4f6b-8e2d-6b1a7c94e5f3");

    /// <summary>The pane's content assembly. Named as text: naming a type from it would load it.</summary>
    public const string PaneAssemblyName = "BHS.Revit.Probe.Pane";

    public const string WpfUiName = "Wpf.Ui";

    /// <summary>
    /// The assembly the pane's content needs and nothing else in this deployment names. Text again, and
    /// for a second reason: naming a type from it would load it, and the whole question is that nobody
    /// else does.
    /// </summary>
    public const string SupportAssemblyName = "BHS.Revit.Probe.Pane.Support";

    private static int _themeChanges;
    private static string _lastThemeChange = string.Empty;
    private static string _switch = string.Empty;

    /// <summary>Whether the pane's assembly was loaded when the probe first looked, straight after startup.</summary>
    public static bool PaneLoadedAtStartup { get; private set; }

    /// <summary>The same question about WPF-UI, which only the pane shell names.</summary>
    public static bool WpfUiLoadedAtStartup { get; private set; }

    /// <summary>The same question about the assembly the pane's content needs beside it.</summary>
    /// <remarks>
    /// False is what makes the answer below worth anything: an assembly something else had already
    /// brought in would be found by a pane whether or not the host loaded the content properly, and the
    /// check would pass while measuring nothing. That is how this defect stayed hidden until a person
    /// met it.
    /// </remarks>
    public static bool SupportLoadedAtStartup { get; private set; }

    /// <summary>The first line of the probe's own startup, straight after the host registered the panes.</summary>
    public static void RecordStartup(UIControlledApplication application)
    {
        PaneLoadedAtStartup = ProbeApplication.IsLoaded(PaneAssemblyName);
        WpfUiLoadedAtStartup = ProbeApplication.IsLoaded(WpfUiName);
        SupportLoadedAtStartup = ProbeApplication.IsLoaded(SupportAssemblyName);

        // Counted by the probe itself, beside the host's own subscription: whether Revit raises the
        // event for a switch made through UIThemeManager is part of what the switch measures.
        application.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Everything the sweep asks about the pane. Inside the pump, on the API thread.</summary>
    public static IReadOnlyDictionary<string, string> Facts(UIApplication application)
    {
        var culture = CultureInfo.InvariantCulture;
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var id = new DockablePaneId(PaneId);

        facts["pane:registered"] = Ask(() => DockablePane.PaneIsRegistered(id));
        facts["pane:exists"] = Ask(() => DockablePane.PaneExists(id));
        facts["pane:shown"] = Ask(() => application.GetDockablePane(id).IsShown());
        facts["pane:title"] = Ask(() => application.GetDockablePane(id).GetTitle());

        // What the title should be: the probe's Entry manifest read back with the overlay for the culture
        // the host chose - the host's own reader, on the host's own answer. Equal to what Revit shows means
        // the host and the manifest agree, on a Revit of any language; which language it was is a note.
        facts["pane:titleExpected"] = Ask(() => ExpectedTitle(out _));
        facts["pane:titleCulture"] = Ask(() => ExpectedTitle(out var culture) is { } && culture.Length > 0 ? culture : "(neutral)");

        // What the Gate button reads on this Revit - a note: its answer depends on the language installed.
        facts["pane:gateText"] = Ask(() => GateText(application));

        var registered = PaneRegistry.Find(PaneId);
        facts["pane:inRegistry"] = registered is null ? "False" : "True";
        facts["pane:setupCalls"] = (registered?.SetupCalls ?? -1).ToString(culture);
        facts["pane:setupDuringRegistration"] = registered?.SetupDuringRegistration.ToString() ?? "(unregistered)";
        facts["pane:setupThread"] = (registered?.FirstSetupThread ?? 0).ToString(culture);
        facts["pane:creatorCalls"] = (registered?.CreatorCalls ?? -1).ToString(culture);
        facts["pane:creatorThread"] = (registered?.FirstCreatorThread ?? 0).ToString(culture);
        facts["pane:toggles"] = (registered?.Toggles ?? -1).ToString(culture);
        facts["pane:apiThread"] = BHS.Logging.LogRouter.PrimaryThreadId.ToString(culture);

        facts["pane:contentLoaded"] = ProbeApplication.IsLoaded(PaneAssemblyName) ? "True" : "False";
        facts["pane:contentLoadedAtStartup"] = PaneLoadedAtStartup ? "True" : "False";
        facts["pane:wpfUiLoaded"] = ProbeApplication.IsLoaded(WpfUiName) ? "True" : "False";
        facts["pane:wpfUiLoadedAtStartup"] = WpfUiLoadedAtStartup ? "True" : "False";

        // Whether a pane's content finds what sits beside it: the half of loading that was missing until
        // 2026-09-20, and that the sweep could not see because nothing the probe pane needed was ever
        // unloaded. The answer itself is the content's; these two say the question was worth asking.
        facts["pane:supportLoaded"] = ProbeApplication.IsLoaded(SupportAssemblyName) ? "True" : "False";
        facts["pane:supportLoadedAtStartup"] = SupportLoadedAtStartup ? "True" : "False";
        facts["pane:supportAnswer"] = PaneFacts.Support;
        facts["pane:supportLocalised"] = PaneFacts.SupportLocalised;
        facts["pane:supportFrom"] = PaneFacts.SupportFrom;

        facts["pane:created"] = PaneFacts.Created.ToString(culture);
        facts["pane:createThread"] = PaneFacts.CreateThread.ToString(culture);
        facts["pane:documentChanges"] = PaneFacts.DocumentChanges.ToString(culture);
        facts["pane:lastDocument"] = PaneFacts.LastDocument;
        facts["pane:readTitle"] = PaneFacts.ReadTitle;
        facts["pane:selectionChanges"] = PaneFacts.SelectionChanges.ToString(culture);
        facts["pane:lastSelection"] = PaneFacts.LastSelection;
        facts["pane:selectionThread"] = PaneFacts.SelectionThread.ToString(culture);
        facts["pane:hostSelectionChanges"] = (registered?.SelectionChanges ?? -1).ToString(culture);

        // What Revit itself has selected at this moment - asked of the active view, not of what any pane was
        // told, because the question is whether a press of the pane's button costs the person their selection.
        facts["pane:selectionNow"] = Ask(() => Spell(application));

        // And what the press made of it. Which side of the command's return Revit clears the selection on is
        // not known, so PaneSelectionKeeper reads it twice and says what it did; these three are that record.
        facts["pane:selectionAtPress"] = registered?.SelectionAtPress ?? "(unregistered)";
        facts["pane:selectionAfterPress"] = registered?.SelectionAfterPress ?? "(unregistered)";
        facts["pane:selectionRestore"] = registered?.SelectionRestore ?? "(unregistered)";

        facts["pane:revitTheme"] = Ask(() => UIThemeManager.CurrentTheme);
        facts["pane:themeChanges"] = Volatile.Read(ref _themeChanges).ToString(culture);
        facts["pane:lastThemeChange"] = Volatile.Read(ref _lastThemeChange);
        facts["pane:themeSwitch"] = Volatile.Read(ref _switch);

        // Which sentence the host's shell would say, straight from its resources: the first satellite
        // assembly of ours Revit is asked to load. Read here rather than off the screen, so that the answer
        // does not depend on the pane being open in the state that shows it.
        facts["pane:revitLanguage"] = Ask(() => application.Application.Language);
        facts["pane:shellCulture"] = BHS.Revit.Host.RevitLanguage.Current?.Name ?? "(neutral)";
        facts["pane:shellNoDocument"] = Ask(() =>
            new System.Resources.ResourceManager("BHS.Revit.Host.Resources.Shell", typeof(BHS.Revit.Host.RevitLanguage).Assembly)
                .GetString("Pane.NoDocument", BHS.Revit.Host.RevitLanguage.Current ?? CultureInfo.InvariantCulture) ?? "(null)");

        // Asked of the live element, which only exists once Revit asked for it. The pump runs on Revit's
        // main thread, which is the pane's too - unmeasured, so the inspection says where it ran.
        if (PaneFacts.Inspect is { } inspect)
        {
            try
            {
                foreach (var pair in inspect())
                    facts[pair.Key + "Live"] = pair.Value;
            }
            catch (Exception error)
            {
                facts["pane:inspect"] = "failed: " + error.GetType().Name + ": " + error.Message;
            }
        }

        return facts;
    }

    /// <summary>The probe's Entry manifest, beside the probe, as the host reads it for Revit's language.</summary>
    private static string ExpectedTitle(out string culture)
    {
        var directory = Path.GetDirectoryName(typeof(PaneProbe).Assembly.Location) ?? string.Empty;
        var manifest = FeatureManifest.Read(
            Path.Combine(directory, ProbeApplication.EntryAssemblyName + FeatureManifest.Extension),
            BHS.Revit.Host.RevitLanguage.Current);

        culture = manifest.OverlayCulture;
        return manifest.Panes.FirstOrDefault(pane => pane.Id == PaneId)?.Title ?? "(no such pane in the manifest)";
    }

    /// <summary>The text Revit gave the Gate button, read back from the ribbon through the API.</summary>
    private static string GateText(UIApplication application)
    {
        foreach (var panel in application.GetRibbonPanels(ProbeApplication.OwnTabName))
        {
            if (panel.Name != ProbeApplication.OwnPanelTitle)
                continue;

            foreach (var item in panel.GetItems())
            {
                if (item.Name == "BHS.Probe.Gate")
                    return item.ItemText;
            }
        }

        return "(not found on the panel)";
    }

    private static List<ElementId>? _selectionBefore;

    /// <summary>
    /// Selects one element of the active view through the API, remembering what was selected, and says which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The selection is the person's</b>, so what stood before is kept and put back by
    /// <see cref="RestoreSelection"/> - the same discipline as the theme, without a marker: a selection does
    /// not outlive the session, so a Revit killed in between loses nothing of the person's.
    /// </para>
    /// <para>
    /// Any element the active view shows that has a category and is neither a view nor a type: the question
    /// is whether the pane is told, not what the element is. Chosen to differ from what is selected already,
    /// so that "told" cannot be the ids it had before.
    /// </para>
    /// </remarks>
    public static string SelectOne(UIApplication application)
    {
        try
        {
            var view = application.ActiveUIDocument;

            if (view?.Document is not { } document || document.ActiveView is not { } active)
                return "no active view";

            var before = view.Selection.GetElementIds().ToList();
            _selectionBefore ??= before;

            var chosen = new FilteredElementCollector(document, active.Id)
                .WhereElementIsNotElementType()
                .Where(element => element.Category is not null && element is not View)
                .Select(element => element.Id)
                .FirstOrDefault(id => before.All(one => one.Value != id.Value));

            if (chosen is null)
                return "nothing selectable in the active view";

            view.Selection.SetElementIds(new List<ElementId> { chosen });

            return Spell(application);
        }
        catch (Exception error)
        {
            return "failed: " + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Puts back what was selected before <see cref="SelectOne"/>, and says what is selected now.</summary>
    public static string RestoreSelection(UIApplication application)
    {
        try
        {
            var view = application.ActiveUIDocument;

            if (view is null)
                return "no active view";

            view.Selection.SetElementIds(_selectionBefore ?? new List<ElementId>());
            _selectionBefore = null;

            return Spell(application);
        }
        catch (Exception error)
        {
            return "failed: " + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>
    /// What the active view has selected, ids ascending - the spelling <c>PaneSelection</c> and
    /// <c>PaneSelectionKeeper</c> both use, so that the sweep can compare two answers as strings.
    /// </summary>
    private static string Spell(UIApplication application)
    {
        var view = application.ActiveUIDocument;

        return view is null
            ? string.Empty
            : string.Join("; ", view.Selection.GetElementIds().Select(id => id.Value).OrderBy(id => id)
                .Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Hides the pane by the API rather than by its button: the sweep's clean-up.</summary>
    public static string Hide(UIApplication application)
    {
        try
        {
            var pane = application.GetDockablePane(new DockablePaneId(PaneId));
            pane.Hide();
            return pane.IsShown() ? "still shown" : "hidden";
        }
        catch (Exception error)
        {
            return "failed: " + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>
    /// Switches Revit's interface theme to the other one, writing the original down first.
    /// </summary>
    public static string SwitchTheme(UIApplication application)
    {
        try
        {
            var before = UIThemeManager.CurrentTheme;
            var after = before == UITheme.Dark ? UITheme.Light : UITheme.Dark;
            var marker = Marker(application);

            // Before the switch, and never overwritten: a marker already there holds the person's own
            // theme from a switch that was never put back, and that is the one to return to.
            if (!File.Exists(marker))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, before.ToString());
            }

            UIThemeManager.CurrentTheme = after;

            var outcome = $"switched {before} -> {after}, now {UIThemeManager.CurrentTheme}";
            Volatile.Write(ref _switch, outcome);
            ProbeLog.Write("pane: theme " + outcome + "; the original is in " + marker);
            return outcome;
        }
        catch (Exception error)
        {
            var outcome = "failed: " + error.GetType().Name + ": " + error.Message;
            Volatile.Write(ref _switch, outcome);
            ProbeLog.Write("pane: the theme could not be switched", error);
            return outcome;
        }
    }

    /// <summary>Puts the theme the marker names back, and removes the marker. Harmless without one.</summary>
    public static string RestoreTheme(UIApplication application) => Restore(Marker(application), "asked");

    /// <summary>On shutdown, the last chance inside this session. Direct, because the pump is closed by then.</summary>
    public static void RestoreThemeOnShutdown(string versionNumber)
    {
        var outcome = Restore(Marker(versionNumber), "on shutdown");

        if (outcome != Nothing)
            ProbeLog.Write("pane: " + outcome);
    }

    /// <summary>At startup, through the pump: a marker left by a Revit that did not get to put it back.</summary>
    public static void RestoreInterrupted(UIApplication application)
    {
        var outcome = Restore(Marker(application), "left by an interrupted run");

        if (outcome != Nothing)
            ProbeLog.Write("pane: " + outcome);
    }

    private const string Nothing = "nothing to restore";

    private static string Restore(string marker, string why)
    {
        try
        {
            if (!File.Exists(marker))
                return Nothing;

            var text = File.ReadAllText(marker).Trim();

            if (!Enum.TryParse<UITheme>(text, out var original))
            {
                File.Delete(marker);
                return $"marker {marker} held '{text}', which is no theme; removed";
            }

            UIThemeManager.CurrentTheme = original;
            File.Delete(marker);

            return $"theme restored to {original} ({why}), now {UIThemeManager.CurrentTheme}";
        }
        catch (Exception error)
        {
            // Kept, so the next start tries again.
            return "theme could not be restored (" + why + "): " + error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>Per release, because Revit keeps the theme per release.</summary>
    private static string Marker(UIApplication application) => Marker(application.Application.VersionNumber ?? "unknown");

    private static string Marker(string versionNumber) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BHS", "probe-theme-" + versionNumber + ".restore");

    private static void OnThemeChanged(object? sender, ThemeChangedEventArgs args)
    {
        Interlocked.Increment(ref _themeChanges);
        Volatile.Write(ref _lastThemeChange, args.ThemeChangedType + " -> " + SafeTheme());
    }

    private static string SafeTheme() => Ask(() => UIThemeManager.CurrentTheme);

    private static string Ask<T>(Func<T> question)
    {
        try
        {
            return question()?.ToString() ?? "(null)";
        }
        catch (Exception error)
        {
            return "threw " + error.GetType().Name + ": " + error.Message;
        }
    }
}
