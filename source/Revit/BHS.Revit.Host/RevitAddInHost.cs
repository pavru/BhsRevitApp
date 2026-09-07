using Autodesk.Revit.ApplicationServices;
using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Revit.Common;
using BHS.Settings;

namespace BHS.Revit.Host;

/// <summary>
/// Everything a Revit add-in host does that does not need a user interface.
/// </summary>
/// <remarks>
/// <para>
/// Revit offers two shapes of add-in application, and both are first-class here:
/// <c>IExternalApplication</c>, which gets a <c>UIControlledApplication</c> and can build a ribbon,
/// and <c>IExternalDBApplication</c>, which gets a plain <c>ControlledApplication</c> and exists
/// where there is no interface at all. Checked against the metadata of all four supported releases:
/// <c>IExternalDBApplication</c> is declared in <c>RevitAPI</c>, not <c>RevitAPIUI</c>, its two
/// methods take a <c>ControlledApplication</c>, and its result has only <c>Succeeded</c> and
/// <c>Failed</c> - not even the third answer <c>Result</c> offers.
/// </para>
/// <para>
/// <b>The line between the two forms is not "interface or no interface" - it is whether there is a
/// way back onto the API thread.</b> Everything else the framework provides works in both: layered
/// settings from disk, logging, the journal sink (which needs only a <c>ControlledApplication</c>),
/// the host registry, and a document's own settings. What the DB form does not get is
/// <c>ExternalEvent</c>, which lives in <c>RevitAPIUI</c>, and therefore the pump.
/// </para>
/// <para>
/// So this class carries the composition and names no type from <c>RevitAPIUI</c> anywhere in it.
/// A single class implementing both interfaces was considered and rejected for the same reason: the
/// mention alone would tie the DB form to the assembly whose absence is the point of having it.
/// </para>
/// </remarks>
public abstract class RevitAddInHost
{
    private RevitContext? _context;
    private LayeredSettings? _settings;
    private RevitDiagnostics? _diagnostics;
    private FeatureServices? _services;
    private ModelSettingsSource? _models;
    private ILog _log = Log.For<RevitAddInHost>();

    /// <summary>This edition's add-in id. Must match the manifest: it is the registry key.</summary>
    protected abstract Guid AddInId { get; }

    /// <summary>This edition's name, for the log and the settings section.</summary>
    protected abstract string Name { get; }

    /// <summary>The layered settings, with their layers, for an edition that reports on them.</summary>
    /// <remarks>
    /// The merged view is on <see cref="IFeatureServices.Settings"/> and is what a feature wants.
    /// This is the whole object, layer list and read errors included, and exists because "which file
    /// should I have edited" is a question worth answering out loud.
    /// </remarks>
    protected LayeredSettings? Layers => _settings;

    /// <summary>What was handed to the modules. Null until startup has got that far.</summary>
    protected IFeatureServices? Services => _services;

    /// <summary>
    /// What Revit is doing, when the edition has asked to be told.
    /// </summary>
    /// <remarks>
    /// Null unless <c>Diagnostics:Enabled</c> is set. Off by default and stated as a rule: this is
    /// a way for something outside the process to watch the inside of it, and what we build for our
    /// own debugging ships to a customer in the same assembly. A door that can be opened by
    /// configuration is a door somebody eventually opens who is not us.
    /// </remarks>
    protected RevitDiagnostics? Diagnostics => _diagnostics;

    /// <summary>
    /// Raised for every phase change, on whatever thread Revit raised it - usually the API thread.
    /// </summary>
    /// <remarks>
    /// An event rather than a channel, because the host does not reference the transport: an
    /// edition that never opens one must not carry Grpc and Google.Protobuf into Revit AppDomain
    /// to get a phase machine. Whoever owns a channel subscribes and translates.
    ///
    /// A handler here runs inside Revit progress reporting. It must not block, and it must not
    /// throw - though a throw is caught and logged rather than allowed to surface as a fault in
    /// Revit own machinery.
    /// </remarks>
    protected event EventHandler<RevitDiagnostic>? DiagnosticObserved;

    /// <summary>The context, for a form that needs it before the services exist.</summary>
    private protected RevitContext? Context => _context;

