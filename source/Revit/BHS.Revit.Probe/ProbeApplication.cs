using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;
using BHS.Revit.Host;
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
/// <para>
/// It derives from <see cref="RevitAddInApplication"/> rather than implementing
/// <c>IExternalApplication</c> itself, and that is deliberate on both sides. The probe stops being a
/// second host that reads its own settings and raises its own logging in parallel with the real one;
/// and the host stops being unexercised code, because the sweep that checks the probe on four live
/// releases now checks the host with it.
/// </para>
/// </remarks>
public sealed class ProbeApplication : RevitAddInApplication
{
    private const int RegistrationAttempts = 5;
    private static readonly TimeSpan BetweenAttempts = TimeSpan.FromSeconds(2);

    /// <summary>Must match the manifest: it is the key everything is filed under.</summary>
    internal static readonly Guid Id = new("6f2e17ae-5ff7-45b2-bb8b-3482446e9a67");

    private ProbeFacts? _facts;
    private ProbeChannel? _channel;
    private ExternalEvent? _exit;
    private ExternalEvent? _press;
    private ExternalEvent? _pressGate;
    private ExternalEvent? _pressPane;
    private NamedPipeServer? _server;

    protected override Guid AddInId => Id;

    protected override string Name => "BHS.Revit.Probe";

    /// <summary>
    /// The one feature the probe declares, which is what lets its Entry manifest be built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared, and not for anything it does.</b> The host builds <c>BHS.Revit.Probe.Entry.features.json</c>
    /// only when this list names a module from <c>BHS.Revit.Probe.Declaration</c>, and the Entry
    /// command finds its host by the same list. Naming the module type loads the declaration here, at
    /// construction - which is exactly what a real edition's list costs, and nothing more: the Entry and
    /// feature assemblies are not named anywhere in the probe.
    /// </para>
    /// <para>
    /// The DB half lists no such module, so a lookup by this feature finds one host with a user
    /// interface and not two - and the sweep compares the host the command was handed with this add-in.
    /// </para>
    /// </remarks>
    protected override IReadOnlyList<IFeatureModule> Modules { get; } =
        new IFeatureModule[] { new BHS.Revit.Probe.Declaration.ProbeGateFeature() };

    /// <summary>The tab the probe's own manifest gives Ping, now also the edition's tab.</summary>
    /// <remarks>
    /// The edition chooses the tab and the feature the panel, so the Entry button - which names only
    /// Ping's panel - lands beside Ping on a tab of ours only if the host applies this. That is the
    /// point of setting it to the same name rather than to another one: one shared panel is what the
    /// probe brings forward, and the sweep asserts that the Entry button stands on it - and on no other
    /// tab - before it reads anything the button was asked.
    /// </remarks>
    protected override string? RibbonTab => OwnTabName;

