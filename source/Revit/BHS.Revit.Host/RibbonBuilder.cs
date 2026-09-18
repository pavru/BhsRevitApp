using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// Builds the ribbon from the manifests lying beside the assemblies, and loads none of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is a string, deliberately.</b> The class a button names is passed to Revit as
/// text and never resolved: <c>PushButtonData</c> takes an assembly path and a class name, and Revit
/// constructs the type when the button is pressed. Writing <c>typeof</c> anywhere in this file would
/// load the entry point, which resolves its generic base, which loads the feature assembly - while
/// the ribbon is being built. That was measured, and it is why the manifest exists at all.
/// </para>
/// <para>
/// On Revit 2024 the cost of getting it wrong is not startup time. Every add-in shares one AppDomain
/// there, so an assembly loaded holds its simple name for the rest of the session, against every
/// other vendor - including features nobody ever touched.
/// </para>
/// <para>
/// <b>Two kinds of manifest, and the difference is who chooses the tab.</b> An edition's own manifest
/// - and the probe's - names its tab per button, exactly as written. A feature's Entry manifest,
/// <c>&lt;P&gt;.Entry.features.json</c>, arrives beside the Entry assembly the edition references and
/// names only panels: the feature chooses the panel, the edition chooses the tab. And it counts only
/// if the edition's <c>Modules</c> list declares a module from <c>&lt;P&gt;.Declaration</c> - the
/// same list the commands behind those buttons find their host through.
/// </para>
/// <para>
/// <b>The two gates match on different keys, and agree only as far as the build makes them.</b> This
/// one asks for any listed module from the assembly <c>&lt;P&gt;.Declaration</c>; a command asks the
/// registry for its exact <c>TFeature</c>. <c>RVTENT002</c> makes every <c>TFeature</c> in
/// <c>&lt;P&gt;.Entry</c> a type from <c>&lt;P&gt;.Declaration</c>, which closes the case of an Entry
/// command naming another feature's module. It does not close a declaration holding two modules of
/// which the edition lists one: a button would then be built whose command answers "this edition does
/// not declare the feature". Neither declaration in the tree holds a second module today.
/// </para>
/// <para>
/// Nothing here throws. A panel Revit refuses, a manifest that will not parse, a tab that already
/// exists - each is a line in the log and a missing button, never a failed startup.
/// </para>
/// </remarks>
internal static class RibbonBuilder
{
    /// <summary>
    /// The buttons this host put on the ribbon, kept so their icons can follow the theme.
    /// </summary>
    /// <remarks>
    /// Only the ones wearing the placeholder. A button an edition gave an icon of its own is not
    /// ours to repaint when Revit switches theme.
    /// </remarks>
    private static readonly List<RibbonButton> Placeheld = new();