    /// <summary>The modules this edition brings up eagerly. Most features need none.</summary>
    /// <remarks>
    /// Deliberately a list the edition writes rather than a directory it scans. Scanning means
    /// loading, and on Revit 2024 every assembly loaded holds its simple name in the AppDomain
    /// shared with every other vendor for the rest of the session - including features nobody
    /// touched. Buttons come from manifests instead, and their assemblies load when pressed.
    /// </remarks>
    protected virtual IReadOnlyList<IFeatureModule> Modules => Array.Empty<IFeatureModule>();

    /// <summary>Called on the API thread once everything the framework provides is up.</summary>
    /// <remarks>
    /// It runs inside the same guard as the rest of startup, so throwing here costs the edition and
    /// not Revit. A host form with an interface overrides this and offers a richer one.
    /// </remarks>
    protected virtual void OnStarted(IFeatureServices services)
    {
    }

    /// <summary>Called on the API thread before the framework is taken down.</summary>
    protected virtual void OnStopping()
    {
    }

    /// <summary>
    /// Builds what the form is allowed to build.
    /// </summary>
    /// <remarks>
    /// The one seam between the forms. It is called from inside startup, which is the only API
    /// context a host ever gets - and the only place an <c>ExternalEvent</c> can be created, which
    /// is why the interface form overrides this rather than adding the pump afterwards.
    /// </remarks>
    private protected virtual FeatureServices CreateServices(
        IRevitContext context,
        ISettings settings,
        IModelSettingsSource models) =>
        new FeatureServices(context, settings, models, Name);

    /// <summary>Everything between Revit calling us and a feature being usable.</summary>
    protected void Start(ControlledApplication controlled)
    {
        var release = controlled.VersionNumber;

        // From disk, before looking for anybody. This is what makes startup order stop mattering:
        // the side configures itself and runs whether or not a companion exists. The product layer
        // resolves to the add-in's own Lib folder - measured, because the obvious ways of finding it
        // both name Revit's installation directory instead.
        _settings = LayeredSettings.Read(new SettingsOptions
        {
            Side = ProcessSide.Revit,
            Release = int.TryParse(release, out var year) ? year : null,
        });

        LogSetup.Start(LogRouter.Default, "revit" + release, _settings);

        // Once per process, however many hosts there are - the guard LogSetup already applies to
        // the sinks it owns, applied here to the one it cannot know about. Measured the first time
        // two hosts ran together: the journal took every warning twice, and on Revit 2024 two
        // separate editions sharing the AppDomain would have done the same. Whoever configures
        // second must not undo or duplicate the first; that rule is why the router is additive.
        if (!LogRouter.Default.Sinks.Any(sink => sink is JournalLogSink))
            LogRouter.Default.Add(new JournalLogSink(controlled));

        LogRouter.Default.Apply(_settings);

        _log = Log.For(Name);
        _log.Info("{0} starting on Revit {1}", Name, release);

        foreach (var pair in _settings.Errors)
            _log.Warn(pair.Value, "settings layer {0} could not be read", pair.Key.Path);

        _context = new RevitContext(controlled, AddInId);
        _models = new ModelSettingsSource(_settings);
        _services = CreateServices(_context, _settings, _models);

        // Before the modules: a module may reach a command in Start, and a command looks itself up
        // here. Additive and keyed, so a second edition in this AppDomain neither sees this nor is
        // displaced by it.
        HostRegistry.Register(AddInId, GetType().Assembly, _services);

        // While the document still exists: DocumentClosedEventArgs carries only an int id - checked
        // against the metadata - so there would be nothing left to drop by then.
        controlled.DocumentClosing += OnDocumentClosing;

        // In the base rather than in the interface form, and that is a correction: this was
        // subscribed one level up while the flag it raises was called "is the session ready". Then
        // it was measured arriving in a DBApplication add-in, which never has a session - so the
        // event is common to both forms, and so is what it means.
        controlled.ApplicationInitialized += OnApplicationInitialized;

        // After the registry and before the modules: a module that does something slow in Start is
        // exactly the kind of thing worth seeing in the stream, and by here everything it needs to
        // report through exists.
        StartDiagnostics(controlled);

        StartModules();

        OnStarted(_services);

        _log.Info("{0} started", Name);
    }

