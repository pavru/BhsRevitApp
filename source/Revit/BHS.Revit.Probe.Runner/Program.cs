using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using BHS.Revit.Launch;
using BHS.Transport;
using BHS.Transport.Configuration;
using BHS.Transport.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace BHS.Revit.Probe.Runner;

internal static class Program
{
    private static readonly string CurrentUser = WindowsIdentity.GetCurrent().Name;

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Options.PrintUsage();
            return 2;
        }

        var installed = RevitInstallation.Discover();
        if (installed.Count == 0)
        {
            Console.WriteLine(@"No Revit found under Program Files\Autodesk.");
            return 1;
        }

        if (options.Undeploy)
        {
            ProbeInstaller.Undeploy(installed);
            return 0;
        }

        if (options.Deploy && !TryDeploy(installed))
            return 1;

        var selected = SelectReleases(installed, options);
        if (selected.Count == 0)
            return 1;

        if (!GuardMachineIsFree(selected))
            return 1;

        // What is actually installed for each release about to be swept - not what was just built.
        var deployed = new Dictionary<int, DeployedProbe>();
        foreach (var installation in selected)
            deployed[installation.Release.Year] = ProbeInstaller.Describe(installation);

        var report = new Report();

        using var registry = new RevitInstanceRegistry();
        var channel = new RunnerChannel(registry);

        using var server = PipeTransport.CreateServer(PipeNames.WinSide);
        server.Error += (_, error) => Console.WriteLine($"       win-side server error: {error.Error.Message}");
        WinSideChannel.BindService(server.ServiceBinder, channel);
        server.Start();

        Console.WriteLine($"serving {PipeNames.WinSide} as {CurrentUser}");

        var launcher = new RevitLauncher(registry);

        try
        {
            foreach (var installation in selected)
                await RunAsync(installation, launcher, registry, options, report,
                    deployed.TryGetValue(installation.Release.Year, out var probe) ? probe : null);
        }
        finally
        {
            server.Kill();
        }

        // Anything still here either was never ours or never left, and the two are worth telling
        // apart: a stranger is somebody's own Revit, a leftover is a registry that missed a death.
        foreach (var left in registry.Instances)
        {
            report.Note(
                left.StartedByUs ? "still registered after the sweep" : "another Revit registered without a token of ours",
                $"release {left.Release}, pid {left.ProcessId} - left alone");
        }

        report.Summarise(selected.Count);

        if (options.ReportPath is { Length: > 0 } reportPath)
        {
            var root = ProbeInstaller.FindRepositoryRoot() ?? Environment.CurrentDirectory;
            var (commit, clean) = SweepReport.DescribeWorkingTree(root);

            report.Sweep.Commit = commit;
            report.Sweep.CommitClean = clean;
            report.Sweep.RecordedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            report.Sweep.WithModel = options.WithModel;
            report.Sweep.ShowTab = Environment.GetEnvironmentVariable("BHS_PROBE_SHOW_TAB") == "1";
            report.Sweep.Write(reportPath);
        }

        return report.Failures;
    }

    /// <summary>One release: start it, drive it, close it.</summary>
    /// <remarks>
    /// Starting, waiting and closing all belong to <see cref="RevitLauncher"/> now. They were
    /// written here first and moved out once they worked, which is why the sweep is still their
    /// test: whatever Win-side eventually does with them, this exercises them against four live
    /// Revit releases.
    /// </remarks>
    private static async Task RunAsync(
        RevitInstallation installation,
        RevitLauncher launcher,
        RevitInstanceRegistry registry,
        Options options,
        Report report,
        DeployedProbe? deployed)
    {
        report.BeginRelease(installation.Release.Year, deployed);
        Report.Heading($"Revit {installation.Release} - {installation.ExecutablePath}");

        var model = options.WithModel ? TakeModelCopy(installation, report) : null;

        using var session = await launcher.LaunchAsync(
            installation,
            new RevitLaunchOptions
            {
                RegistrationTimeout = options.RegistrationTimeout,
                ShutdownTimeout = options.ShutdownTimeout,
                ModelPath = model,
            });

        if (session.Process is not null)
            report.Note("launched", "pid " + session.Process.Id.ToString(CultureInfo.InvariantCulture));

        try
        {
            if (!report.Check("the add-in registers over the well-known pipe", session.Registered))
            {
                ExplainFailedLaunch(session, report);
                return;
            }

            report.Note("registered after", session.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

            await InspectAsync(installation, registry, session, options, model, report);
        }
        finally
        {
            Finish(session, options, report);
        }
    }

    /// <summary>Says what to look at when a Revit never announced itself.</summary>
    /// <remarks>
    /// The one path the runner has to explain without being told anything, because a probe that
    /// never registered never got to tell it anything.
    /// </remarks>
    private static void ExplainFailedLaunch(RevitSession session, Report report)
    {
        switch (session.Outcome)
        {
            case RevitLaunchOutcome.NotStarted:
                report.Note("Revit would not start at all", session.Installation.ExecutablePath);
                break;

            case RevitLaunchOutcome.ExitedBeforeRegistering:
                report.Note("Revit exited before registering", "exit code " + ExitCode(session));
                break;

            default:
                report.Note("probe log", ProbeLogPath(session.Process?.Id ?? 0));
                report.Note("no log there means", "the add-in was never loaded - check the Revit journal");
                break;
        }
    }

    private static string ExitCode(RevitSession session)
    {
        try
        {
            return session.Process?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>Everything worth asking a Revit that has announced itself.</summary>
    private static async Task InspectAsync(
        RevitInstallation installation,
        RevitInstanceRegistry registry,
        RevitSession session,
        Options options,
        string? model,
        Report report)
    {
        var instance = session.Instance!;
        var revit = session.Process!;

        // The launcher question, settled from the inside this time: the process that reports for
        // duty is the process that was started.
        report.Check("the registering process is the one that was launched", instance.ProcessId == revit.Id);
        report.Check("the registration names the right release", instance.Release == installation.Release.Year);
        report.Check(
            "the caller is this account",
            string.Equals(instance.Account, CurrentUser, StringComparison.OrdinalIgnoreCase));

        // Layer two, as the registry applied it: a peer is recorded verified only when the
        // operating system agrees the pipe it named is served by the process it claimed.
        report.Check("the registry verified the peer against its pipe", instance.Verified);
        report.Check("the registry knows it was started by us", instance.StartedByUs);
        report.Check("the registry is watching it for exit", registry.Watched.Any(one => one.InstanceId == instance.InstanceId));

        // Layer two again, from the client side, on the raw name rather than through the registry.
        var served = PeerIdentity.TryGetServerProcess(
            instance.PipeName, TimeSpan.FromSeconds(5), out var serverPid, out var image);

        report.Check("a pipe server lives inside the Revit process", served && serverPid == revit.Id);
        report.Check(
            "its image is the Revit that was started",
            image is not null && string.Equals(image, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        await CheckRecoveryAsync(instance, report);

        var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(instance.PipeName));

        var snapshot = await client.GetConfigurationAsync(new ConfigurationRequest());
        report.Check(
            "GetConfiguration is answered from inside Revit",
            Value(snapshot, "Revit:Release") == installation.Release.Year.ToString(CultureInfo.InvariantCulture));

        report.Check("the probe saw the correlation token", Value(snapshot, "Instance:StartedByRunner") == "True");

        var addInDirectory = Value(snapshot, "AddIn:Directory");
        report.Note("build", Value(snapshot, "Revit:VersionBuild") + ", language " + Value(snapshot, "Revit:Language"));
        report.Note("loaded from", addInDirectory);
        report.Note("probe log", Value(snapshot, "AddIn:Log"));

        report.Check("the snapshot names the contract it speaks", snapshot.ContractVersion == Handshake.ContractVersion);

        await CheckConfigurationFlowAsync(instance.PipeName, client, snapshot, report);
        await CheckContextAsync(client, report);
        await CheckSettingsAsync(client, installation, addInDirectory, report);
        await CheckLoggingAsync(client, revit.Id, report);

        var documentArrived = !options.WithModel || await CheckDocumentAsync(client, model, report);

        // After the document, never before it. Revit asks an availability class only while the tab
        // holding the button is shown, and the probe brings that tab forward when a document opens.
        // Asked earlier the counter reads zero and says nothing - which is how the first two runs of
        // this measurement spent twenty minutes of Revit apiece proving nothing.
        await CheckRibbonAsync(client, options, report);

        if (options.WithModel)
            await CheckModelSettingsAsync(client, report);

        // After the ribbon and the model, because one of its questions is about an event that only
        // arrives once Revit has finished starting - and asking a thing that has not happened yet
        // measures the clock, not the thing.
        await CheckDbHostAsync(client, report);

        await CheckAssembliesAsync(client, installation, addInDirectory, report);

        if (options.KeepOpen)
        {
            report.Note("left running", "pid " + revit.Id.ToString(CultureInfo.InvariantCulture));
            return;
        }

        // Never ask a Revit that is still opening something to close.
        //
        // Measured, and it cost a modal dialog on a run nobody was supposed to be watching: the exit
        // command goes into the external event queue, Revit reaches it in the middle of the document
        // it is opening, and asks whether to cancel the operation. Nothing outside the process can
        // answer that, so an unattended sweep would sit in front of it until the deadline - the same
        // failure as the unsigned add-in dialog, arrived at from the other side.
        if (!documentArrived)
        {
            report.Note("not asking it to close", "the document never arrived, and a request now would land mid-operation");
            session.Kill();
            report.Check("the Revit that would not open its model could be killed", !session.IsRunning);
            return;
        }

        await CloseAsync(session, registry, options, report);
    }

    /// <summary>
    /// The streaming path, end to end, with Revit on the publishing side for the first time.
    /// </summary>
    private static async Task CheckConfigurationFlowAsync(
        string pipeName,
        RevitSideChannel.RevitSideChannelClient client,
        ConfigurationSnapshot snapshot,
        Report report)
    {
        var configuration = new ConfigurationBuilder()
            .Add(new PeerConfigurationSource { PipeName = pipeName })
            .Build();

        try
        {
            var arrived = await WaitForAsync(() => configuration["Revit:VersionBuild"] == Value(snapshot, "Revit:VersionBuild"));
            report.Check("what Revit publishes arrives as ordinary configuration", arrived);

            var reloaded = false;
            using (ChangeToken.OnChange(configuration.GetReloadToken, () => reloaded = true))
            {
                var before = configuration["Probe:Publications"];
                await client.AskAsync(new AskRequest { Question = "publish" });

                var updated = await WaitForAsync(() => configuration["Probe:Publications"] != before);
                report.Check("a change streams out of Revit to the consumer", updated);
                report.Check("the change token fired", reloaded);
            }

            report.Check(
                "sections bind",
                configuration.GetSection("Revit")["Release"] == Value(snapshot, "Revit:Release"));
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The other half of configuration: what a Revit-side assembly reads from disk, read inside Revit.
    /// </summary>
    /// <remarks>
    /// Worth a live check rather than a unit test for one reason above the others. The product
    /// layer is meant to be the add-in's own folder, and inside Revit the obvious ways of finding
    /// it - the entry assembly, <c>AppContext.BaseDirectory</c> - both name Revit's installation
    /// directory instead. Nothing outside Revit can tell whether that was got right.
    /// <para>
    /// The two files the runner laid down at deployment say which layer won: the common one names
    /// itself, the per-release one overrules it, and the answer has to name the release this Revit
    /// actually is.
    /// </para>
    /// </remarks>
    private static async Task CheckSettingsAsync(
        RevitSideChannel.RevitSideChannelClient client,
        RevitInstallation installation,
        string addInDirectory,
        Report report)
    {
        var settings = await client.AskAsync(new AskRequest { Question = "settings" });

        if (settings.Values.TryGetValue("settings:error", out var failure))
        {
            report.Check("settings are read from disk inside Revit", false);
            report.Note("settings failed", failure);
            return;
        }

        var release = installation.Release.Year.ToString(CultureInfo.InvariantCulture);

        report.Check(
            "the product layer is the add-in's own folder, not Revit's",
            string.Equals(settings.Values.GetValueOrDefault("settings:product"), addInDirectory, StringComparison.OrdinalIgnoreCase));

        report.Check(
            "a settings file on disk is read inside Revit",
            settings.Values.GetValueOrDefault("value:Probe:Marker") == "product");

        report.Check(
            "the release-specific layer overrules the common one",
            settings.Values.GetValueOrDefault("value:Probe:Layer") == "revit" + release);

        // Nine files - three directories, three names each - and the environment on top of them.
        report.Check(
            "nine files and the environment are looked at, existing or not",
            settings.Values.Count(pair => pair.Key.StartsWith("layer:", StringComparison.Ordinal)) == 10);

        foreach (var layer in settings.Values.Where(pair => pair.Key.StartsWith("layer:", StringComparison.Ordinal))
                                             .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                             .Where(pair => pair.Value.StartsWith("read", StringComparison.Ordinal)))
        {
            report.Note("settings layer", layer.Value);
        }
    }

    /// <summary>
    /// The logging layer, running inside Revit.
    /// </summary>
    /// <remarks>
    /// The file half of this could be checked anywhere. The journal sink could not: it needs a
    /// <c>ControlledApplication</c>, it must attach during <c>OnStartup</c>, and its whole rule -
    /// write only from Revit's API thread - is meaningless outside a process that has one.
    /// <para>
    /// The header is checked too, because it carries the standing requirement to record which copy
    /// of each shared assembly actually loaded. That answer is only available from inside, and it is
    /// the answer both of this repository's worst failures turned out to need.
    /// </para>
    /// </remarks>
    private static async Task CheckLoggingAsync(
        RevitSideChannel.RevitSideChannelClient client,
        int processId,
        Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "log" });
        var path = answer.Values.GetValueOrDefault("log:file") ?? string.Empty;

        var sinks = answer.Values.Where(pair => pair.Key.StartsWith("log:sink:", StringComparison.Ordinal))
                                 .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                 .Select(pair => pair.Value)
                                 .ToList();

        report.Check("the add-in writes a log file inside Revit",
            !string.IsNullOrEmpty(path) && File.Exists(path));

        report.Check("the file is the one findable by process id",
            string.Equals(path, ProbeLogPath(processId), StringComparison.OrdinalIgnoreCase));

        // One, not one per host. The probe now runs two hosts in one process and the first run with
        // both doubled every journal warning - the router is additive on purpose, so a sink it
        // cannot deduplicate has to be guarded by whoever adds it.
        report.Check("the journal sink attached, which needs the API thread",
            sinks.Any(sink => sink.StartsWith("JournalLogSink", StringComparison.Ordinal)));

        report.Check("the journal sink attached exactly once, however many hosts",
            sinks.Count(sink => sink.StartsWith("JournalLogSink", StringComparison.Ordinal)) == 1);

        report.Check("the journal takes warnings and worse only",
            sinks.Any(sink => sink.StartsWith("JournalLogSink >= Warning", StringComparison.Ordinal)));

        report.Check("nothing is being dropped",
            answer.Values.GetValueOrDefault("log:dropped") == "0");

        var text = ReadLog(path);

        // Either in the header or in a later line: the header is a snapshot taken while the host
        // comes up, and the transport is loaded after it. Both forms answer the same question -
        // which copy of a shared assembly this process actually ended up with.
        report.Check("the log records which assemblies actually loaded",
            text.Contains("# assembly:BHS.Transport", StringComparison.Ordinal) ||
            text.Contains("assembly:BHS.Transport", StringComparison.Ordinal));

        report.Check("records are marked with the thread they were written on",
            text.Contains("*]", StringComparison.Ordinal));

        report.Note("log", path);
        report.Note("sinks", string.Join(", ", sinks));
    }

    /// <summary>Reads a log a live Revit still holds open.</summary>
    /// <remarks>
    /// Only possible because the sink opens the file with <c>FileShare.ReadWrite</c>, which is the
    /// one thing that lets Win-side see a running Revit's log at all.
    /// </remarks>
    private static string ReadLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// A copy of the release's empty model, for this run only.
    /// </summary>
    /// <remarks>
    /// A copy, because opening a model can rewrite it - a file from an older release is upgraded on
    /// open - and the originals in <c>testdata</c> should survive being used. Per release, because
    /// which release a model belongs to is not a detail: giving 2027 the 2024 file would upgrade it
    /// and prove nothing about opening.
    /// </remarks>
    private static string? TakeModelCopy(RevitInstallation installation, Report report)
    {
        var year = installation.Release.Year.ToString(CultureInfo.InvariantCulture);
        var root = ProbeInstaller.FindRepositoryRoot();

        if (root is null)
        {
            report.Check($"a test model for Revit {year} is available", false);
            return null;
        }

        var source = Path.Combine(root, "testdata", $"Empty Revit Model {year}.rvt");

        if (!File.Exists(source))
        {
            report.Check($"a test model for Revit {year} is available", false);
            report.Note("expected at", source);
            report.Note("how to make one", "see testdata/readme.md - they are deliberately not in git");
            return null;
        }

        try
        {
            var copy = Path.Combine(Path.GetTempPath(), $"bhs-sweep-{year}-{Guid.NewGuid():N}.rvt");
            File.Copy(source, copy, overwrite: true);
            return copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Check($"a test model for Revit {year} could be copied", false);
            report.Note("why", error.Message);
            return null;
        }
    }

    /// <summary>
    /// The second half of registration: the document, which arrives after it.
    /// </summary>
    /// <remarks>
    /// <c>OnStartup</c> has no document, so registration cannot carry one; what a Revit is working
    /// on comes later, over <c>DocumentOpened</c>, and reaches a consumer as a fresh configuration
    /// snapshot. Nothing else in the sweep exercises that path, and its budget is not the
    /// registration budget - a cold Revit registers long before it has opened anything.
    /// </remarks>
    private static async Task<bool> CheckDocumentAsync(
        RevitSideChannel.RevitSideChannelClient client,
        string? model,
        Report report)
    {
        if (model is null)
            return true;

        var expected = Path.GetFileNameWithoutExtension(model);
        var title = string.Empty;
        var started = DateTime.UtcNow;

        // Fifteen minutes, and every raise of this number has been paid for by a failure that was
        // not one. Two minutes failed while Revit 2026 was opening the model perfectly well - it
        // finished at 3 minutes 51 seconds. Five minutes then failed on Revit 2024 on a machine that
        // had already started Revit four times that hour: the add-in log timestamps the document at
        // 11 minutes 36 seconds after registration, so the work had started, was running, and
        // finished - the budget was simply shorter than the machine.
        //
        // The rule this keeps costing to relearn: before raising a timeout, ask whether the work
        // being waited for ever began. The answer is in the add-in's log and in Revit's journal, not
        // in the stopwatch. It began every time.
        var arrived = await WaitForAsync(() =>
        {
            title = Value(client.GetConfiguration(new ConfigurationRequest()), "Document:Title");
            return !string.IsNullOrEmpty(title);
        }, 900_000);

        report.Check("the model given on the command line is opened and reported", arrived);

        report.Check("and it is the one that was asked for",
            string.Equals(title, expected, StringComparison.OrdinalIgnoreCase));

        report.Note("document", string.IsNullOrEmpty(title) ? "(none)" : title);
        report.Note("opened after registration",
            (DateTime.UtcNow - started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

        try
        {
            File.Delete(model);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Revit still has it open; the temp directory keeps it.
        }

        return arrived;
    }

    /// <summary>
    /// How strict Revit is about where an availability class lives.
    /// </summary>
    /// <remarks>
    /// The API help says an <c>IExternalCommandAvailability</c> implementation "should share the
    /// same assembly with add-in External Command". Advice or rule decides whether the ribbon
    /// generator has to emit a pair of thin classes per command into the edition assembly, or
    /// whether the framework can supply predicates of its own - roughly half the work either way.
    /// <para>
    /// Reported as a measurement rather than a check, because both answers are legitimate; what is
    /// not legitimate is deciding it by reading the sentence twice.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The other shape of add-in: an application with no user interface.
    /// </summary>
    /// <remarks>
    /// Everything said about this form until now came from metadata. Ordinary interactive Revit
    /// loads a <c>DBApplication</c> manifest as readily as an <c>Application</c> one, so this costs
    /// the sweep nothing and no headless engine is involved - and it puts both forms in one process
    /// at once, which is the case the host registry actually has to survive.
    /// </remarks>
    private static async Task CheckDbHostAsync(RevitSideChannel.RevitSideChannelClient client, Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "dbhost" });
        var values = answer.Values;

        report.Check("a DBApplication add-in starts the same host",
            values.GetValueOrDefault("db:started") == "True");

        report.Check("and it is keyed by its own add-in id, beside the interface one",
            string.Equals(values.GetValueOrDefault("db:addInId"), ProbeDeployment.DbAddInId,
                StringComparison.OrdinalIgnoreCase));

        // Looked up by id and compared, not counted. A count of two would also be satisfied by one
        // host that registered twice, which is the failure this is here to catch.
        _ = int.TryParse(values.GetValueOrDefault("db:hostsRegistered"), out var hosts);

        report.Check("both add-in ids are found in the registry",
            values.GetValueOrDefault("db:foundBoth") == "True");

        report.Check("and they are two hosts, not one answering twice",
            values.GetValueOrDefault("db:distinct") == "True");

        // The narrowing is a fact about the host, not a convention: the same registry hands out one
        // with a way onto the API thread and one without, and each is what its form can offer.
        report.Check("the interface host offers the API thread and the DB host does not",
            values.GetValueOrDefault("db:uiHasPump") == "True"
            && values.GetValueOrDefault("db:dbHasPump") == "False");

        report.Check("both forms were called on the same API thread",
            values.GetValueOrDefault("db:apiThread") == values.GetValueOrDefault("db:uiApiThread"));

        report.Check("a module starts in the DB host too",
            values.GetValueOrDefault("db:moduleStarted") == "True");

        report.Check("and it is given its own settings section",
            values.GetValueOrDefault("db:moduleSection") == "product");

        report.Check("startup there is not yet Revit having started, as in the interface form",
            values.GetValueOrDefault("db:initializedAtStart") == "False");

        report.Check("settings from disk are read there too",
            values.GetValueOrDefault("db:settings") == "product");

        // The whole argument for a separate interface over a nullable member: the mismatch has to
        // say what is wrong where it happens, not throw a NullReferenceException somewhere later.
        var refusal = values.GetValueOrDefault("db:uiRefusal") ?? string.Empty;

        report.Check("asking for the API thread there is refused, in a sentence",
            refusal.Contains("no user interface", StringComparison.OrdinalIgnoreCase)
            && refusal.Contains("DBApplication", StringComparison.Ordinal));

        // Waited for rather than read once - the rule this repository bought with three wrong
        // measurements of the availability class. It was a note until it was measured; the answer
        // was yes, and it cost the context a member name: what fires here has nothing to do with a
        // session, so the framework calls it "initialized" and raises it for both forms.
        var initialized = await WaitForAsync(
            () => client.Ask(new AskRequest { Question = "dbhost" })
                        .Values.GetValueOrDefault("db:initialized") == "True",
            30_000);

        report.Check("and Revit tells it that it has finished starting, session or no session",
            initialized);

        report.Note("db host", $"add-in {values.GetValueOrDefault("db:addInId")}, {hosts} host(s) registered");
    }

    private static async Task CheckRibbonAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Options options,
        Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "ribbon" });
        report.Check("the ribbon panel and button were built", answer.Values.ContainsKey("ribbon:availabilityCalls"));

        // From the file the SDK wrote beside the assembly, not from a list in code. Both buttons, or
        // the count is wrong and something silently skipped one.
        report.Check("and they came from the generated manifest, not from code",
            answer.Values.GetValueOrDefault("ribbon:fromManifest") == "2");


        // Only when the tab has been brought forward on purpose. Revit asks an availability class
        // while its tab is shown, and showing it turned out to provoke a cancel-the-operation dialog
        // in the middle of a model load - its own journal names ProgressCancelled - so the sweep no
        // longer does it, and a count of zero here would be silence rather than an answer.
        // The half that costs nothing and answers the question that matters most here: a ribbon
        // built from plain type names must not have loaded the assemblies behind them. On Revit 2024
        // an assembly that loads holds its name in the AppDomain shared with every vendor for the
        // rest of the session, including features nobody touched.
        report.Check("the feature assembly is not loaded while the ribbon stands",
            answer.Values.GetValueOrDefault("ribbon:featureLoaded") == "False");

        if (!options.WithModel || Environment.GetEnvironmentVariable("BHS_PROBE_SHOW_TAB") != "1")
        {
            report.Note("availability and the press", "not asked - set BHS_PROBE_SHOW_TAB=1 for both");
            return;
        }

        // Waited for, not read once. The probe republishes the document title before it activates
        // the tab, so the moment this check becomes reachable is half a second before the answer
        // exists - which is exactly how the previous run reported zero while the log said otherwise.
        var calls = "0";

        var asked = await WaitForAsync(() =>
        {
            calls = client.Ask(new AskRequest { Question = "ribbon" })
                          .Values.GetValueOrDefault("ribbon:availabilityCalls") ?? "0";
            return calls != "0";
        }, 60_000);

        report.Check("Revit asks the availability class once its tab is shown", asked);
        report.Note("availability calls", calls);

        // Here rather than beside the manifest check, because the answer is recorded by the code that
        // brings the tab forward - the only place in the probe already on the UI thread. One button
        // goes to Revit's own Add-Ins tab and one to a tab of ours, which is the only way the
        // builder's tab branch runs at all: creating a tab, surviving one that exists already and
        // putting a panel on it were written and never executed until a button asked for them.
        // Waited for, not read once. The icon measurement happens inside the same pump action that
        // brings the tab forward, and the flags it sets before measuring are visible first - so a
        // single read caught Revit 2026 between the two and reported an empty measurement as a
        // failed one. The rule keeps having to be relearned in new places: a condition that arrives
        // asynchronously is waited for.
        var ribbon = new Dictionary<string, string>(StringComparer.Ordinal);

        await WaitForAsync(() =>
        {
            ribbon = new Dictionary<string, string>(
                client.Ask(new AskRequest { Question = "ribbon" }).Values, StringComparer.Ordinal);

            return ribbon.ContainsKey("icon:largeDrawn");
        }, 30_000);

        report.Check("a tab of our own was created and holds its panel",
            ribbon.GetValueOrDefault("ribbon:ownTab") == "True");

        // Asked of the live ribbon rather than of the builder. A button with no image gets an empty
        // frame from Revit, and a panel of those reads as broken rather than unfinished - so until a
        // feature draws its own, the framework puts the vendor mark on every button.
        report.Check("and every button on it wears an icon",
            ribbon.GetValueOrDefault("ribbon:icons") == "True");

        // Was a note while nobody knew the answer; an assertion now that the measurement gave one.
        // Revit's ribbon draws icons with Stretch=None, so the drawn size is the source's natural
        // size - pixels times 96/dpi - and a plain 64-pixel file makes a 64-unit button rather than
        // a sharper 32-unit one. Declaring 192 dpi keeps the button at 32 units and doubles the
        // pixels available to a display that has them, which is the whole answer for 150% and 200%.
        report.Check("both icons ship twice the pixels at twice the declared dpi",
            ribbon.GetValueOrDefault("icon:smallSourcePixels") == "32x32@192dpi"
            && ribbon.GetValueOrDefault("icon:sourcePixels") == "64x64@192dpi");

        // Only the large one is asserted, and the reason is the ribbon's, not ours: a large button
        // draws LargeImage and nothing else, so the small image is never laid out here and has no
        // drawn size to check. Asserting one anyway would be asserting the absence of a button.
        report.Check("and the large one draws at 32 units, so the button did not grow",
            ribbon.GetValueOrDefault("icon:largeDrawn") == "32x32");

        // The variant follows Revit rather than a guess. Both are shipped, and the live buttons are
        // repainted when the theme changes - which is why this is a check and not a note.
        report.Check("the icon variant matches Revit's theme",
            ribbon.GetValueOrDefault("icon:themedIcon")
                == (ribbon.GetValueOrDefault("icon:theme") == "Dark" ? "dark" : "light"));

        foreach (var key in new[]
                 {
                     "icon:dpiScale", "icon:useOriginalImageSize", "icon:theme", "icon:smallDrawn",
                     "icon:allDrawn",
                 })
        {
            if (ribbon.TryGetValue(key, out var value))
                report.Note(key, value);
        }

        await CheckFeatureCommandAsync(client, report);
    }

    /// <summary>
    /// A command Revit built itself, finding its host and loading its feature to do it.
    /// </summary>
    /// <remarks>
    /// Three answers in one press, and none of them reachable any other way. Whether the registry
    /// keyed by add-in id is found from inside a command Revit constructed from a string; what
    /// <c>ActiveAddInId</c> actually returns there, which was documented and never measured; and
    /// whether the feature assembly stays unloaded until the button is used.
    /// </remarks>
    private static async Task CheckFeatureCommandAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        var runs = "0";
        var addInId = "(none)";
        var loaded = "False";
        var attempts = 0;

        // Pressed more than once, for the reason the close needed the same: a posted command is
        // dropped when Revit is not ready for it, silently and with nothing anywhere to say so. The
        // first attempt here landed half a second after the ribbon tab was brought forward and went
        // nowhere.
        var pressed = await WaitForAsync(() =>
        {
            if (attempts == 0 || attempts % 20 == 0)
                client.Ask(new AskRequest { Question = "press" });

            attempts++;

            var values = client.Ask(new AskRequest { Question = "ribbon" }).Values;
            runs = values.GetValueOrDefault("ribbon:pingRuns") ?? "0";
            addInId = values.GetValueOrDefault("ribbon:pingAddInId") ?? "(none)";
            loaded = values.GetValueOrDefault("ribbon:featureLoaded") ?? "False";
            return runs != "0";
        }, 90_000);

        report.Check("a feature command runs through a one-line entry point", pressed);

        report.Check("and it found its own host, keyed by add-in id",
            string.Equals(addInId, ProbeDeployment.AddInId, StringComparison.OrdinalIgnoreCase));

        report.Check("and the feature assembly loaded only once it was needed", loaded == "True");

        report.Note("feature command", $"runs {runs}, host {addInId}");
    }

    /// <summary>
    /// Settings that belong to the document, written into it and read back.
    /// </summary>
    /// <remarks>
    /// The whole path in one check: a channel call reaching the API thread through the pump, a
    /// transaction, Extensible Storage written and read, and the project chain preferring what the
    /// model says. None of it can be measured from outside Revit, and the storage half cannot be
    /// measured without a document, which is why this runs only with one open.
    /// </remarks>
    private static async Task CheckModelSettingsAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "model" });
        var document = answer.Values.GetValueOrDefault("model:document") ?? "(none)";

        if (!report.Check("the pump reaches the API thread and finds the open document", document != "(none)"))
            return;

        report.Check("a project setting starts unset in a fresh model",
            answer.Values.GetValueOrDefault("model:before") == "(unset)");

        report.Check("it is written into the model and read back",
            answer.Values.GetValueOrDefault("model:after") == "written-by-the-probe");

        report.Check("and the model is then named as where it came from",
            answer.Values.GetValueOrDefault("model:origin") == "Model");

        report.Check("a key without the project prefix is not taken from the model",
            answer.Values.GetValueOrDefault("model:userScoped") != "written-by-the-probe");

        // The subtle half, and the reason the schema has a third field: Extensible Storage refuses
        // a null, so a removed key travels as a name in a list and is unfolded back into key -> null
        // on reading. Without this the overlay would let a cleared value fall through to the layer
        // below and the mechanism would look like it worked.
        report.Check("the product layer really did set the key that is about to be cleared",
            answer.Values.GetValueOrDefault("model:clearedBefore") == "set-by-the-product-layer");

        report.Check("and a project can clear what the vendor's own file set",
            answer.Values.GetValueOrDefault("model:clearedAfter") == "(unset)");

        // The sweep must leave nothing behind to be asked about. A written document is a modified
        // one, and Revit asks whether to save it on the way out - which nothing outside the process
        // can answer.
        report.Check("and the document is left unmodified, so nothing is asked on the way out",
            answer.Values.GetValueOrDefault("model:clean") == "True");

        report.Note("model settings", $"document {document}, origin {answer.Values.GetValueOrDefault("model:origin")}");
    }

    /// <summary>Where the call lands, and in which AppDomain the add-in is living.</summary>
    private static async Task CheckContextAsync(RevitSideChannel.RevitSideChannelClient client, Report report)
    {
        var context = await client.AskAsync(new AskRequest { Question = "context" });

        var api = context.Values["thread:api"];
        var calling = context.Values["thread:calling"];

        // Not a defect: it is the constraint every Revit-side feature has to be built around, and
        // the reason closing Revit goes through an external event rather than a direct call.
        report.Check("the call arrives off the Revit API thread", api != calling);

        report.Note("threads", $"api {api}, call {calling}");
        report.Note("appdomain", $"{context.Values["appdomain:name"]} (#{context.Values["appdomain:id"]})");
        report.Note("load context", context.Values.GetValueOrDefault("loadcontext") ?? "(not reported)");
        report.Note("runtime", context.Values["runtime"]);
    }

    /// <summary>
    /// Which copy of each assembly we ship actually got loaded.
    /// </summary>
    /// <remarks>
    /// The reason the probe exists. Revit ships its own <c>Grpc.Core.Api</c> and
    /// <c>Google.Protobuf</c> from 2025 on, loads them before any add-in, and whoever loads first
    /// wins; on Revit 2024 there is not even an AppDomain boundary to argue across. RefCheck
    /// compares surfaces at build time and cannot say which file is open at run time.
    /// <para>
    /// What we ship is read from the deployed folder rather than listed here, so the check follows
    /// the dependency graph wherever it goes next.
    /// </para>
    /// </remarks>
    private static async Task CheckAssembliesAsync(
        RevitSideChannel.RevitSideChannelClient client,
        RevitInstallation installation,
        string addInDirectory,
        Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "assemblies" });

        var shipped = ShippedAssemblies(installation);
        var shadowed = new List<string>();

        foreach (var pair in answer.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!pair.Key.StartsWith("assembly:", StringComparison.Ordinal) || pair.Key == "assembly:count")
                continue;

            var name = pair.Key.Substring("assembly:".Length);
            var separator = pair.Value.LastIndexOf(" | ", StringComparison.Ordinal);
            var version = separator < 0 ? pair.Value : pair.Value.Substring(0, separator);
            var location = separator < 0 ? string.Empty : pair.Value.Substring(separator + 3);

            var ours = IsUnder(location, addInDirectory);
            var revits = IsUnder(location, installation.InstallDirectory);
            var origin = ours ? "ours" : revits ? "REVIT" : "elsewhere";

            report.Note($"{name} {version}", $"{origin}  {location}");

            if (shipped.Contains(name) && !ours)
                shadowed.Add(name);
        }

        report.Note("assemblies loaded", answer.Values["assembly:count"]);

        foreach (var name in shadowed)
            report.Note("Revit's copy won", name);

        // Substitution itself is not the question - Revit loads first and always wins. The question
        // is whether the copy that won has been checked, and RefCheck's watchlist is the record of
        // that. Anything substituted from outside the list went unverified.
        var vetted = RefCheckWatchlist.TryLoad(ProbeInstaller.FindRepositoryRoot());

        if (vetted is null)
        {
            report.Note("RefCheck watchlist", "not readable from here, so substitutions are unjudged");
            return;
        }

        var unverified = shadowed.Where(name => !vetted.Contains(name)).ToList();
        report.Check("every assembly Revit substituted is one RefCheck vets", unverified.Count == 0);

        foreach (var name in unverified)
            report.Note("substituted but not on the watchlist", name);
    }

    /// <summary>
    /// What a Win-side that lost its registry can rebuild, while the instance is still running.
    /// </summary>
    /// <remarks>
    /// A second registry, deliberately empty, standing in for a restarted Win-side. It is the only
    /// way to exercise the recovery path against a real Revit: enumeration, the check that the
    /// name is served by the process it is named after, and the call that makes a live peer say
    /// which contract it speaks.
    /// </remarks>
    private static async Task CheckRecoveryAsync(RevitInstance instance, Report report)
    {
        using var restarted = new RevitInstanceRegistry();
        var found = await restarted.RecoverAsync();

        var recovered = restarted.Instances.FirstOrDefault(one => one.ProcessId == instance.ProcessId);

        report.Check("a registry that lost everything finds it again by enumeration", recovered is not null);

        if (recovered is null)
            return;

        report.Check("the recovered instance is the same one", recovered.InstanceId == instance.InstanceId);
        report.Check("recovery reads the release out of the pipe name", recovered.Release == instance.Release);

        // The token lives in the registration message and in the started process's environment,
        // and enumeration reaches neither. Recovering an instance therefore cannot recover the
        // claim that we started it - which is the point of checking rather than assuming.
        report.Check("a recovered instance is never claimed as ours", !recovered.StartedByUs);

        report.Note("recovered by enumeration", found.ToString(CultureInfo.InvariantCulture) + " instance(s)");
    }

    /// <summary>Close it the way it is meant to be closed, and measure how long that takes.</summary>
    private static async Task CloseAsync(
        RevitSession session,
        RevitInstanceRegistry registry,
        Options options,
        Report report)
    {
        var instance = session.Instance!;
        var closing = Stopwatch.StartNew();

        var exited = await session.CloseAsync(options.ShutdownTimeout, "probe sweep finished");
        report.Check("Revit closes on an ExitRevit posted from inside", exited);

        if (!exited)
            return;

        report.Note("closed after", closing.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

        // Nothing told the registry; it was watching the process. A departure that has to be swept
        // for is a departure nobody hears about until somebody asks.
        var dropped = await WaitForAsync(() => !registry.TryGet(instance.InstanceId, out _), 10000);
        report.Check("the registry drops it when the process goes", dropped);

        // Only readable now, and only from the file: OnShutdown runs while Revit is leaving, so
        // there is no channel left to ask over. The DB form's way down is the half of its life that
        // nothing else in this sweep touches.
        var log = ReadLog(ProbeLogPath(instance.ProcessId));

        report.Check("the DB host is taken down on the way out, like the interface one",
            log.Contains("BHS.Revit.Probe.Db stopped", StringComparison.Ordinal));

        report.Check("and its module is stopped with it",
            log.Contains("the DB half's module stopped", StringComparison.Ordinal));
    }

    private static HashSet<string> ShippedAssemblies(RevitInstallation installation)
    {
        var lib = Path.Combine(ProbeInstaller.ProbeDirectory(installation), "Lib");
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(lib))
            return shipped;

        foreach (var file in Directory.GetFiles(lib, "*.dll"))
            shipped.Add(Path.GetFileNameWithoutExtension(file));

        return shipped;
    }

    /// <summary>Polls until a condition holds, or until the time runs out.</summary>
    /// <remarks>
    /// For the things that happen a moment after something else - configuration arriving, an
    /// instance leaving the registry. Everything worth waiting minutes for has a handle to watch
    /// instead, and that waiting lives in <see cref="RevitLauncher"/>.
    /// </remarks>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int millisecondsTimeout = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millisecondsTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>Kills a Revit that would otherwise be left behind holding a licence seat.</summary>
    private static void Finish(RevitSession session, Options options, Report report)
    {
        if (options.KeepOpen || !session.IsRunning)
            return;

        report.Note("killing", "pid " + (session.Process?.Id.ToString(CultureInfo.InvariantCulture) ?? "?")
                               + " - it did not close on its own");

        report.Check("the leftover Revit could be killed", session.Kill());
    }

    private static bool TryDeploy(IReadOnlyList<RevitInstallation> installed)
    {
        var root = ProbeInstaller.FindRepositoryRoot();

        if (root is null)
        {
            Console.WriteLine("--deploy needs the repository, and this build is not inside one.");
            return false;
        }

        if (ProbeInstaller.Deploy(root, installed))
            return true;

        Console.WriteLine("The build failed, so nothing was installed.");
        return false;
    }

    private static List<RevitInstallation> SelectReleases(IReadOnlyList<RevitInstallation> installed, Options options)
    {
        var selected = options.Releases.Count == 0
            ? installed.ToList()
            : installed.Where(installation => options.Releases.Contains(installation.Release)).ToList();

        foreach (var asked in options.Releases.Where(asked => selected.All(found => found.Release != asked)))
            Console.WriteLine($"Revit {asked} is not installed.");

        var missing = selected.Where(installation => !ProbeInstaller.IsDeployed(installation)).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine("The probe is not installed for: " + string.Join(", ", missing.Select(one => one.Release)));
            Console.WriteLine("Install it with --deploy, or by hand:");
            Console.WriteLine("  dotnet build " + ProbeDeployment.ProjectPath + " -c Release -p:RevitDeploy=Local");
            return new List<RevitInstallation>();
        }

        // Checked rather than assumed, because the failure is silent from out here: an unsigned
        // add-in Revit has not been told to trust brings up a modal dialog whose default answer is
        // "do not load", before any add-in gets its OnStartup. The sweep would wait out its whole
        // deadline in front of it and report that the probe never registered.
        //
        // Not only our own add-in. The dialog belongs to whichever add-in is untrusted, and a
        // machine somebody actually works on collects them.
        var blocked = selected
            .Select(installation => (Installation: installation, Untrusted: AddInTrust.Untrusted(installation)))
            .Where(pair => pair.Untrusted.Count > 0)
            .ToList();

        if (blocked.Count == 0)
            return selected;

        foreach (var (installation, untrusted) in blocked)
        {
            Console.WriteLine($"Revit {installation.Release} would stop and ask about:");

            foreach (var addIn in untrusted)
            {
                var ours = ProbeDeployment.AddInIds.Contains(addIn.AddInId, StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"  {addIn}{(ours ? "  <- ours, run --deploy" : string.Empty)}");
            }
        }

        if (options.AllowUntrusted)
        {
            Console.WriteLine("continuing anyway - answer each dialog with \"Always load\" as it appears.");
            return selected;
        }

        Console.WriteLine();
        Console.WriteLine("Trust them in Revit (\"Always load\"), uninstall them, or pass --allow-untrusted");
        Console.WriteLine("to answer the dialogs by hand.");
        return new List<RevitInstallation>();
    }

    /// <summary>
    /// Refuses to run when a Revit is already open, or when someone else owns the well-known name.
    /// </summary>
    /// <remarks>
    /// Both are about not disturbing what is already there. The sweep kills what it started, and
    /// telling its own Revit from somebody's work in progress after the fact is not a game worth
    /// playing. The name matters for a different reason: the library never sets
    /// <c>FirstPipeInstance</c> - it does not exist on .NET Framework at all - so a second server
    /// on the same name would coexist silently and connections would land on either.
    /// </remarks>
    private static bool GuardMachineIsFree(IReadOnlyList<RevitInstallation> selected)
    {
        var running = Process.GetProcessesByName("Revit");

        if (running.Length > 0)
        {
            Console.WriteLine($"Revit is already running ({running.Length} process(es)). Close it first: the sweep starts and kills its own.");
            return false;
        }

        if (PipeNames.Enumerate().Contains(PipeNames.WinSide))
        {
            Console.WriteLine($"Something is already serving {PipeNames.WinSide}. Two servers on one name coexist silently, so this run would be unreliable.");
            return false;
        }

        Console.WriteLine("sweeping: " + string.Join(", ", selected.Select(one => one.Release)));
        return true;
    }

    /// <summary>Where the probe writes, worked out the same way the probe works it out.</summary>
    /// <summary>
    /// The log a Revit process wrote, found by its process id.
    /// </summary>
    /// <remarks>
    /// A pattern rather than a fixed name: the file also carries the moment the process started, so
    /// that two runs of the same release cannot overwrite one another. The process id is what makes
    /// it findable from outside, which matters most in the case this is used for - a Revit that
    /// never registered and therefore never answered anything.
    /// </remarks>
    private static string ProbeLogPath(int processId)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BHS", "Logs");

        try
        {
            var match = Directory.EnumerateFiles(directory, $"*-{processId.ToString(CultureInfo.InvariantCulture)}-*.log")
                                 .OrderByDescending(File.GetLastWriteTimeUtc)
                                 .FirstOrDefault();

            return match ?? Path.Combine(directory, $"(no log for pid {processId})");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return directory;
        }
    }

    private static bool IsUnder(string path, string directory) =>
        !string.IsNullOrEmpty(path)
        && !string.IsNullOrEmpty(directory)
        && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);

    private static string Value(ConfigurationSnapshot snapshot, string key) =>
        snapshot.Values.TryGetValue(key, out var value) ? value : string.Empty;
}
