using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Settings;

namespace BHS.Revit.Host;

/// <summary>
/// What an edition with a user interface derives from.
/// </summary>
/// <remarks>
/// <para>
/// An edition writes one class and no more:
/// </para>
/// <code>
/// public sealed class BhsElectricalApplication : RevitAddInApplication
/// {
///     protected override Guid AddInId =&gt; new("...");
///     protected override string Name =&gt; "BHS Electrical";
/// }
/// </code>
/// <para>
/// The shape is the predecessor's and is kept: a thin <c>IExternalApplication</c> that Revit finds
/// by manifest, which composes and then hands over. What is not kept is what it composed with -
/// the Generic Host, a container and a static holding the built host - each replaced for a measured
/// reason recorded in <c>CLAUDE.md</c>.
/// </para>
/// <para>
/// This form adds exactly three things to <see cref="RevitAddInHost"/>: the pump, the external event
/// behind it, and a <c>UIControlledApplication</c> to build a ribbon and register dockable panes with.
/// Everything else is in the base, and is what the <see cref="RevitDbAddInApplication"/> form gets too.
/// </para>
/// <para>
/// <b>Nothing here blocks.</b> No waiting on a channel, no <c>Task.Run(...).Wait(n)</c>, no walking
/// of directories, no <c>AppDomain.AssemblyResolve</c>. Every add-in's <c>OnStartup</c> is
/// serialised with every other vendor's, and a cold Revit already costs up to seventy seconds;
/// the predecessor spent thirty of them waiting for its own host to start and four more padding a
/// splash screen.
/// </para>
/// </remarks>
public abstract class RevitAddInApplication : RevitAddInHost, IExternalApplication
{
    private RevitApiPump? _pump;
    private ExternalEvent? _pumpEvent;
    private UIControlledApplication? _application;
    private bool _watchingDialogs;
    private PaneHost? _panes;
    private EventHandler<ThemeChangedEventArgs>? _themeChanged;

    public Result OnStartup(UIControlledApplication application)
    {
        // First statement, before the first try. OnStartup runs on the API thread by definition, so
        // this is both the earliest and the only place the answer is free - and without it every
        // opening line of the log claims it was written somewhere else.
        LogRouter.PrimaryThreadId = Environment.CurrentManagedThreadId;

        // The same answer, where a diagnostic can reach it. Created inside Revit's own progress
        // callback, a diagnostic has no context to ask - so the process-wide fact is recorded once,
        // here, in the only place it is free.
        RevitDiagnostic.ApiThreadId = Environment.CurrentManagedThreadId;

        try
        {
            _application = application;

            // Before Start, so that anything the edition builds in its hooks already speaks Revit's
            // language. Revit's, not Windows': see RevitLanguage.
            FollowLanguage(application);

            Start(application.ControlledApplication);

            // After Start, because Start is what decides whether diagnostics were asked for at all.
            WatchDialogs(application);

            // After Start, because Start built the ribbon and the panes that follow the theme.
            WatchTheme(application);
        }
        catch (Exception error)
        {
            ReportFailedStart(error);
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            // Closed to new work first, and the queue is not drained: Revit is already leaving, and
            // what was deferred was by definition something that could wait.
            _pump?.Dispose();

            if (_watchingDialogs)
            {
                application.DialogBoxShowing -= OnDialogBoxShowing;
                _watchingDialogs = false;
            }

            if (_themeChanged is not null)
            {
                application.ThemeChanged -= _themeChanged;
                _themeChanged = null;
            }

            _panes?.Stop(application);

            Stop(application.ControlledApplication);
        }
        catch (Exception error)
        {
            ReportFailedStop(error);
        }

        return Result.Succeeded;
    }


    /// <summary>
    /// Reports modal dialogs into the diagnostic stream. Never answers one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half of diagnostics that only the interface form can provide: <c>DialogBoxShowing</c> is
    /// declared in RevitAPIUI, which the composition deliberately never names. It is also the half
    /// worth the most. A modal dialog is the one failure that, from outside the process, looks
    /// exactly like a slow Revit - twice diagnosed wrongly here, once as a lost request and once as
    /// a budget needing raising, and seen again the day this was written.
    /// </para>
    /// <para>
    /// <b>It reports and does not answer, and that is a rule rather than a first version.</b>
    /// <c>OverrideResult</c> would answer every dialog in the process, including other vendors' -
    /// and answering "yes" to somebody else's "save changes?" is corrupting somebody else's model.
    /// If answering is ever wanted, it belongs behind an explicit list of dialog ids supplied by
    /// whoever is watching, in a sweep and never in a product.
    /// </para>
    /// </remarks>
    private void WatchDialogs(UIControlledApplication application)
    {
        if (Diagnostics is null)
            return;

        try
        {
            application.DialogBoxShowing += OnDialogBoxShowing;
            _watchingDialogs = true;
        }
        catch (Exception error)
        {
            Log.For(Name).Warn(error, "diagnostics: dialogs could not be watched");
        }
    }