    /// <summary>Builds every button declared beside <paramref name="directory"/>.</summary>
    /// <param name="application">What <c>OnStartup</c> was given; valid for this call only.</param>
    /// <param name="manifests">Every manifest beside the edition, read once by the caller and shared with the panes.</param>
    /// <param name="directory">The edition's own folder.</param>
    /// <param name="editionTab">
    /// The tab a feature's Entry buttons go on. Null or empty means Revit's own Add-Ins tab - the same
    /// meaning an empty <c>Tab</c> has in a manifest.
    /// </param>
    /// <param name="declaredAssemblies">
    /// The simple names of the assemblies the edition's modules come from. Listed, not started.
    /// </param>
    /// <param name="log">Where every refusal goes, since none of them is thrown.</param>
    /// <returns>How many buttons were added.</returns>
    public static int Build(
        UIControlledApplication application,
        IReadOnlyList<FeatureManifest> manifests,
        string directory,
        string? editionTab,
        ICollection<string> declaredAssemblies,
        ILog log)
    {
        if (manifests.Count == 0)
            return 0;

        var placements = new List<Placement>();
        var honoured = 0;
        var skipped = 0;

        foreach (var manifest in manifests)
        {
            var fileName = System.IO.Path.GetFileName(manifest.Path);

            // Null means "each button's own tab", which is what an edition's own manifest gets.
            string? tab = null;

            if (manifest.IsEntry)
            {
                var declaration = manifest.EntryFeature + FeatureManifest.DeclarationSuffix;

                // Listed, not started: a module whose Start threw must not take its feature's
                // ribbon with it - the commands behind these buttons are still found through the
                // same list, and they can say what went wrong.
                if (!declaredAssemblies.Contains(declaration))
                {
                    log.Error(
                        "ribbon: {0} was not built - it belongs to the feature {1}, and this edition's Modules list declares no module from {2}; add that feature's module to Modules",
                        fileName, manifest.EntryFeature, declaration);

                    skipped++;
                    continue;
                }

                tab = editionTab ?? string.Empty;

                // An Entry manifest has no say in the tab, so a tab in one is a mistake in the
                // feature's project file. Said once per manifest, and the edition's tab wins.
                var named = manifest.Buttons.FirstOrDefault(button => button.Tab.Length > 0);

                if (named is not null)
                {
                    log.Warn(
                        "ribbon: {0} names the tab {1}, but a feature's Entry manifest does not choose tabs; its buttons go on {2}",
                        fileName, named.Tab, Describe(tab));
                }
            }

            // Beside the manifest, because that is where the build put it and where deployment keeps
            // it. Revit is given a path, not a name, and resolves nothing itself.
            var assemblyPath = System.IO.Path.Combine(directory, manifest.Assembly);

            if (!System.IO.File.Exists(assemblyPath))
            {
                log.Error("ribbon manifest {0} names {1}, which is not beside it",
                    manifest.Path, manifest.Assembly);

                skipped++;
                continue;
            }

            honoured++;

            foreach (var button in manifest.Buttons)
                placements.Add(new Placement(tab ?? button.Tab, assemblyPath, button));
        }

        var added = 0;
        var panels = new Dictionary<string, RibbonPanel>(StringComparer.Ordinal);
        var tabs = new HashSet<string>(StringComparer.Ordinal);

        // One panel per tab and panel name, whichever manifests its buttons came from - an edition's
        // own buttons and a feature's can share a panel, and so can two features. Panels appear in
        // the order they are first reached; within a panel, by order then by name across every
        // manifest, because Order says where a button sits on its panel rather than in its file.
        foreach (var panel in placements.GroupBy(placement => placement.PanelKey, StringComparer.Ordinal))
        {
            foreach (var placement in Ordered(panel))
            {
                if (Add(application, panels, tabs, placement, log))
                    added++;
            }
        }

        log.Info("ribbon: {0} button(s) from {1} manifest(s), {2} skipped", added, honoured, skipped);

        // Once per ribbon, not once per button. A plain button is indistinguishable from one whose
        // feature has simply not drawn an icon yet, so the difference has to be said rather than
        // seen - and the placeholder exists precisely so that "no icon" never looks normal.
        if (added > 0 && PlaceholderIcon.LastFailure is { Length: > 0 } failure)
            log.Warn("ribbon: placeholder icons are not what they should be - {0}", failure);

        return added;
    }

    /// <summary>By order, then by name: the same ribbon on every start, whatever the file system says.</summary>
    /// <remarks>Stable, so a tie across manifests keeps the manifests' own order.</remarks>
    private static IEnumerable<Placement> Ordered(IEnumerable<Placement> placements) =>
        placements.OrderBy(placement => placement.Button.Order)
                  .ThenBy(placement => placement.Button.Name, StringComparer.Ordinal);

    private static string Describe(string tab) => tab.Length == 0 ? "Revit's Add-Ins tab" : "the tab " + tab;

