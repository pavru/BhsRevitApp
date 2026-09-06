using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
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
/// behind it, and a <c>UIControlledApplication</c> to build a ribbon with. Everything else is in the
/// base, and is what the <see cref="RevitDbAddInApplication"/> form gets too.
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

    public Result OnStartup(UIControlledApplication application)
    {
        // First statement, before the first try. OnStartup runs on the API thread by definition, so
        // this is both the earliest and the only place the answer is free - and without it every
        // opening line of the log claims it was written somewhere else.
        LogRouter.PrimaryThreadId = Environment.CurrentManagedThreadId;

        try
        {
            _application = application;

            Start(application.ControlledApplication);
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

            Stop(application.ControlledApplication);
        }
        catch (Exception error)
        {
            ReportFailedStop(error);
        }

        return Result.Succeeded;
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
    /// The directory is this assembly's own, found the only way that works inside Revit: from the
    /// assembly itself. The obvious alternatives - the entry assembly and
    /// <c>AppContext.BaseDirectory</c> - both name Revit's installation folder, because the entry
    /// assembly here is <c>Revit.exe</c>. Measured, for the settings layer, and true again here.
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

            RibbonButtons = RibbonBuilder.Build(_application!, directory!, services.Log);
        }
        catch (Exception error)
        {
            // A ribbon that could not be built is a missing button, never a failed start.
            services.Log.Error(error, "the ribbon could not be built");
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

        // Only now, because the event lives on UIApplication and this is the first legitimate one a
        // host is handed. Present on all four releases - checked against the metadata. Autodesk's
        // icon guidelines ask for a light and a dark variant of every icon, and choosing once at
        // startup would leave every button wrong for anyone who switches theme by daylight.
        session.ThemeChanged += (_, _) => RibbonBuilder.FollowTheme(Log.For(Name));

        Log.For(Name).Info("session is ready, document {0}",
            session.ActiveUIDocument?.Document?.Title ?? "(none)");
    }
}