    /// <summary>
    /// Keeps the ribbon's placeholder icons and every live pane on Revit's theme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Subscribed here, in <c>OnStartup</c>, and that is a correction.</b> It used to wait for
    /// <c>ApplicationInitialized</c>, with a comment saying the event lives on <c>UIApplication</c>. It
    /// lives on <c>UIControlledApplication</c> too, on all four releases - checked against the metadata
    /// of 2024 and 2027 - so the window between startup and initialisation, in which a theme change
    /// would have been lost, is closed.
    /// </para>
    /// <para>
    /// Not filtered by <c>ThemeChangedType</c>: the ribbon's repaint is a handful of property sets, and
    /// the panes compare what they drew with <c>UIThemeManager.CurrentTheme</c> and do nothing when it
    /// did not change - so a canvas theme change costs next to nothing, and a misreported type could not
    /// cost a missed repaint. What the type reports is logged; whether the event fires at all when Revit
    /// follows the system theme is not measured.
    /// </para>
    /// </remarks>
    private void WatchTheme(UIControlledApplication application)
    {
        try
        {
            _themeChanged = (_, args) =>
            {
                var log = Log.For(Name);
                log.Debug("Revit's theme changed ({0})", args.ThemeChangedType);

                RibbonBuilder.FollowTheme(log);
                _panes?.FollowTheme();
            };

            application.ThemeChanged += _themeChanged;
        }
        catch (Exception error)
        {
            Log.For(Name).Warn(error, "theme changes could not be watched");
        }
    }

    private static void FollowLanguage(UIControlledApplication application)
    {
        try
        {
            RevitLanguage.Current = RevitLanguage.Culture(application.ControlledApplication.Language);
        }
        catch (Exception)
        {
            // The neutral strings, then. Not worth a failed start, and the log is not up yet.
        }
    }