    /// <summary>
    /// Everything the probe adds on top of what the host already brought up.
    /// </summary>
    /// <remarks>
    /// What is no longer here is the point: the API thread, the layered settings, the log, the
    /// journal sink, the context and the pump all arrived before this runs. What remains is what
    /// only the probe wants - a channel server, a registration, a ribbon and a way out.
    /// </remarks>
    protected override void OnStarted(IUiFeatureServices services, UIControlledApplication application)
    {
        // First, before anything below can load anything: the ribbon was built a moment ago, inside
        // the base, and this is the earliest the probe can ask whether building it loaded the Entry
        // assembly. Loaded already means Revit loads a button's assembly when the button is added;
        // not loaded means it waits to be asked, and the handler below timestamps when it was.
        EntryLoadedAtRibbonBuild = IsLoaded(EntryAssemblyName);
        FeatureLoadedAtRibbonBuild = IsLoaded(FeatureAssemblyName);
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        // Straight after the host registered the panes, for the same reason: whether registering the
        // probe pane loaded its content assembly, or WPF-UI, is answered only this early.
        PaneProbe.RecordStartup(application);

        // Built by the host before this runs, from the manifests beside the assembly - the probe's own
        // and, since the probe declares its gate feature, BHS.Revit.Probe.Entry.features.json. Nothing
        // here names a button; the project files do, and the SDK wrote them down.
        ButtonsFromManifest = RibbonButtons;

        ProbeLog.Write("startup: begin");
        ProbeLog.Write($"startup: Entry assembly loaded by the time the ribbon was built: {EntryLoadedAtRibbonBuild}");

        var facts = new ProbeFacts(services.Revit.Controlled);
        _facts = facts;

        ProbeLog.Write($"startup: Revit {facts.VersionNumber} build {facts.VersionBuild}, pid {facts.ProcessId}, api thread {facts.ApiThreadId}");
        ProbeLog.Write($"startup: loaded from {facts.AddInAssembly}");
        ProbeLog.Write("startup: settings product layer is " + BHS.Settings.SettingsLayout.ProductDirectory);
        ProbeLog.Write("startup: logging to " + ProbeLog.Path);
        ProbeLog.Write($"startup: host registry holds {HostRegistry.Count} edition(s)");

        // Created here because an external event can only be created from an API context, and
        // OnStarted is still inside the one OnStartup was given.
        _exit = ExternalEvent.Create(new ExitRevitHandler());
        _press = ExternalEvent.Create(new PressButtonHandler("BHS.Probe.Ping", PressButtonHandler.Ping));

        // Kept, so the channel can say what the last press of it came to.
        GatePressHandler = new PressButtonHandler("BHS.Probe.Gate", PressButtonHandler.Gate);
        _pressGate = ExternalEvent.Create(GatePressHandler);

        // The pane's button, on an event of its own like Gate's. A toggle: the sweep presses it again
        // only when the previous press demonstrably did not run - RegisteredPane.Toggles says so.
        PanePressHandler = new PressButtonHandler("BHS.Probe.PaneToggle", PressButtonHandler.PaneToggle);
        _pressPane = ExternalEvent.Create(PanePressHandler);

        _channel = new ProbeChannel(facts, Layers, services, _exit, _press, _pressGate, _pressPane);

        // A theme switch an earlier run could not put back - Revit killed between the switch and the
        // restore. Through the pump, because UIThemeManager is asked from an API context everywhere else.
        services.Pump.Post("probe: restore a theme an interrupted run switched",
            session => PaneProbe.RestoreInterrupted(session.Application));

        var server = PipeTransport.CreateServer(facts.PipeName);
        server.Error += (_, error) => ProbeLog.Write("server error", error.Error);
        RevitSideChannel.BindService(server.ServiceBinder, _channel);
        server.Start();
        _server = server;

        ProbeLog.Write("startup: serving " + facts.PipeName);

        // The host raises these on Revit API thread, inside its progress reporting, so this handler
        // does one translation and one non-blocking hand-off and nothing else. If the publisher had
        // to wait for a reader here, Revit would be waiting for that reader too.
        // The backlog first, then the subscription. Everything between the host starting diagnostics
        // and this line had nowhere to go - including the very first event, Starting - so a watcher
        // saw a process that appeared to begin already initialised.
        if (Diagnostics is not null)
        {
            foreach (var early in Diagnostics.Backlog)
                _channel?.Diagnostics.Publish(Translate(early));
        }

        DiagnosticObserved += (_, diagnostic) => _channel?.Diagnostics.Publish(Translate(diagnostic));

        if (Diagnostics is null)
            ProbeLog.Write("startup: diagnostics are off - set Diagnostics:Enabled to watch phases");


        services.Revit.Controlled.DocumentOpened += OnDocumentOpened;

        // Registration leaves the process, so it must not be what a cold Revit start waits on.
        var registration = new Thread(Register)
        {
            IsBackground = true,
            Name = "BHS probe registration",
        };

        registration.Start();
    }

    protected override void OnStopping()
    {
        // First: the last chance inside this session to give the person their theme back.
        if (_facts is not null)
            PaneProbe.RestoreThemeOnShutdown(_facts.VersionNumber);

        try
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

            if (_facts is not null && Services is not null)
                Services.Revit.Controlled.DocumentOpened -= OnDocumentOpened;
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
    }

