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

/// <summary>
/// Everything the runner asks a Revit that has announced itself.
/// </summary>
/// <remarks>
/// <para>
/// The seam is what the code talks to. <see cref="Program"/> talks to the machine - it picks
/// releases, deploys, starts a process and kills it - while everything here talks to the probe over
/// the channel and reports what came back. They were one file of fourteen hundred lines, the largest
/// and the most-rewritten in the repository, and nothing about the two halves wanted to be together
/// except the order they were written in.
/// </para>
/// <para>
/// Nothing here starts a Revit or kills one. <see cref="CloseAsync"/> is the exception that proves
/// the rule: it asks the probe to close from the inside, which is a question put over the channel
/// like the rest, and killing the one that will not is the caller's business.
/// </para>
/// </remarks>
internal static class SweepChecks
{
    /// <summary>Everything worth asking a Revit that has announced itself.</summary>
    internal static async Task InspectAsync(
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
            string.Equals(instance.Account, Program.CurrentUser, StringComparison.OrdinalIgnoreCase));

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

        // Started here and read at the end. A phase has a beginning and an end, so a watcher that
        // samples sees neither: the first version listened for twenty seconds and recorded one
        // event, because the document it was waiting for arrived a minute after it gave up.
        using var watcher = new DiagnosticsWatcher(instance.PipeName);

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

        var documentArrived = !options.WithModel || await CheckDocumentAsync(client, model, watcher, report);

        // Straight out, before anything else asks the channel a question. Everything below needs a
        // document or a Revit that is answering, and this Revit has neither - so continuing was not
        // resilience, it was a second failure written over the first: measured, the sweep died with
        // an unhandled RpcException inside the model-settings check, which is a worse report than
        // "the document never arrived".
        if (!documentArrived)
        {
            // The stream first. The run that went wrong is the one whose phases are worth reading,
            // and it was the only run that never showed them.
            ReportDiagnostics(watcher, options, report);

            report.Note("not asking it to close", "the document never arrived, and a request now would land mid-operation");
            session.Kill();
            report.Check("the Revit that would not open its model could be killed", !session.IsRunning);

            // Its missing checks are accounted for: this release stopped on purpose, and the floor
            // is there to notice checks that vanish without anyone noticing.
            report.AbandonRelease();
            return;
        }

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

