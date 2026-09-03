using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Revit.Common;
using BHS.Settings;

namespace BHS.Revit.Host;

/// <summary>
/// What an edition derives from. Everything between Revit calling <c>OnStartup</c> and a feature
/// being usable happens here.
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
/// <b>Nothing here blocks.</b> No waiting on a channel, no <c>Task.Run(...).Wait(n)</c>, no walking
/// of directories, no <c>AppDomain.AssemblyResolve</c>. Every add-in's <c>OnStartup</c> is
/// serialised with every other vendor's, and a cold Revit already costs up to seventy seconds;
/// the predecessor spent thirty of them waiting for its own host to start and four more padding a
/// splash screen.
/// </para>
/// </remarks>
public abstract class RevitAddInApplication : IExternalApplication
{
    private RevitContext? _context;
    private RevitApiPump? _pump;
    private ExternalEvent? _pumpEvent;
    private LayeredSettings? _settings;
    private FeatureServices? _services;
    private ILog _log = Log.For<RevitAddInApplication>();

    /// <summary>This edition's add-in id. Must match the manifest: it is the registry key.</summary>
    protected abstract Guid AddInId { get; }

    /// <summary>This edition's name, for the log and the settings section.</summary>
    protected abstract string Name { get; }

    /// <summary>The modules this edition brings up eagerly. Most features need none.</summary>
    /// <remarks>
    /// Deliberately a list the edition writes rather than a directory it scans. Scanning means
    /// loading, and on Revit 2024 every assembly loaded holds its simple name in the AppDomain
    /// shared with every other vendor for the rest of the session - including features nobody
    /// touched. Buttons come from manifests instead, and their assemblies load when pressed.
    /// </remarks>
    protected virtual IReadOnlyList<IFeatureModule> Modules => Array.Empty<IFeatureModule>();

    public Result OnStartup(UIControlledApplication application)
    {
        // First statement, before the first try. OnStartup runs on the API thread by definition, so
        // this is both the earliest and the only place the answer is free - and without it every
        // opening line of the log claims it was written somewhere else.
        LogRouter.PrimaryThreadId = Environment.CurrentManagedThreadId;

        try
        {
            return Start(application);
        }
        catch (Exception error)
        {
            // An exception escaping OnStartup is a dialog on a machine with nobody in front of it.
            // A failed start is a line in the log and a missing button, never a refusal to load.
            _log.Critical(error, "{0} did not start", Name);
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            Stop(application);
        }
        catch (Exception error)
        {
            _log.Error(error, "{0} did not shut down cleanly", Name);
        }

        return Result.Succeeded;
    }

    /// <summary>Called on the API thread once everything the framework provides is up.</summary>
    /// <remarks>
    /// Where an edition builds its ribbon and does whatever else it needs. It runs inside the same
    /// guard as the rest of startup, so throwing here costs the edition and not Revit.
    /// </remarks>
    protected virtual void OnStarted(IFeatureServices services)
    {
    }

    /// <summary>Called on the API thread before the framework is taken down.</summary>
    protected virtual void OnStopping()
    {
    }

    private Result Start(UIControlledApplication application)
    {
        var controlled = application.ControlledApplication;
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
        LogRouter.Default.Add(new JournalLogSink(controlled));
        LogRouter.Default.Apply(_settings);

        _log = Log.For(Name);
        _log.Info("{0} starting on Revit {1}", Name, release);

        foreach (var pair in _settings.Errors)
            _log.Warn(pair.Value, "settings layer {0} could not be read", pair.Key.Path);

        _context = new RevitContext(application, AddInId);

        // An external event can only be created from a valid API context, and this is the only one
        // the host will ever be handed.
        _pump = new RevitApiPump();
        _pumpEvent = ExternalEvent.Create(_pump);
        _pump.Attach(_pumpEvent);

        _services = new FeatureServices(_context, _pump, _settings, Name);

        // Before the modules: a module may reach a command in Start, and a command looks itself up
        // here. Additive and keyed, so a second edition in this AppDomain neither sees this nor is
        // displaced by it.
        HostRegistry.Register(AddInId, GetType().Assembly, _services);

        // The moment a UIApplication becomes legitimate. It goes inside the pump rather than into
        // the context, because a session handed out is a session used from the wrong thread.
        controlled.ApplicationInitialized += OnApplicationInitialized;

        StartModules();

        OnStarted(_services);

        _log.Info("{0} started", Name);
        return Result.Succeeded;
    }

    private void StartModules()
    {
        foreach (var module in Modules)
        {
            try
            {
                module.Start(_services!);
                _log.Info("module {0} started", module.GetType().FullName);
            }
            catch (Exception error)
            {
                // One broken feature must not cost the others, and must not cost Revit.
                _log.Error(error, "module {0} did not start", module.GetType().FullName);
            }
        }
    }

    private void OnApplicationInitialized(object? sender, Autodesk.Revit.DB.Events.ApplicationInitializedEventArgs args)
    {
        try
        {
            // The public constructor, checked against all four releases. UIApplication.Instance
            // exists only on 2027, so it is no use as the common path.
            var session = new UIApplication((Application)sender!);
            _pump?.Attach(_pumpEvent!);
            _context?.MarkSessionReady();

            _log.Info("session is ready, document {0}",
                session.ActiveUIDocument?.Document?.Title ?? "(none)");
        }
        catch (Exception error)
        {
            _log.Error(error, "could not take the session");
        }
    }

    private void Stop(UIControlledApplication application)
    {
        // Closed to new work first, and the queue is not drained: Revit is already leaving, and
        // what was deferred was by definition something that could wait.
        _pump?.Dispose();

        try
        {
            application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;
        }
        catch (Exception error)
        {
            _log.Warn(error, "could not unsubscribe from ApplicationInitialized");
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
}
