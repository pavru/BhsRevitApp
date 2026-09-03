using System.Runtime.CompilerServices;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;
using GrpcDotNetNamedPipes;

namespace BHS.Revit.Probe;

/// <summary>
/// The probe add-in: serves the channel from inside Revit and announces itself to whoever is
/// listening on the well-known name.
/// </summary>
/// <remarks>
/// Two questions could not be answered outside Revit, and they are the two this class exists for.
/// A pipe server costs four blocked threads in whatever process hosts it, and nothing had ever
/// hosted one in Revit. And on Revit 2025 our copies of <c>Grpc.Core.Api</c> and
/// <c>Google.Protobuf</c> arrive into a process that loaded its own long before - a conflict
/// measured on a live process once, from outside, and never from inside the AppDomain that has to
/// live with it.
/// <para>
/// Nothing here fails loudly. A probe that pops a dialog on a Revit nobody is sitting in front of
/// converts a measurement into a hang, so every failure is written to a log the runner reports and
/// startup returns success regardless.
/// </para>
/// </remarks>
public sealed class ProbeApplication : IExternalApplication
{
    private const int RegistrationAttempts = 5;
    private static readonly TimeSpan BetweenAttempts = TimeSpan.FromSeconds(2);

    private ProbeFacts? _facts;
    private ProbeChannel? _channel;
    private ExternalEvent? _exit;
    private NamedPipeServer? _server;