    /// <summary>
    /// Subscribes to Revit progress and document events, if this edition asked for it.
    /// </summary>
    /// <remarks>
    /// Guarded twice over. Failing to start diagnostics must never fail the add-in - the whole
    /// point is to watch a start, not to be another way of ruining one - and the setting has to be
    /// asked for explicitly rather than defaulted on, because subscribing to ProgressChanged means
    /// a callback on the API thread during every model load in the process.
    /// </remarks>
    private void StartDiagnostics(ControlledApplication controlled)
    {
        if (_settings is null || !_settings.Flag("Diagnostics:Enabled", false))
            return;

        try
        {
            _diagnostics = new RevitDiagnostics(controlled, _log,
                diagnostic => DiagnosticObserved?.Invoke(this, diagnostic));

            _log.Info("diagnostics: watching Revit phases");
        }
        catch (Exception error)
        {
            _log.Warn(error, "diagnostics could not be started");
        }
    }

    /// <summary>The way down, in the reverse order of the way up.</summary>
    protected void Stop(ControlledApplication controlled)
    {
        _diagnostics?.Dispose();
        _diagnostics = null;

        try
        {
            controlled.DocumentClosing -= OnDocumentClosing;
            controlled.ApplicationInitialized -= OnApplicationInitialized;
        }
        catch (Exception error)
        {
            _log.Warn(error, "could not unsubscribe from Revit's events");
        }

        OnStopping();

        // Reverse order, each guarded: a module that fails to stop must not strand the ones below it.
        for (var index = Modules.Count - 1; index >= 0; index--)
        {
            try
            {
                Modules[index].Stop();
            }
            catch (Exception error)
            {
                _log.Error(error, "module {0} did not stop", Modules[index].GetType().FullName);
            }
        }

        HostRegistry.Unregister(AddInId);
        _settings?.Dispose();

        // LogRouter.Default is deliberately left alone. It is shared, another edition may still be
        // running, and the file sink flushes every line anyway. The additivity that holds at startup
        // has to hold on the way out too.
        _log.Info("{0} stopped", Name);
    }

    /// <summary>What to write when a start did not happen. Never a dialog.</summary>
    /// <remarks>
    /// An exception escaping into Revit is a modal window on a machine with nobody in front of it.
    /// A failed start is a line in the log and a missing button, never a refusal to load.
    /// </remarks>
    private protected void ReportFailedStart(Exception error) =>
        _log.Critical(error, "{0} did not start", Name);

    private protected void ReportFailedStop(Exception error) =>
        _log.Error(error, "{0} did not shut down cleanly", Name);

    private void StartModules()
    {
        foreach (var module in Modules)
        {
            try
            {
                // Its own section and its own log category, keyed by the module's simple type name.
                // Two modules must not be able to argue over a key, and a shared log file has to say
                // who wrote each line - which is the whole reason FeatureServices can narrow itself.
                module.Start(_services!.For(module.GetType().Name));
                _log.Info("module {0} started", module.GetType().FullName);
            }
            catch (Exception error)
            {
                // One broken feature must not cost the others, and must not cost Revit.
                _log.Error(error, "module {0} did not start", module.GetType().FullName);
            }
        }
    }

    /// <summary>Called on the API thread when Revit has finished starting. Guarded by the caller.</summary>
    /// <remarks>
    /// The interface form overrides this to take the session, which is the one thing about this
    /// moment that is not common to both.
    /// </remarks>
    private protected virtual void OnInitialized(object sender)
    {
    }

    private void OnApplicationInitialized(object? sender, Autodesk.Revit.DB.Events.ApplicationInitializedEventArgs args)
    {
        try
        {
            _context?.MarkInitialized();
            OnInitialized(sender!);

            _log.Info("Revit finished starting");
        }
        catch (Exception error)
        {
            _log.Error(error, "could not act on Revit having started");
        }
    }

    private void OnDocumentClosing(object? sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs args)
    {
        try
        {
            _models?.Forget(args.Document);
        }
        catch (Exception error)
        {
            _log.Warn(error, "could not forget a closing document");
        }
    }
}