    private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs args)
    {
        var id = args.DialogId ?? string.Empty;
        var detail = args is TaskDialogShowingEventArgs task ? task.Message ?? string.Empty : string.Empty;

        Diagnostics?.Observe(new RevitDiagnostic(RevitPhase.Blocked, "dialog", detail)
        {
            DialogId = id,
        });
    }

    /// <summary>Called on the API thread once everything the framework provides is up.</summary>
    /// <remarks>
    /// Where an edition builds its ribbon and does whatever else it needs.
    /// <para>
    /// <paramref name="application"/> is passed rather than kept anywhere a feature can reach. Ribbon
    /// panels and dockable panes are registered through <c>UIControlledApplication</c> and through
    /// nothing else, but it is a startup object: valid for this call and not afterwards. Putting it
    /// in the context would have made it look storable, which is the same mistake the context avoids
    /// with <c>UIApplication</c>, one level up.
    /// </para>
    /// </remarks>
    protected virtual void OnStarted(IUiFeatureServices services, UIControlledApplication application)
    {
    }

    /// <summary>How many buttons the manifests contributed. Zero is normal for an edition with none.</summary>
    protected int RibbonButtons { get; private set; }

    /// <summary>
    /// The tab this edition puts its features' buttons on. Null or empty means Revit's own Add-Ins
    /// tab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The edition chooses the tab, the feature chooses the panel.</b> A feature's Entry manifest
    /// names panels only, because the same feature is offered by more than one edition and a tab is a
    /// claim on the ribbon that belongs to whoever is installed. Buttons in the edition's own manifest
    /// keep the tab they name, exactly as before; this applies to Entry manifests alone.
    /// </para>
    /// <para>
    /// Empty means what an empty <c>Tab</c> means in a manifest - the Add-Ins tab - so an edition that
    /// says nothing claims nothing.
    /// </para>
    /// </remarks>
    protected virtual string? RibbonTab => null;

    /// <summary>Sealed: builds the ribbon, then hands over to the edition.</summary>
    /// <remarks>
    /// Before the edition's own hook, so that an edition adding something by hand adds it to a ribbon
    /// that already exists rather than racing its own manifest.
    /// </remarks>
    protected sealed override void OnStarted(IFeatureServices services)
    {
        BuildRibbon(services);
        OnStarted((IUiFeatureServices)services, _application!);
    }

    /// <summary>
    /// Builds whatever the manifests beside this assembly declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directory is this assembly's own, found the only way that works inside Revit: from the
    /// assembly itself. The obvious alternatives - the entry assembly and
    /// <c>AppContext.BaseDirectory</c> - both name Revit's installation folder, because the entry
    /// assembly here is <c>Revit.exe</c>. Measured, for the settings layer, and true again here.
    /// </para>
    /// <para>
    /// The assemblies the edition's modules come from are named from the module instances the edition
    /// already built, so nothing is loaded to name them: a feature's Entry manifest counts only when
    /// its <c>&lt;P&gt;.Declaration</c> is among them.
    /// </para>
    /// </remarks>
    private void BuildRibbon(IFeatureServices services)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(GetType().Assembly.Location);

            if (string.IsNullOrEmpty(directory))
            {
                services.Log.Warn("no directory for {0}, so no ribbon was built", GetType().Assembly.FullName);
                return;
            }

            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in DeclaredModuleTypes())
            {
                if (type.Assembly.GetName().Name is { Length: > 0 } name)
                    declared.Add(name);
            }

            // Read once, for the ribbon and the panes both: a manifest that cannot be read is reported
            // once, and the two can never disagree about what the folder held. In Revit's language: the
            // overlay for RevitLanguage.Current, if the SDK baked one, replaces the neutral text by item
            // name - the pane title is fixed at registration on 2024-2026, so this is the only moment.
            var manifests = FeatureManifest.ReadDirectory(directory!,
                (file, error) => services.Log.Error(error, "ribbon manifest {0} could not be read", file),
                RevitLanguage.Current);

            ReportOverlays(manifests, services.Log);

            RibbonButtons = RibbonBuilder.Build(_application!, manifests, directory!, RibbonTab, declared, services.Log);

            RegisterPanes(services, manifests, directory!);
        }
        catch (Exception error)
        {
            // A ribbon that could not be built is a missing button, never a failed start.
            services.Log.Error(error, "the ribbon could not be built");
        }
    }

    /// <summary>Says, once per manifest, which language its text is in - and why, when it is not Revit's.</summary>
    /// <remarks>
    /// A missing overlay shows itself only as English on a Russian ribbon, which reads as "not translated"
    /// whether the file was never made, never delivered or would not parse. The line tells the three apart
    /// from the neutral case: no overlay culture asked, overlay found and laid, overlay asked and absent.
    /// </remarks>
    private static void ReportOverlays(IReadOnlyList<FeatureManifest> manifests, ILog log)
    {
        var culture = RevitLanguage.Current;

        foreach (var manifest in manifests)
        {
            var file = System.IO.Path.GetFileName(manifest.Path);

            if (culture is null)
                log.Debug("ribbon: {0} in its neutral text - Revit's language has no overlay of ours", file);
            else if (manifest.OverlayCulture.Length > 0)
                log.Info("ribbon: {0} in {1}, {2} string(s) from its overlay", file, manifest.OverlayCulture, manifest.OverlaidStrings);
            else if (System.IO.File.Exists(FeatureManifest.OverlayPath(manifest.Path, culture.Name)))
                log.Warn("ribbon: {0} has a {1} overlay that could not be read, so its text stays neutral", file, culture.Name);
            else
                log.Info("ribbon: {0} has no {1} overlay beside it, so its text stays neutral", file, culture.Name);
        }
    }

    /// <summary>How many dockable panes the manifests contributed and Revit accepted.</summary>
    protected int PaneCount => _panes?.Count ?? 0;

    /// <summary>Registers the dockable panes the same manifests declare. Never throws.</summary>
    /// <remarks>
    /// After the ribbon, so that a pane's button exists before its pane - not that Revit minds the
    /// order, but a log read top to bottom then tells the story in the order a person meets it. The
    /// anchor for loading content later is the edition's own assembly, for the load context it is in.
    /// </remarks>
    private void RegisterPanes(IFeatureServices services, IReadOnlyList<FeatureManifest> manifests, string directory)
    {
        try
        {
            var panes = new PaneHost(services.Log);
            panes.Register(_application!, manifests, directory, Modules, services.Ui(), AddInId, GetType().Assembly);
            _panes = panes;
        }
        catch (Exception error)
        {
            services.Log.Error(error, "the dockable panes could not be registered");
        }
    }

    /// <summary>
    /// Builds the pump, which is the whole of what this form adds to the composition.
    /// </summary>
    /// <remarks>
    /// An external event can only be created from a valid API context, and startup is the only one
    /// a host will ever be handed - which is why this is a seam in the base rather than something
    /// an edition does afterwards.
    /// </remarks>
    private protected sealed override FeatureServices CreateServices(
        IRevitContext context,
        ISettings settings,
        IModelSettingsSource models)
    {
        _pump = new RevitApiPump();
        _pumpEvent = ExternalEvent.Create(_pump);
        _pump.Attach(_pumpEvent);

        return new UiFeatureServices(context, _pump, settings, models, Name);
    }

    /// <summary>The one thing about Revit having started that is this form's alone: the session.</summary>
    private protected sealed override void OnInitialized(object sender)
    {
        // The public constructor, checked against all four releases. UIApplication.Instance exists
        // only on 2027, so it is no use as the common path.
        var session = new UIApplication((Application)sender);
        _pump?.Attach(_pumpEvent!);

        // The theme is no longer watched from here: see WatchTheme, subscribed in OnStartup.

        Log.For(Name).Info("session is ready, document {0}",
            session.ActiveUIDocument?.Document?.Title ?? "(none)");
    }
}