    public Result OnStartup(UIControlledApplication application)
    {
        // An AppDomain.AssemblyResolve handler was tried here and removed. It did not help - the
        // binding it was meant to correct succeeds, it just succeeds onto Revit's copy, and the
        // handler only runs when binding fails. Worse, on Revit 2024 it would answer every failed
        // resolve in an AppDomain shared with every other vendor, offering them our assemblies.
        // The fix lives in BHS.Grpc.NamedPipes instead: agree with Revit on the version.
        return Start(application);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Result Start(UIControlledApplication application)
    {
        // First statement, before the first record. OnStartup runs on the API thread by definition,
        // so this is both the earliest and the only place the answer is free - and without it the
        // opening lines of every log would claim they were written somewhere else.
        LogRouter.PrimaryThreadId = Environment.CurrentManagedThreadId;

        try
        {
            ProbeLog.Write("startup: begin");

            var facts = new ProbeFacts(application.ControlledApplication);
            _facts = facts;

            ProbeLog.Write($"startup: Revit {facts.VersionNumber} build {facts.VersionBuild}, pid {facts.ProcessId}, api thread {facts.ApiThreadId}");
            ProbeLog.Write($"startup: loaded from {facts.AddInAssembly}");

            // Created here because an external event can only be created from an API context, and
            // this is the only API context the probe will ever be handed.
            // Read before anything is served, because that is the order the claim is about: a
            // side configures itself from disk and only then goes looking for a companion.
            var settings = new ProbeSettings(facts.Release);

            if (settings.Settings is not null)
            {
                LogSetup.Start(
                    LogRouter.Default,
                    "revit" + facts.Release.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    settings.Settings,
                    console: false,
                    facts: facts.Snapshot());

                LogRouter.Default.Add(new BHS.Revit.Common.JournalLogSink(application.ControlledApplication));
                LogRouter.Default.Apply(settings.Settings);
            }

            ProbeLog.Write("startup: settings product layer is " + BHS.Settings.SettingsLayout.ProductDirectory);
            ProbeLog.Write("startup: logging to " + ProbeLog.Path);

            _exit = ExternalEvent.Create(new ExitRevitHandler());
            _channel = new ProbeChannel(facts, settings, _exit);

            var server = PipeTransport.CreateServer(facts.PipeName);
            server.Error += (_, error) => ProbeLog.Write("server error", error.Error);
            RevitSideChannel.BindService(server.ServiceBinder, _channel);
            server.Start();
            _server = server;

            ProbeLog.Write("startup: serving " + facts.PipeName);

            BuildRibbon(application, facts);

            application.ControlledApplication.DocumentOpened += OnDocumentOpened;

            // Registration leaves the process, so it must not be what a cold Revit start waits on.
            var registration = new Thread(Register)
            {
                IsBackground = true,
                Name = "BHS probe registration",
            };
            registration.Start();
        }
        catch (Exception error)
        {
            ProbeLog.Write("startup failed", error);
        }

        // Always succeeded. A failed probe should leave Revit usable and leave a log behind; the
        // runner decides what a missing registration means.
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        }
        catch (Exception error)
        {
            ProbeLog.Write("shutdown: could not unsubscribe", error);
        }

        try
        {
            _server?.Kill();
            _server?.Dispose();
            ProbeLog.Write("shutdown: server stopped");
        }
        catch (Exception error)
        {
            ProbeLog.Write("shutdown: server would not stop", error);
        }

        return Result.Succeeded;
    }

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs args)
    {
        try
        {
            var document = args.Document;
            _facts?.SetDocument(document?.Title ?? string.Empty, document?.PathName ?? string.Empty);
            _channel?.Republish();
            ProbeLog.Write("document opened: " + (document?.Title ?? "(none)"));

            ShowAddInsTab();
        }
        catch (Exception error)
        {
            ProbeLog.Write("document opened: republish failed", error);
        }
    }

    /// <summary>
    /// Announces this instance on the well-known name, retrying briefly.
    /// </summary>
    /// <remarks>
    /// Retried because registration is the supported way to be found and a companion that is
    /// starting at the same moment should not be missed for it. Failing altogether is normal:
    /// most Revit sessions begin with a person double-clicking, and nobody is listening then.
    /// </remarks>
    private void Register()
    {
        var facts = _facts;
        if (facts is null)
            return;

        var request = new RegisterRequest
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = facts.InstanceId,
            CorrelationToken = facts.CorrelationToken ?? string.Empty,
            PipeName = facts.PipeName,
            RevitVersion = facts.Release,
            ProcessId = facts.ProcessId,
        };

        for (var attempt = 1; attempt <= RegistrationAttempts; attempt++)
        {
            try
            {
                var client = new WinSideChannel.WinSideChannelClient(PipeTransport.CreateClient(PipeNames.WinSide));
                var response = client.Register(request);
                ProbeLog.Write($"registered with '{response.InstanceId}' on contract '{response.ContractVersion}'");
                return;
            }
            catch (RpcException error)
            {
                ProbeLog.Write($"registration attempt {attempt}/{RegistrationAttempts} failed ({error.StatusCode})", error);
            }
            catch (Exception error)
            {
                ProbeLog.Write($"registration attempt {attempt}/{RegistrationAttempts} failed", error);
            }

            // Once, on the first failure. A registration that never arrives is the case where the
            // runner cannot ask anything, so the answer it would have asked for has to be written
            // down unprompted - and the answer is almost always which copy of what got loaded.
            if (attempt == 1)
            {
                LogLoadedAssemblies();
                LogBinding();
            }

            Thread.Sleep(BetweenAttempts);
        }

        ProbeLog.Write("registration abandoned: nobody is serving " + PipeNames.WinSide);
    }

    /// <summary>
    /// What the two disagreeing references to the polyfill actually bind to.
    /// </summary>
    /// <remarks>
    /// <c>Grpc.Core.Api</c> is compiled against System.Memory 4.0.1.1 and
    /// <c>GrpcDotNetNamedPipes</c> against 4.0.2.0, and only one file can be shipped. Whether that
    /// is fatal depends on whether both references end up on the same loaded assembly, which is a
    /// question about the binder rather than about the packages, and therefore one to ask the
    /// running process.
    /// </remarks>
    private static void LogBinding()
    {
        const string token = "Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51";

        // 4.0.5.0 is what the accepted Revit-side UI set asks for: WPF-UI 4.1.0 on net4x depends on
        // the System.Memory package 4.6.3, whose assembly version that is. Revit 2024 ships 4.0.1.1
        // and Revit.exe.config carries no redirect for this assembly, so whether a 4.0.5.0 reference
        // can bind at all inside Revit 2024 is the whole question - and only the binder can answer.
        foreach (var wanted in new[]
                 {
                     "System.Memory, Version=4.0.1.1, " + token,
                     "System.Memory, Version=4.0.2.0, " + token,
                     "System.Memory, Version=4.0.5.0, " + token,
                 })
        {
            try
            {
                var bound = System.Reflection.Assembly.Load(wanted);
                ProbeLog.Write($"bind '{wanted}' -> {bound.GetName().Version} at {bound.Location}");
            }
            catch (Exception error)
            {
                ProbeLog.Write($"bind '{wanted}' failed", error);
            }
        }

        try
        {
            var method = typeof(SerializationContext).GetMethod("GetBufferWriter");
            ProbeLog.Write(method is null
                ? "SerializationContext.GetBufferWriter is not there at all"
                : $"GetBufferWriter returns {method.ReturnType.FullName} from {method.ReturnType.Assembly.GetName().FullName}");
        }
        catch (Exception error)
        {
            ProbeLog.Write("could not reflect over SerializationContext", error);
        }
    }

    /// <summary>
    /// One panel with one button, so that the ribbon path is exercised rather than assumed.
    /// </summary>
    /// <remarks>
    /// It also settled a question that decided the shape of the ribbon generator. The API help says
    /// an <c>IExternalCommandAvailability</c> implementation "should share the same assembly with
    /// add-in External Command"; a button pointing at a class in <c>BHS.Revit.Common</c> answered it
    /// - Revit resolves the name inside the command's own assembly and nowhere else, so the type is
    /// not found and a <c>TypeLoadException</c> reaches the user as a modal dialog. "Should" is
    /// "must", and the failure is loud rather than a greyed-out button.
    /// </remarks>
    private static void BuildRibbon(UIControlledApplication application, ProbeFacts facts)
    {
        try
        {
            var panel = application.CreateRibbonPanel("BHS Probe");

            var button = new PushButtonData(
                "BHS.Probe.Command",
                "Probe",
                facts.AddInAssembly,
                typeof(ProbeCommand).FullName)
            {
                AvailabilityClassName = typeof(LocalAvailability).FullName,
                ToolTip = "Does nothing. Exists so that the ribbon path is exercised.",
            };

            panel.AddItem(button);
            ProbeLog.Write("ribbon: added a button with an availability class in the command's assembly");
        }
        catch (Exception error)
        {
            // The interesting failure mode, and the one worth catching rather than throwing: Revit
            // refusing the button outright is itself the answer.
            ProbeLog.Write("ribbon: could not be built", error);
        }
    }

    /// <summary>
    /// Brings the tab holding our buttons to the front, so that Revit asks about them.
    /// </summary>
    /// <remarks>
    /// Availability is queried while the tab is shown, and an unattended run shows nothing - which
    /// is why the first two sweeps answered "Revit asked neither" and settled nothing. Switching
    /// tabs is not a Revit API operation at all; it belongs to the WPF ribbon underneath, reached
    /// through <c>AdWindows</c>, so it needs no transaction and no external event - but it does need
    /// the UI thread, and <c>DocumentOpened</c> is on it.
    /// </remarks>
    private static void ShowAddInsTab()
    {
        try
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;

            if (ribbon is null)
            {
                ProbeLog.Write("ribbon: AdWindows has no ribbon yet");
                return;
            }

            foreach (var tab in ribbon.Tabs)
            {
                if (tab.Panels.Any(panel => panel.Source?.Title == "BHS Probe"))
                {
                    ribbon.ActiveTab = tab;
                    ProbeLog.Write($"ribbon: activated tab '{tab.Id}' to make Revit ask about availability");
                    return;
                }
            }

            ProbeLog.Write("ribbon: could not find the tab holding our panel");
        }
        catch (Exception error)
        {
            ProbeLog.Write("ribbon: could not activate the tab", error);
        }
    }

    private static void LogLoadedAssemblies()
    {
        try
        {
            foreach (var pair in LoadedAssemblies.Report())
                ProbeLog.Write("  " + pair.Key + " = " + pair.Value);
        }
        catch (Exception error)
        {
            ProbeLog.Write("could not report loaded assemblies", error);
        }
    }
}