    private static bool Add(
        UIControlledApplication application,
        Dictionary<string, RibbonPanel> panels,
        HashSet<string> tabs,
        Placement placement,
        ILog log)
    {
        var button = placement.Button;

        try
        {
            var panel = Panel(application, panels, tabs, placement, log);

            if (panel is null)
                return false;

            var data = new PushButtonData(button.Name, button.Text, placement.AssemblyPath, button.ClassName);

            if (button.ToolTip.Length > 0)
                data.ToolTip = button.ToolTip;

            if (button.LongDescription.Length > 0)
                data.LongDescription = button.LongDescription;

            // Until a feature draws its own. An empty frame on a ribbon reads as broken rather than
            // unfinished, and the vendor mark says whose button it is even before it says what it
            // does. Both properties live on ButtonData, so this is the same line for every kind of
            // button - checked against the metadata of 2024 and 2027.
            data.Image = PlaceholderIcon.Small;
            data.LargeImage = PlaceholderIcon.Large;

            // Named as text like the command itself. Revit takes the assembly from the button and
            // looks for this class inside it and nowhere else - measured, and a class from another
            // assembly is a TypeLoadException dialog rather than a greyed-out button. RefCheck fails
            // the build for it, so by here it has already been answered for.
            if (button.AvailabilityClassName.Length > 0)
                data.AvailabilityClassName = button.AvailabilityClassName;

            // Kept, because Revit's theme can change while it runs and a dark mark on a dark ribbon
            // is not an icon. RibbonButton carries Image and LargeImage - checked on 2024 and 2027 -
            // so the live button can be repainted without rebuilding the ribbon.
            if (panel.AddItem(data) is RibbonButton added)
                Placeheld.Add(added);

            return true;
        }
        catch (Exception error)
        {
            log.Error(error, "ribbon: button {0} could not be added", button.Name);
            return false;
        }
    }

    private static RibbonPanel? Panel(
        UIControlledApplication application,
        Dictionary<string, RibbonPanel> panels,
        HashSet<string> tabs,
        Placement placement,
        ILog log)
    {
        if (panels.TryGetValue(placement.PanelKey, out var existing))
            return existing;

        var tab = placement.Tab;
        var name = placement.Button.Panel;

        try
        {
            RibbonPanel panel;

            if (tab.Length == 0)
            {
                // No tab named: Revit's own Add-Ins tab, which is where a button belongs until
                // somebody decides otherwise. A tab of one's own is a claim on the ribbon.
                panel = application.CreateRibbonPanel(name);
            }
            else
            {
                // Once per tab and never twice: a second CreateRibbonTab with the same name throws,
                // and two editions in one AppDomain on Revit 2024 make that a real case rather than
                // a careless one.
                if (tabs.Add(tab))
                {
                    try
                    {
                        application.CreateRibbonTab(tab);
                    }
                    catch (Exception error)
                    {
                        // Somebody else's, or ours from another edition. Either way it now exists,
                        // which is all the panel needs.
                        log.Debug("ribbon: tab {0} was already there ({1})", tab, error.GetType().Name);
                    }
                }

                panel = application.CreateRibbonPanel(tab, name);
            }

            panels[placement.PanelKey] = panel;
            return panel;
        }
        catch (Exception error)
        {
            log.Error(error, "ribbon: panel {0} could not be created", name);
            return null;
        }
    }

    /// <summary>
    /// Repaints the placeholder buttons after Revit switched theme. On the UI thread.
    /// </summary>
    /// <remarks>
    /// Autodesk's icon guidelines ask for a light and a dark variant of every icon, and Revit has
    /// had both themes since 2024. Choosing once at startup would leave every button wrong for
    /// anyone who switches, which is a thing people do by daylight.
    /// </remarks>
    public static void FollowTheme(ILog log)
    {
        try
        {
            var small = PlaceholderIcon.Small;
            var large = PlaceholderIcon.Large;

            foreach (var button in Placeheld)
            {
                button.Image = small;
                button.LargeImage = large;
            }

            log.Debug("ribbon: repainted {0} placeholder icon(s) for the current theme", Placeheld.Count);
        }
        catch (Exception error)
        {
            log.Warn(error, "ribbon: could not follow the theme change");
        }
    }

    /// <summary>One button, with the tab it actually goes on and the assembly its manifest named.</summary>
    private sealed class Placement
    {
        public Placement(string tab, string assemblyPath, FeatureButton button)
        {
            Tab = tab;
            AssemblyPath = assemblyPath;
            Button = button;

            // Joined by a character no manifest puts in a tab or panel name, so two names never
            // join into a third.
            PanelKey = tab + "\0" + button.Panel;
        }

        /// <summary>Empty for Revit's Add-Ins tab.</summary>
        public string Tab { get; }

        public string AssemblyPath { get; }

        public FeatureButton Button { get; }

        public string PanelKey { get; }
    }
}