        // Last, because everything above is what the stream was watching. Read any earlier and the
        // measurement would be of the sweep's own beginning rather than of a Revit doing work.
        ReportDiagnostics(watcher, options, report);

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
        await CloseAsync(session, registry, options, report);
    }

    /// <summary>
    /// Says what the diagnostic stream carried, and what it cost.
    /// </summary>
    /// <remarks>
    /// The measurement this mechanism has to survive is its own price. Progress reporting is the
    /// hottest callback in the Revit process during a model load, so the number that matters is not
    /// how much arrived but how much was worth passing on - the publisher thins positions to one
    /// every 250 ms and never thins a change of meaning.
    ///
    /// The longest quiet spell is recorded because it is the number a watchdog would be built on:
    /// waiting on silence only works if you know how long Revit is entitled to be silent.
    /// </remarks>
    private static void ReportDiagnostics(DiagnosticsWatcher watcher, Options options, Report report)
    {
        if (watcher.Failure.Length > 0)
            report.Note("diagnostic stream failed", watcher.Failure);

        report.Check("the diagnostic stream carries what Revit is doing", watcher.Received > 0);
        report.Check("every phase arrived on Revit's API thread", watcher.OffApiThread == 0);
        report.Check("the publisher accounted for everything it dropped", watcher.Gaps == 0);

        report.Note("diagnostic events", watcher.Summary());
        report.Note("phases seen", string.Join(", ", watcher.Phases));

        foreach (var caption in watcher.Captions)
            report.Note("progress caption", caption);

        // The identifier rather than the message: it is stable across languages and releases, and
        // it is what any future policy - or a person reading a failed sweep - would key on. The
        // captions above arrive in Revit's UI language, which is not something to build on.
        foreach (var dialog in watcher.Dialogs)
            report.Note("modal dialog", dialog);

        // Only with a model: a Revit that opens nothing does almost no work, and asking it to prove
        // that a load is visible would be asking about a load that never happened.
        if (options.WithModel)
        {
            // OpeningDocument only. Working was in this condition too, and Working appears on every
            // sweep including the ones that open nothing - so the check passed without a model, which
            // is the one circumstance it exists to rule out.
            report.Check(
                "opening a model is visible as it happens",
                watcher.Phases.Contains(RevitPhase.OpeningDocument));

            report.Check("and so is the document becoming usable", watcher.Phases.Contains(RevitPhase.DocumentReady));
        }
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
            var arrived = await WorkWatch.WaitForAsync(() => configuration["Revit:VersionBuild"] == Value(snapshot, "Revit:VersionBuild"));
            report.Check("what Revit publishes arrives as ordinary configuration", arrived);

            var reloaded = false;
            using (ChangeToken.OnChange(configuration.GetReloadToken, () => reloaded = true))
            {
                var before = configuration["Probe:Publications"];
                await client.AskAsync(new AskRequest { Question = "publish" });

                var updated = await WorkWatch.WaitForAsync(() => configuration["Probe:Publications"] != before);
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
        DiagnosticsWatcher watcher,
        Report report)
    {
        if (model is null)
            return true;

        var expected = Path.GetFileNameWithoutExtension(model);
        var title = string.Empty;
        var started = DateTime.UtcNow;

        // Not a budget any more. Every raise of the old one was paid for by a failure that was not
        // one - two minutes while Revit 2026 opened the model and finished at 3:51, then five
        // minutes on a 2024 whose log dates the document at 11:36 after registration - and each time
        // the lesson was the same: ask whether the work began, rather than how long it has taken.
        // The stream answers that continuously.
        //
        // Sixty seconds of quiet against a measured ten, and the ceiling kept at the old fifteen
        // minutes as a last resort so an unattended sweep still ends.
        var outcome = await WorkWatch.WaitWhileWorkingAsync(
            () =>
            {
                title = Value(client.GetConfiguration(new ConfigurationRequest()), "Document:Title");
                return !string.IsNullOrEmpty(title);
            },
            watcher,
            quiet: TimeSpan.FromSeconds(60),
            ceiling: TimeSpan.FromMinutes(15));

        var arrived = outcome == WaitOutcome.Arrived;

        report.Check("the model given on the command line is opened and reported", arrived);

        if (!arrived)
        {
            // Said in the terms the ending actually has, because the three are answered differently:
            // a person answers a dialog, a hang wants investigating, and a ceiling means the machine
            // was slower than anything measured here.
            report.Note("the wait ended because", outcome switch
            {
                WaitOutcome.Blocked => "Revit is waiting for somebody to answer "
                    + (watcher.BlockedBy.Length > 0 ? watcher.BlockedBy : "a dialog it did not name"),
                WaitOutcome.WentQuiet => "Revit said nothing for 60s while " + watcher.CurrentPhase,
                WaitOutcome.Unreachable => "Revit stopped answering - gone, or held by something that answers for it"
                    + (watcher.BlockedBy.Length > 0 ? ", last seen showing " + watcher.BlockedBy : string.Empty),
                // The ceiling is reached for two different reasons, and saying which matters: with a
                // live stream Revit was demonstrably busy the whole time, while without one - never
                // started, or broken part way - nothing was being watched and the ceiling is all
                // there was. The second reads as a fifteen-minute hang unless it says otherwise.
                _ when watcher.Failure.Length > 0 =>
                    "fifteen minutes passed with the diagnostics stream broken (" + watcher.Failure
                    + "), so silence proved nothing",
                _ => watcher.Received > 0
                    ? "fifteen minutes passed while Revit was still working"
                    : "fifteen minutes passed and diagnostics never spoke, so silence proved nothing",
            });
        }

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
        var initialized = await WorkWatch.WaitForAsync(
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

        var asked = await WorkWatch.WaitForAsync(() =>
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

        await WorkWatch.WaitForAsync(() =>
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
        var pressed = await WorkWatch.WaitForAsync(() =>
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
        var dropped = await WorkWatch.WaitForAsync(() => !registry.TryGet(instance.InstanceId, out _), 10000);
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
    internal static string ProbeLogPath(int processId)
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