    /// <summary>
    /// Turns a host diagnostic into what travels on the wire.
    /// </summary>
    /// <remarks>
    /// The translation lives here rather than in the host because the host does not reference the
    /// transport: an edition with no channel must not carry Grpc into Revit AppDomain to have a
    /// phase machine. This is the seam where the two meet, and it is three lines.
    /// </remarks>
    private static DiagnosticEvent Translate(RevitDiagnostic diagnostic) => new()
    {
        AtUnixMs = new DateTimeOffset(diagnostic.AtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        // Same numbers on both sides, and the cast is the whole translation: the proto enum is
        // written to match Abstractions.RevitPhase value for value, so a mismatch would be a
        // change somebody made to one of them alone.
        Phase = (BHS.Transport.Protocol.RevitPhase)(int)diagnostic.Phase,
        Caption = diagnostic.Caption,
        Detail = diagnostic.Detail,
        Position = diagnostic.Position,
        Lower = diagnostic.Lower,
        Upper = diagnostic.Upper,
        DialogId = diagnostic.DialogId,
        ApiThread = diagnostic.ApiThread,
    };

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs args)
    {
        try
        {
            var document = args.Document;
            _facts?.SetDocument(document?.Title ?? string.Empty, document?.PathName ?? string.Empty);
            _channel?.Republish();
            ProbeLog.Write("document opened: " + (document?.Title ?? "(none)"));

            // Off unless asked for. Switching the ribbon tab is what made Revit put up
            // "stop the current operation?" - its own journal names the trigger, ProgressCancelled,
            // in the middle of loading the model - and a modal dialog in an unattended sweep is the
            // failure this whole runner exists to avoid. Moving it onto the pump was not enough:
            // Revit reports itself idle between phases of a load that is still running.
            //
            // The question it existed to answer is answered and written down, so the sweep no longer
            // pays for it. BHS_PROBE_SHOW_TAB=1 brings it back for whoever wants to ask again.
            if (Environment.GetEnvironmentVariable("BHS_PROBE_SHOW_TAB") == "1")
                Services?.Ui().Pump.Post("probe: show our ribbon tab", _ => ShowOurTab());
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
    /// What the ribbon proved, kept because the answer decided the shape of the whole arrangement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel and its two buttons are declared in the project file now and built by the host from
    /// the generated manifest. What used to be written here settled two things worth keeping:
    /// </para>
    /// <para>
    /// An <c>IExternalCommandAvailability</c> implementation must live in the command's own assembly.
    /// The API help says it "should"; a button naming a class in <c>BHS.Revit.Common</c> answered the
    /// difference - Revit takes the assembly from the button and resolves the class inside it and
    /// nowhere else, so the type is not found and a <c>TypeLoadException</c> reaches the user as a
    /// modal dialog. "Should" is "must", and the failure is loud rather than a greyed-out button.
    /// It is <c>RVTRIB004</c> now.
    /// </para>
    /// <para>
    /// And naming a class with <c>typeof</c> loads it, which resolves its base, which loads the
    /// feature assembly - while the ribbon is being built. Measured here, before the manifest existed
    /// to make it impossible.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Brings the tab holding our buttons to the front, so that Revit asks about them.
    /// </summary>
    /// <remarks>
    /// Availability is queried while the tab is shown, and an unattended run shows nothing - which
    /// is why the first two sweeps answered "Revit asked neither" and settled nothing. Switching
    /// tabs is not a Revit API operation at all; it belongs to the WPF ribbon underneath, reached
    /// through <c>AdWindows</c>, so it needs no transaction. It does need the UI thread, and it
    /// needs Revit not to be in the middle of something - which is why it is posted to the pump
    /// rather than run from <c>DocumentOpened</c>, where it once provoked a cancel-the-operation
    /// dialog on a loaded machine.
    /// <para>
    /// <b>The tab named <see cref="OwnTabName"/>, not the first tab holding a panel of that title - and
    /// that is a correction.</b> Two manifests now make a <c>Probe feature</c> panel: the probe's own,
    /// on <c>BHS</c>, and the Entry manifest, on whatever tab the host gives it. A host that ignored the
    /// edition's tab would put the Gate button on Add-Ins, in a panel of the same title; whether Add-Ins
    /// or <c>BHS</c> comes first in <c>ribbon.Tabs</c> is not measured, and if Add-Ins did, the first
    /// version activated it and every tab check passed on the wrong tab. So the tab is chosen by name,
    /// and what was activated, which buttons stand on its panel and which other tabs hold a panel of
    /// that title are all recorded here, on the UI thread, for the sweep to assert before it reads the
    /// Entry counters.
    /// </para>
    /// </remarks>
    private static void ShowOurTab()
    {
        try
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;

            if (ribbon is null)
            {
                ProbeLog.Write("ribbon: AdWindows has no ribbon yet");
                return;
            }

            // Id or title: the log has shown the Id of a tab CreateRibbonTab made to be its name, and
            // the title is the other spelling a tab could carry. Both are compared with the same name.
            var own = ribbon.Tabs.FirstOrDefault(tab =>
                (tab.Id == OwnTabName || tab.Title == OwnTabName) && HoldsPanel(tab, OwnPanelTitle));

            var others = ribbon.Tabs
                               .Where(tab => !ReferenceEquals(tab, own) && HoldsPanel(tab, OwnPanelTitle))
                               .Select(tab => string.IsNullOrEmpty(tab.Id) ? tab.Title ?? "?" : tab.Id)
                               .ToList();

            OtherTabsWithOwnPanel = others.Count == 0 ? NoneRecorded : string.Join(" | ", others);

            if (own is null)
            {
                // The control's placement, so that availability can still be asked of something and the
                // sweep has a count to set the missing tab against. Recorded under its own name: the
                // assertion on the Entry button's placement is about which tab this was.
                var fallback = ribbon.Tabs.FirstOrDefault(tab => HoldsPanel(tab, "BHS Probe"));

                if (fallback is null)
                {
                    ActivatedTab = NoneRecorded;
                    ProbeLog.Write("ribbon: could not find the tab holding our panel");
                    return;
                }

                ribbon.ActiveTab = fallback;
                ActivatedTab = string.IsNullOrEmpty(fallback.Id) ? fallback.Title ?? "?" : fallback.Id;
                ProbeLog.Write($"ribbon: no tab '{OwnTabName}' holds '{OwnPanelTitle}'; activated '{ActivatedTab}' holding 'BHS Probe'");
                return;
            }

            // Recorded before the tab is brought forward: activating it is what makes Revit ask the
            // availability classes, and the sweep reads the placement before it reads what that asking
            // counted.
            var items = own.Panels
                           .Where(panel => panel.Source?.Title == OwnPanelTitle)
                           .SelectMany(panel => panel.Source!.Items)
                           .OfType<Autodesk.Windows.RibbonButton>()
                           .ToList();

            OwnPanelButtonIds = string.Join(" | ", items.Select(item => item.Id ?? string.Empty));
            OwnPanelButtonTexts = string.Join(" | ", items.Select(item => item.Text ?? string.Empty));

            // The spelling that matched, so the sweep compares like with like.
            ActivatedTab = own.Id == OwnTabName ? own.Id : own.Title ?? "?";

            ribbon.ActiveTab = own;

            // While we are on the thread that may ask: whether the builder's tab branch really produced
            // a tab, seen on the live ribbon rather than counted by us - and whether the buttons on it
            // came out wearing the placeholder icons.
            OwnTabSeen = true;

            IconsSeen = items.Count > 0
                        && items.TrueForAll(item => item.Image is not null && item.LargeImage is not null);

            ProbeLog.Write($"ribbon: {items.Count} button(s) on '{OwnPanelTitle}', all with icons: {IconsSeen}");
            ProbeLog.Write($"ribbon: button ids there: {OwnPanelButtonIds}; other tabs with that panel: {OtherTabsWithOwnPanel}");

            MeasureIcons(ribbon, items);

            ProbeLog.Write($"ribbon: activated tab '{own.Id}' holding '{OwnPanelTitle}'");
        }
        catch (Exception error)
        {
            ProbeLog.Write("ribbon: could not activate the tab", error);
        }
    }

    private static bool HoldsPanel(Autodesk.Windows.RibbonTab tab, string title) =>
        tab.Panels.Any(panel => panel.Source?.Title == title);

    /// <summary>What a placement fact reads when it was looked for and nothing was there.</summary>
    internal const string NoneRecorded = "(none)";

    /// <summary>The press handler for the Entry button, kept so the channel can report its last outcome.</summary>
    internal static PressButtonHandler? GatePressHandler;

    /// <summary>The press handler for the pane's button, kept for the same reason.</summary>
    internal static PressButtonHandler? PanePressHandler;

    /// <summary>The tab <see cref="ShowOurTab"/> brought forward, by Id; empty until it ran.</summary>
    internal static volatile string ActivatedTab = string.Empty;

    /// <summary>The ids of the buttons on <see cref="OwnPanelTitle"/> on that tab, joined; empty until it ran.</summary>
    /// <remarks>The AdWindows id format for an add-in button is not measured, so the texts go beside it.</remarks>
    internal static volatile string OwnPanelButtonIds = string.Empty;

    /// <summary>The texts of the same buttons, joined, in the same order.</summary>
    internal static volatile string OwnPanelButtonTexts = string.Empty;

    /// <summary>Every other tab holding a panel titled <see cref="OwnPanelTitle"/>; empty until it ran.</summary>
    internal static volatile string OtherTabsWithOwnPanel = string.Empty;

    /// <summary>How many buttons the host built from the manifest. Read by the channel.</summary>
    internal static int ButtonsFromManifest;

    /// <summary>The panel the ping button sits on, on a tab of ours rather than Revit's Add-Ins.</summary>
    internal const string OwnPanelTitle = "Probe feature";

    /// <summary>
    /// Whether the live ribbon was seen holding our own tab, as recorded from the UI thread.
    /// </summary>
    /// <remarks>
    /// <b>Recorded, not asked.</b> The first version of this asked <c>AdWindows</c> directly and was
    /// called over the channel, which measurement has said from the beginning arrives on a pool
    /// thread - so every call threw <c>InvalidOperationException</c> from
    /// <c>RibbonControl.get_Tabs()</c>, WPF refusing a foreign thread, about a hundred and fifty
    /// times per run. The ribbon was fine; the question was asked from the wrong place, which is the
    /// single most repeated mistake in this repository.
    /// </remarks>
    internal static bool OwnTabSeen;

    /// <summary>What the live ribbon does with an icon, measured on the UI thread.</summary>
    /// <remarks>
    /// The question is what to ship for a display at 150% or 200% scaling, and it does not need the
    /// machine set to those to answer: WPF lays a button image out in logical units and resamples the
    /// source to whatever device pixels that comes to, so the required source size is the layout box
    /// times the scale factor. Both halves are readable here, and the arithmetic covers every scale.
    /// <para>
    /// What cannot be inferred and has to be tried is whether the ribbon keeps its layout when handed
    /// a bigger bitmap. <c>ComponentManager.UseOriginalImageSize</c> exists, so the ribbon is capable
    /// of sizing a button to its image instead - and if Revit has that on, a larger icon changes the
    /// geometry of the panel rather than sharpening the picture.
    /// </para>
    /// </remarks>
    internal static readonly Dictionary<string, string> IconFacts = new(StringComparer.Ordinal);

    /// <summary>Whether every button on our own panel was seen carrying both images.</summary>
    /// <remarks>
    /// Asked of the live ribbon, not of the code that set them: <c>PushButtonData</c> accepting an
    /// <c>ImageSource</c> proves nothing about what Revit did with it.
    /// </remarks>
    internal static bool IconsSeen;

    /// <summary>
    /// Measures what the ribbon does with the icons it was given. On the UI thread, always.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first version of this read the layout before asking for one, so the 32-pixel icon came
    /// back "not drawn" while the 64-pixel one came back at 64 - which looks like an answer and is
    /// an artefact. Everything here now measures after an explicit layout pass, and reports every
    /// image the ribbon is drawing rather than the one it was asked about.
    /// </para>
    /// <para>
    /// Three things decide what to ship. <c>Stretch</c> says whether the source size is the drawn
    /// size. <c>DpiX</c> on the bitmap says what "natural size" means - WPF takes it as pixels times
    /// 96/dpi, so a 48-pixel image declared at 144 dpi is 32 units wide and lands exactly on the
    /// device grid at 150%. And the drawn size against the scale factor gives the pixels a display
    /// actually asks for.
    /// </para>
    /// </remarks>
    private static void MeasureIcons(
        Autodesk.Windows.RibbonControl ribbon,
        IReadOnlyList<Autodesk.Windows.RibbonButton> items)
    {
        try
        {
            // Before reading anything. A box that has not been measured is zero, and zero read as an
            // answer is how the first attempt at this reported the opposite of the truth.
            ribbon.UpdateLayout();

            IconFacts["icon:useOriginalImageSize"] =
                Autodesk.Windows.ComponentManager.UseOriginalImageSize.ToString();

            var source = System.Windows.PresentationSource.FromVisual(ribbon);
            var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;
            IconFacts["icon:dpiScale"] = scale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            var button = items[0];
            IconFacts["icon:sourcePixels"] = Describe(button.LargeImage);
            IconFacts["icon:smallSourcePixels"] = Describe(button.Image);

            // The whole finding in two numbers: what we ship and what it comes out as. A 64-pixel
            // source drawn at 32 units means the button did not change size while the picture gained
            // pixels; a 64-pixel source drawn at 64 would mean the opposite, and that is what a file
            // at 96 dpi does - the other entries in the list below are exactly that, as a control.
            IconFacts["icon:smallDrawn"] = DrawnSize(ribbon, button.Image);
            IconFacts["icon:largeDrawn"] = DrawnSize(ribbon, button.LargeImage);
            IconFacts["icon:allDrawn"] = DrawnImages(ribbon);

            // Which of the two variants Revit's current theme asked for. Autodesk's guidelines want
            // both, and a dark mark on a dark ribbon is not an icon.
            IconFacts["icon:theme"] = Autodesk.Revit.UI.UIThemeManager.CurrentTheme.ToString();
            IconFacts["icon:themedIcon"] = BHS.Revit.Host.PlaceholderIcon.Dark ? "dark" : "light";

            foreach (var pair in IconFacts)
                ProbeLog.Write($"{pair.Key} = {pair.Value}");
        }
        catch (Exception error)
        {
            ProbeLog.Write("ribbon: could not measure the icons", error);
        }
    }

    private static string Describe(System.Windows.Media.ImageSource? image) =>
        image is System.Windows.Media.Imaging.BitmapSource bitmap
            ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}@{bitmap.DpiX:F0}dpi"
            : "(none)";

    /// <summary>The size the ribbon drew one particular source at, in logical units.</summary>
    private static string DrawnSize(System.Windows.DependencyObject root, System.Windows.Media.ImageSource? wanted)
    {
        if (wanted is not System.Windows.Media.Imaging.BitmapSource source)
            return "(none)";

        foreach (var element in Descendants(root))
        {
            if (element is System.Windows.Controls.Image image
                && image.Source is System.Windows.Media.Imaging.BitmapSource bitmap
                && bitmap.PixelWidth == source.PixelWidth
                && Math.Abs(bitmap.DpiX - source.DpiX) < 0.5
                && image.ActualWidth > 0)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                return image.ActualWidth.ToString("F0", culture) + "x" + image.ActualHeight.ToString("F0", culture);
            }
        }

        return "(not drawn)";
    }

    /// <summary>
    /// Every image the ribbon is drawing from one of ours, with the size it came out at.
    /// </summary>
    /// <remarks>
    /// Matched by pixel size rather than by reference: the ribbon is free to wrap or convert what it
    /// was handed, and a reference comparison that quietly matches nothing reads as "not drawn".
    /// </remarks>
    private static string DrawnImages(System.Windows.DependencyObject root)
    {
        var seen = new List<string>();

        foreach (var element in Descendants(root))
        {
            if (element is not System.Windows.Controls.Image image)
                continue;

            if (image.Source is not System.Windows.Media.Imaging.BitmapSource bitmap)
                continue;

            if (bitmap.PixelWidth is not (16 or 32 or 48 or 64) || bitmap.PixelWidth != bitmap.PixelHeight)
                continue;

            var culture = System.Globalization.CultureInfo.InvariantCulture;

            seen.Add($"{bitmap.PixelWidth}px@{bitmap.DpiX.ToString("F0", culture)}dpi" +
                     $"->{image.ActualWidth.ToString("F1", culture)}x{image.ActualHeight.ToString("F1", culture)}" +
                     $" stretch={image.Stretch}");
        }

        return seen.Count == 0 ? "(none drawn)" : string.Join(" | ", seen.Distinct());
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);

            yield return child;

            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    /// <summary>Whether the stand-in feature has been loaded, asked without loading it.</summary>
    internal static bool IsFeatureLoaded() => IsLoaded(FeatureAssemblyName);

    /// <summary>Whether an assembly of this simple name is loaded, asked without loading it.</summary>
    internal static bool IsLoaded(string simpleName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == simpleName)
                return true;
        }

        return false;
    }

    /// <summary>The tab the probe's own manifest names for Ping, and the one it gives the edition.</summary>
    internal const string OwnTabName = "BHS";

    /// <summary>The stand-in feature. Named as text: naming a type from it would load it.</summary>
    internal const string FeatureAssemblyName = "BHS.Revit.Probe.Feature";

    /// <summary>The Entry assembly. Named as text, for the same reason and more strictly still.</summary>
    internal const string EntryAssemblyName = "BHS.Revit.Probe.Entry";

    /// <summary>Whether the Entry assembly was already loaded when the probe first looked.</summary>
    /// <remarks>
    /// The first line of <c>OnStarted</c>, which runs straight after the host built the ribbon - so
    /// true here means building the ribbon loaded it: nothing of ours names a type from that assembly,
    /// and the builder hands Revit strings. What that says about Revit is that it loads a button's
    /// assembly when the button is added, rather than when its availability is first asked.
    /// </remarks>
    internal static bool EntryLoadedAtRibbonBuild;

    /// <summary>The same question about the feature assembly, which must be false.</summary>
    internal static bool FeatureLoadedAtRibbonBuild;

    private static long _entryLoadedTicks;
    private static long _featureLoadedTicks;

    /// <summary>When the Entry assembly loaded after the ribbon was built, or null.</summary>
    internal static DateTime? EntryLoadedUtc => Ticks(ref _entryLoadedTicks);

    /// <summary>When the feature assembly loaded after the ribbon was built, or null.</summary>
    internal static DateTime? FeatureLoadedUtc => Ticks(ref _featureLoadedTicks);

    private static DateTime? Ticks(ref long field)
    {
        var ticks = Interlocked.Read(ref field);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Timestamps the two assemblies the Entry experiment is about, as they load. Watches, never
    /// answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AssemblyLoad</c> and not <c>AssemblyResolve</c>, for the reason written down for the
    /// framework's own watcher: the first only looks, the second would be answering other vendors'
    /// resolution in an AppDomain Revit 2024 shares with them.
    /// </para>
    /// <para>
    /// A time rather than a flag, because the question is an order: whether the Entry assembly came in
    /// when Revit first asked its availability class, and whether the feature came in only on the press.
    /// The sweep puts these beside <c>GateRule.FirstCallUtc</c>. It runs for every assembly Revit loads
    /// after startup, so it compares one name and writes nothing to the log; and it never throws, since
    /// a throw here would land in whoever was loading.
    /// </para>
    /// </remarks>
    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            var name = args.LoadedAssembly.GetName().Name;

            if (name == EntryAssemblyName)
                Interlocked.CompareExchange(ref _entryLoadedTicks, DateTime.UtcNow.Ticks, 0);
            else if (name == FeatureAssemblyName)
                Interlocked.CompareExchange(ref _featureLoadedTicks, DateTime.UtcNow.Ticks, 0);
        }
        catch (Exception)
        {
            // Looking must never become a failure inside somebody else's load.
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
