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
    /// <summary>
    /// How long Revit is entitled to say nothing before a wait calls it a hang.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised from sixty seconds on the third and fourth readings of the same quantity. The first
    /// two runs put the worst silence at 11.5 s and 8.3 s, so sixty looked like a fivefold margin;
    /// the runs of 2026-09-09 measured 50.9 s and then 53.8 s on Revit 2026, back to back. A value
    /// that holds where it stands twice is that release's working norm on this machine, not an
    /// outlier - and the threshold was sitting six seconds above it.
    /// </para>
    /// <para>
    /// Three times over the worst measured, because this repository has raised four budgets and
    /// every one of them was paid for by a failure that was not one. The rule those cost is to
    /// count from the upper bound and not to shave it: a threshold tuned to a lucky run is a false
    /// alarm postponed. It is still six times faster than the 900 s budget the watchdog replaced,
    /// which is the whole point of watching work rather than the clock.
    /// </para>
    /// <para>
    /// Said once so that the number and the sentence reporting it cannot drift apart - two places
    /// stating one fact is how this file's own documentation came to say <c>2 of 2</c> after the
    /// third check was made required.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(150);
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
        await CheckRibbonAsync(client, options, watcher, report);

        if (options.WithModel)
        {
            await CheckModelSettingsAsync(client, report);

            // Right after it, and for the same reason: both need a document, and this one writes
            // into it. Kept apart because they answer different questions - one that the mechanism
            // works, one how permanent its identity is.
            await CheckSchemaEvolutionAsync(client, report);

            // Last of the document questions, and the only one that mostly asks rather than asserts.
            await SurveyCablingAsync(client, report);

            // And the newest of them, which asks rather than asserts for the same reason the survey
            // did when it was new: nobody here knows yet what the owner's indicator family is.
            await RecommendedBoxAsync(client, report);
        }

        // Outside the model block: a modal window stands on the API thread whether or not a document
        // is open, and the one fact that changes without one is what a read returns.
        await CheckModalWindowAsync(client, report);

        // Same shape, same reason: writing the files needs no document at all, and only the binding
        // half does. Both halves say so rather than falling silent.
        await CheckSharedParametersAsync(client, report);

        // After the ribbon and the model, because one of its questions is about an event that only
        // arrives once Revit has finished starting - and asking a thing that has not happened yet
        // measures the clock, not the thing.
        await CheckDbHostAsync(client, report);

        // Outside the model block on purpose. A case that needs a document and has none is reported
        // skipped, by name and with the reason - which is the whole difference between a check that
        // was not asked and a check that quietly disappeared. One case in the first suite needs no
        // document at all, so a plain sweep still proves the harness is alive.
        await CheckDeclaredTestsAsync(client, report);

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
        // Quiet is what Revit is allowed to withhold; the ceiling stays at the old fifteen minutes
        // as a last resort so an unattended sweep still ends.
        var outcome = await WorkWatch.WaitWhileWorkingAsync(
            () =>
            {
                title = Value(client.GetConfiguration(new ConfigurationRequest()), "Document:Title");
                return !string.IsNullOrEmpty(title);
            },
            watcher,
            quiet: Quiet,
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
                WaitOutcome.WentQuiet => "Revit said nothing for "
                    + Quiet.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s while " + watcher.CurrentPhase,
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

        return arrived;
    }

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
    private static async Task CheckRibbonAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Options options,
        DiagnosticsWatcher watcher,
        Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "ribbon" });
        report.Check("the ribbon panel and button were built", answer.Values.ContainsKey("ribbon:availabilityCalls"));

        // From the files the SDK wrote beside the assemblies, not from a list in code: the probe's own
        // two buttons and the two that arrived beside BHS.Revit.Probe.Entry - Gate and, since dockable
        // panes, the pane's toggle. All four, or the count is wrong and something silently skipped one -
        // and the likeliest to go missing are the Entry pair, which the host builds only because the
        // probe's Modules list declares its feature, and which reach the folder only as a related file
        // of an assembly nothing in the probe names.
        report.Check("and they came from the generated manifest, not from code",
            answer.Values.GetValueOrDefault("ribbon:fromManifest") == "4");


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

        // Before anything below can show the pane: its laziness can only be asked while nobody has.
        await CheckPaneRegisteredAsync(client, report);

        if (!options.WithModel || Environment.GetEnvironmentVariable("BHS_PROBE_SHOW_TAB") != "1")
        {
            report.Note("availability and the press", "not asked - set BHS_PROBE_SHOW_TAB=1 for both");

            // The one Entry question an unattended sweep can answer, because it needs the tab NOT to be
            // shown: does Revit load a button's assembly when the button is added, or only once it asks
            // the button's availability class? Nothing of ours names a type from the Entry assembly, so
            // loaded here is Revit's doing. A note, because either answer is compatible with the design
            // - it decides only when the Entry and declaration assemblies arrive, not whether the
            // feature does - and a check written before its answer agrees with whoever wrote it.
            report.Note("Entry assembly while the ribbon stands and its tab was never shown",
                "loaded " + (answer.Values.GetValueOrDefault("ribbon:entryLoaded") ?? "(missing)")
                + ", already when the ribbon was built " + (answer.Values.GetValueOrDefault("ribbon:entryLoadedAtRibbonBuild") ?? "(missing)")
                + ", its rule asked " + (answer.Values.GetValueOrDefault("ribbon:gateCalls") ?? "(missing)") + " time(s)");

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

        // Where the Entry button landed, asserted before its counters are read - and that order is the
        // point. The control above is shared: LocalAvailability sits behind Ping on the edition's tab and
        // behind Probe.Command on Add-Ins, so its count says a tab was shown and not which one. Without
        // this, a zero from the Entry rule could mean Revit refused the Entry class or that Gate was never
        // on the tab that was shown, and nothing would tell the two apart. Recorded by the code that
        // brought the tab forward, on the UI thread, so it is waited for rather than read once.
        await CheckEntryPlacementAsync(client, report);

        // Straight after the placement and before anything is pressed. With Gate seen on the shown tab
        // beside Ping, a count for the control and none for the Entry rule says Revit refused the Entry
        // class rather than never asked. And no press may come first - Ping's loads the feature assembly,
        // and then "still not loaded" could not be asked of anything.
        await CheckEntryAvailabilityAsync(client, report);

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

        // After Ping, so that Ping stays the control it was: its press is what proves the feature
        // assembly loads on a press, and a Gate press first would have loaded it on Ping's behalf.
        await CheckEntryCommandAsync(client, watcher, report);

        // Last: showing a pane is interface work, and everything above wanted the ribbon undisturbed.
        await CheckPaneShownAsync(client, report);
    }

    /// <summary>
    /// The probe pane as it stands before anybody shows it: registered, and nothing behind it loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same question the ribbon asks, one level over.</b> A pane is registered at startup from
    /// strings, and its content assembly - and WPF-UI, which only the host's pane shell names - must stay
    /// out of the AppDomain until Revit first asks for the element. On Revit 2024 an assembly that loads
    /// holds its simple name for the rest of the session against every vendor, so a pane nobody opens
    /// must cost nothing but its registration.
    /// </para>
    /// <para>
    /// <b>Skipped out loud when Revit restored the pane as shown.</b> Revit keeps a pane's visibility from
    /// the previous session - by the documentation of <c>VisibleByDefault</c>, not measured - so a sweep
    /// that died with the pane open starts the next Revit with the content already asked for. That is
    /// not laziness failing; it is a question nobody could ask this time, and saying so is the
    /// difference between a skip and a pass. The extended mode hides the pane before it leaves.
    /// </para>
    /// <para>
    /// Everything about when Revit calls setup, and on which thread, goes into notes: none of it is
    /// known, and a check written before its answer agrees with whoever wrote it.
    /// </para>
    /// </remarks>
    private static async Task CheckPaneRegisteredAsync(RevitSideChannel.RevitSideChannelClient client, Report report)
    {
        var pane = (await client.AskAsync(new AskRequest { Question = "pane" })).Values;

        report.Check("the probe pane is registered with Revit",
            pane.GetValueOrDefault("pane:registered") == "True" && pane.GetValueOrDefault("pane:inRegistry") == "True");

        // Asked is not shown: measured on 2026, Revit calls the creator while it opens a model with the
        // pane never shown. So the creator's calls are a note, and the check is about what matters - the
        // content assembly and Wpf.Ui stay out until somebody sees the pane. Only a pane Revit restored as
        // shown makes the question unaskable.
        var restoredShown = pane.GetValueOrDefault("pane:shown") == "True";

        if (restoredShown)
        {
            report.Note("pane laziness",
                "not asked - Revit restored the pane as shown, most likely because an earlier run left it open; " +
                "creator calls " + (pane.GetValueOrDefault("pane:creatorCalls") ?? "(missing)"));
        }
        else
        {
            report.Check("and its content is not loaded while nobody showed it",
                pane.GetValueOrDefault("pane:contentLoaded") == "False");

            report.Check("and WPF-UI is not loaded while no pane has been shown",
                pane.GetValueOrDefault("pane:wpfUiLoaded") == "False");
        }

        foreach (var key in new[]
                 {
                     "pane:exists", "pane:shown", "pane:title", "pane:creatorCalls", "pane:setupCalls", "pane:setupDuringRegistration",
                     "pane:setupThread", "pane:apiThread", "pane:contentLoadedAtStartup", "pane:wpfUiLoadedAtStartup",
                     "pane:revitLanguage", "pane:shellCulture", "pane:shellNoDocument", "pane:revitTheme",
                 })
        {
            report.Note(key, pane.GetValueOrDefault(key) ?? "(missing)");
        }
    }

    /// <summary>
    /// The probe pane shown by its button, its content created, its theme followed, and hidden again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only with the tab shown and a person at the screen</b> - the caller returns early otherwise.
    /// Showing a pane is interface work of the same kind as bringing a tab forward, which once provoked
    /// "stop the current operation?" in the middle of a model load; and the theme switch changes a
    /// setting of that person's Revit, and puts it back.
    /// </para>
    /// <para>
    /// <b>A toggle is pressed again only when the previous press never ran.</b> A press is a request
    /// Revit may drop - bought twice already - so pressing is repeated; but a second press that did run
    /// hides what the first showed. <c>RegisteredPane.Toggles</c> counts presses that reached
    /// <c>PaneEntryPoint</c>, and the press is repeated only while it has not moved.
    /// </para>
    /// <para>
    /// Six checks, each something the pane design rests on and none measured before: the button shows the
    /// pane; Revit asks for the content only then, and the assembly loads then; the content is created
    /// once, told the open model, and reads it through the pump; the shell wears WPF-UI's theme for
    /// Revit's theme and our accent over it; a live theme switch reaches it; and the button hides it again.
    /// </para>
    /// </remarks>
    private static async Task CheckPaneShownAsync(RevitSideChannel.RevitSideChannelClient client, Report report)
    {
        if (!await ToggleAsync(client, show: true))
        {
            report.Check("the pane button shows the probe pane", false);
            NotePane(client, report);
            return;
        }

        report.Check("the pane button shows the probe pane", true);

        // Waited for: the content reads the title through the pump, which runs when Revit is idle.
        var pane = new Dictionary<string, string>(StringComparer.Ordinal);

        await WorkWatch.WaitForAsync(() =>
        {
            pane = Pane(client);
            return pane.GetValueOrDefault("pane:readTitle") is { Length: > 0 } && pane.ContainsKey("pane:themeSourceLive");
        }, 30_000);

        report.Check("and only then does Revit ask for its content, loading the pane assembly",
            int.TryParse(pane.GetValueOrDefault("pane:creatorCalls"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var creatorCalls)
            && creatorCalls >= 1
            && pane.GetValueOrDefault("pane:contentLoaded") == "True"
            && pane.GetValueOrDefault("pane:contentLoadedAtStartup") == "False");

        var title = pane.GetValueOrDefault("pane:lastDocument") ?? string.Empty;

        report.Check("the pane content is created once, told the open model, and reads it through the pump",
            pane.GetValueOrDefault("pane:created") == "1"
            && title.Length > 0
            && pane.GetValueOrDefault("pane:readTitle") == title);

        report.Check("the pane wears WPF-UI's theme for Revit's theme, with our accent over it", Themed(pane));

        foreach (var key in new[]
                 {
                     "pane:creatorCalls", "pane:creatorThread", "pane:createThread", "pane:apiThread", "pane:documentChanges",
                     "pane:themeSourceLive", "pane:accentPrimaryLive", "pane:backgroundLive", "pane:revitBackgroundLive",
                     "pane:inspect",
                 })
        {
            if (pane.TryGetValue(key, out var value))
                report.Note(key, value);
        }

        await CheckThemeSwitchAsync(client, report, pane.GetValueOrDefault("pane:revitTheme") ?? string.Empty);

        var hidden = await ToggleAsync(client, show: false);
        report.Check("the pane button hides it again", hidden);

        // Whatever the button did, the pane does not stay shown: Revit would restore it next time, and the
        // next sweep's laziness question could not be asked.
        if (!hidden)
            report.Note("pane hidden by the API instead", HideByApi(client));
    }

    /// <summary>Switches Revit's theme, checks the live pane follows, and always puts it back.</summary>
    private static async Task CheckThemeSwitchAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report,
        string before)
    {
        try
        {
            var outcome = client.Ask(new AskRequest { Question = "switchtheme" }).Values.GetValueOrDefault("pane:themeSwitch") ?? "(missing)";
            report.Note("theme switch", outcome);

            var pane = new Dictionary<string, string>(StringComparer.Ordinal);

            var followed = await WorkWatch.WaitForAsync(() =>
            {
                pane = Pane(client);
                var now = pane.GetValueOrDefault("pane:revitTheme") ?? string.Empty;
                return now.Length > 0 && now != before && Themed(pane);
            }, 30_000);

            report.Check("switching Revit's theme re-themes the live pane", followed);
            report.Note("theme after the switch",
                (pane.GetValueOrDefault("pane:revitTheme") ?? "(missing)") + ", pane " +
                (pane.GetValueOrDefault("pane:themeSourceLive") ?? "(missing)") + ", ThemeChanged seen " +
                (pane.GetValueOrDefault("pane:themeChanges") ?? "(missing)") + " time(s), last " +
                (pane.GetValueOrDefault("pane:lastThemeChange") ?? "(missing)"));
        }
        finally
        {
            // Always, and said out loud: this is a setting of the person's Revit.
            var restored = client.Ask(new AskRequest { Question = "restoretheme" }).Values.GetValueOrDefault("pane:themeRestore") ?? "(missing)";
            report.Note("theme restored", restored);
        }
    }

    /// <summary>Whether the live pane's theme dictionary and accent match Revit's theme.</summary>
    private static bool Themed(IReadOnlyDictionary<string, string> pane)
    {
        var dark = pane.GetValueOrDefault("pane:revitTheme") == "Dark";
        var source = pane.GetValueOrDefault("pane:themeSourceLive") ?? string.Empty;

        // The Designer's primary accent for each theme, as WPF prints a colour.
        var accent = dark ? "#FF3DB8AC" : "#FF0E8C82";

        return source.IndexOf(dark ? "Dark" : "Light", StringComparison.OrdinalIgnoreCase) >= 0
               && string.Equals(pane.GetValueOrDefault("pane:accentPrimaryLive"), accent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Presses the pane's button until the pane is in the wanted state, pressing again only while the
    /// previous press has not reached <c>PaneEntryPoint</c>.
    /// </summary>
    private static async Task<bool> ToggleAsync(RevitSideChannel.RevitSideChannelClient client, bool show)
    {
        var wanted = show ? "True" : "False";
        var start = Pane(client);

        if (start.GetValueOrDefault("pane:shown") == wanted)
            return true;

        var toggles = start.GetValueOrDefault("pane:toggles") ?? "0";
        var attempts = 0;

        client.Ask(new AskRequest { Question = "presspane" });

        return await WorkWatch.WaitForAsync(() =>
        {
            var now = Pane(client);

            if (now.GetValueOrDefault("pane:shown") == wanted)
                return true;

            // Every few polls, and only while no press has run: a dropped request is repeated, a press
            // that ran is never doubled.
            if (++attempts % 5 == 0 && now.GetValueOrDefault("pane:toggles") == toggles)
                client.Ask(new AskRequest { Question = "presspane" });

            return false;
        }, 60_000);
    }

    private static string HideByApi(RevitSideChannel.RevitSideChannelClient client) =>
        client.Ask(new AskRequest { Question = "hidepane" }).Values.GetValueOrDefault("pane:hide") ?? "(missing)";

    private static Dictionary<string, string> Pane(RevitSideChannel.RevitSideChannelClient client) =>
        new(client.Ask(new AskRequest { Question = "pane" }).Values, StringComparer.Ordinal);

    private static void NotePane(RevitSideChannel.RevitSideChannelClient client, Report report)
    {
        var pane = Pane(client);

        foreach (var key in new[] { "pane:shown", "pane:toggles", "pane:press", "pane:creatorCalls", "pane:exists", "pane:title" })
            report.Note(key, pane.GetValueOrDefault(key) ?? "(missing)");
    }

    /// <summary>
    /// Whether the Entry button landed on the edition's tab, on Ping's panel - the placement every other
    /// Entry check assumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The edition chooses the tab, and this is the only check that looks.</b> Everything else about the
    /// tab reached the sweep indirectly: through the press spelling, which fails far later and reads as
    /// Revit refusing the Entry command; and through the tab the probe brought forward, which was the first
    /// one holding a panel of Ping's title - a panel two manifests now make. So the probe picks the tab by
    /// name and records what was on it, and this asserts three things at once: the tab shown is the
    /// edition's, Gate and Ping both stand on its panel, and no other tab holds a panel of that title.
    /// </para>
    /// <para>
    /// A button is recognised by its AdWindows id containing the manifest name, or by its text. The id
    /// format is not measured, and the text is unique on this panel; both raw lists go into the notes so a
    /// red here can be read for which half did not match.
    /// </para>
    /// </remarks>
    /// <summary>What the probe records for a placement fact it looked for and found nothing - its <c>NoneRecorded</c>.</summary>
    private const string NoneRecorded = "(none)";

    private static async Task CheckEntryPlacementAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        await WorkWatch.WaitForAsync(() =>
        {
            values = new Dictionary<string, string>(
                client.Ask(new AskRequest { Question = "ribbon" }).Values, StringComparer.Ordinal);

            return values.GetValueOrDefault("ribbon:activatedTab") is { Length: > 0 };
        }, 30_000);

        var activated = values.GetValueOrDefault("ribbon:activatedTab") ?? string.Empty;
        var ids = values.GetValueOrDefault("ribbon:ownPanelButtonIds") ?? string.Empty;
        var texts = (values.GetValueOrDefault("ribbon:ownPanelButtonTexts") ?? string.Empty)
                    .Split(new[] { " | " }, StringSplitOptions.None);
        var others = values.GetValueOrDefault("ribbon:otherTabsWithOwnPanel") ?? string.Empty;

        var gate = ids.Contains("BHS.Probe.Gate", StringComparison.Ordinal) || texts.Contains("Gate");
        var ping = ids.Contains("BHS.Probe.Ping", StringComparison.Ordinal) || texts.Contains("Ping");

        report.Check("the Entry button landed on the edition's tab, beside Ping, and on no other tab",
            activated == ProbeDeployment.EditionTab && gate && ping && others == NoneRecorded);

        report.Note("Entry button placement",
            $"tab shown {(activated.Length > 0 ? activated : "(not recorded)")}, button ids [{ids}], " +
            $"texts [{string.Join(" | ", texts)}], other tabs with that panel {(others.Length > 0 ? others : "(not recorded)")}");
    }

    /// <summary>
    /// An availability class from a feature's Entry assembly: whether Revit asks it, and what asking
    /// it loads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two assertions, because the design rests on both.</b> That Revit calls an availability class
    /// whose logic is <c>AvailabilityEntryPoint&lt;GateRule&gt;</c> in <c>BHS.Revit.Abstractions</c>:
    /// RefCheck accepts the shape against metadata, and only Revit can say whether it constructs it. And
    /// that asking it leaves the feature assembly out of the AppDomain until a press, although the Entry
    /// assembly also holds a command entry point whose base names the feature's command. Both were
    /// written before the answer and first came back green on all four releases on 2026-09-14. Red on
    /// either means the design is wrong, not the probe.
    /// </para>
    /// <para>
    /// <b>The loaded state is read after the counter moved, and read twice.</b> Once in the same answer
    /// that first shows the rule was asked - the probe reads the counter before the assembly list, so
    /// within one answer the order is guaranteed - and once more a moment later, still before any
    /// press. No wait can prove that something does not happen; the second read is there so that a load
    /// following closely on the first call is not missed by a read that came too early.
    /// </para>
    /// <para>
    /// <b>The rest are notes</b>, because nobody knew the answers when they were written and either is
    /// compatible with the design: what <c>ActiveAddInId</c> says while Revit asks availability, whether
    /// the rule is called off the API thread, and whether the Entry assembly came in when the ribbon was
    /// built or only when its class was first asked. First answered 2026-09-14, alike on four releases:
    /// the probe's own id, never off the API thread, and only when first asked.
    /// </para>
    /// </remarks>
    private static async Task CheckEntryAvailabilityAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var loadedWhenAsked = "(not read)";

        var asked = await WorkWatch.WaitForAsync(() =>
        {
            values = new Dictionary<string, string>(
                client.Ask(new AskRequest { Question = "ribbon" }).Values, StringComparer.Ordinal);

            if (values.GetValueOrDefault("ribbon:gateCalls") is null or "0")
                return false;

            loadedWhenAsked = values.GetValueOrDefault("ribbon:featureLoaded") ?? "(missing)";
            return true;
        }, 60_000);

        report.Check("Revit asks an Entry availability class, whose generic base is in another assembly", asked);

        var loadedAfter = "(not read)";

        if (asked)
        {
            // A pause rather than a wait, and on purpose: this is the one read in the sweep that hopes
            // nothing happens, so there is no condition to wait for - only a second look taken late
            // enough to be different from the first.
            await Task.Delay(2000);

            values = new Dictionary<string, string>(
                client.Ask(new AskRequest { Question = "ribbon" }).Values, StringComparer.Ordinal);

            loadedAfter = values.GetValueOrDefault("ribbon:featureLoaded") ?? "(missing)";
        }
        else
        {
            // Said, because the check below is about to fail for a reason that is not its own.
            report.Note("the feature assembly after the Entry rule was asked", "not evaluated - the rule was never asked");

            foreach (var line in LogLines(values.GetValueOrDefault("log"), "BHS.Revit.Probe.Entry"))
                report.Note("what the probe log says about the Entry assembly", line);
        }

        report.Check("and the feature assembly is still not loaded once it has been asked",
            asked && loadedWhenAsked == "False" && loadedAfter == "False");

        report.Note("Entry rule calls",
            (values.GetValueOrDefault("ribbon:gateCalls") ?? "(missing)")
            + ", off the API thread " + (values.GetValueOrDefault("ribbon:gateCallsOffApiThread") ?? "(missing)")
            + " (API thread " + (values.GetValueOrDefault("ribbon:apiThread") ?? "?")
            + ", first call on thread " + (values.GetValueOrDefault("ribbon:gateFirstCallThread") ?? "?") + ")");

        report.Note("feature assembly when the Entry rule was first seen asked, and again 2 s later",
            loadedWhenAsked + ", " + loadedAfter);

        report.Note("ActiveAddInId inside availability, raw, from the control",
            "first " + (values.GetValueOrDefault("ribbon:availabilityActiveAddInFirst") ?? "(missing)")
            + ", latest " + (values.GetValueOrDefault("ribbon:availabilityActiveAddInLatest") ?? "(missing)"));

        report.Note("Entry assembly loaded", DescribeEntryLoad(values));
    }

    /// <summary>When the Entry assembly came in, in the terms the question has.</summary>
    private static string DescribeEntryLoad(IReadOnlyDictionary<string, string> values)
    {
        if (values.GetValueOrDefault("ribbon:entryLoadedAtRibbonBuild") == "True")
            return "already when the ribbon was built - Revit loaded the button's assembly as it added the button";

        var loaded = Moment(values, "ribbon:entryLoadedUtc");
        var firstCall = Moment(values, "ribbon:gateFirstCallUtc");

        if (loaded is null)
        {
            return values.GetValueOrDefault("ribbon:entryLoaded") == "True"
                ? "after the ribbon was built, at a moment nobody recorded"
                : "not loaded";
        }

        if (firstCall is null)
            return "after the ribbon was built, and its rule has not been asked";

        // Signed, and said as a difference rather than as "before": the load is expected to lead the
        // first call by milliseconds, and a negative number is a finding rather than a typo.
        return "after the ribbon was built; the first call to its rule came "
               + (firstCall.Value - loaded.Value).TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)
               + "s after the load";
    }

    /// <summary>
    /// A command from a feature's Entry assembly, pressed: whether it runs, and which host it reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two assertions.</b> That the press runs <c>GateCommand</c> through
    /// <c>CommandEntryPoint&lt;ProbeGateFeature, GateCommand&gt;</c>, and that the host it was handed is
    /// the probe's own interface host - found by the feature its <c>Modules</c> list declares. The
    /// Entry assembly belongs to no edition, so the assembly half of the lookup Ping uses would find
    /// nothing here, and the two-argument base does not try it.
    /// </para>
    /// <para>
    /// <b>Pressed like Ping, and stopped sooner.</b> A posted command is dropped silently when Revit is
    /// not ready, so it is posted again. But a press can also be refused, and a refusal is a dialog.
    /// Pressing again would stack dialogs on the person answering them - each dismissal releases the next
    /// pending press into the same dialog - so the pressing stops on any of three signs:
    /// </para>
    /// <list type="bullet">
    /// <item><description>our own refusal in the probe log - no host, or several - which names the command
    /// or the feature; read before each repeat, since it reads the whole log;</description></item>
    /// <item><description>a press that found no command id on the edition's tab, which the probe records
    /// and which no repeat can change; looked at on every poll;</description></item>
    /// <item><description>Revit blocking, or raising a dialog, after the first press. A refusal Revit makes
    /// itself - failing to construct the entry point, a missing <c>[Transaction]</c>, a type that will not
    /// load - comes before our code runs and leaves nothing in the probe log. Whether Revit's dialog for a
    /// failed external command raises <c>DialogBoxShowing</c> is not measured, so the press outcome is
    /// noted as well: "posted and never ran" and "never posted" must not read alike when no dialog event
    /// arrives either.</description></item>
    /// </list>
    /// <para>
    /// The raw <c>ActiveAddInId</c> is a note: first recorded 2026-09-14, when it named the probe on all
    /// four releases - before that Ping recorded the host it was handed, not the id - and the entry point
    /// consults it only when more than one host declares the feature, which this sweep does not arrange.
    /// </para>
    /// </remarks>
    private static async Task CheckEntryCommandAsync(
        RevitSideChannel.RevitSideChannelClient client,
        DiagnosticsWatcher watcher,
        Report report)
    {
        const int timeout = 90_000;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var refusals = new List<string>();
        var runs = "0";
        var attempts = 0;
        var presses = 0;
        var stoppedBecause = string.Empty;

        // Taken before the first press, so that what came after it is what counts: a dialog answered
        // earlier in the sweep leaves both the dialog list and the current phase where it left them.
        var dialogsBefore = watcher.Dialogs.Count;
        var blockedBefore = watcher.BlockedEvents;
        var blockedAtStart = watcher.CurrentPhase == RevitPhase.Blocked;

        // Its own deadline, set no later than the wait's: WaitForAsync evaluates the condition once more
        // after its own deadline has passed, and a press raised then would land in the checks that follow.
        var pressUntil = DateTime.UtcNow.AddMilliseconds(timeout);

        await WorkWatch.WaitForAsync(() =>
        {
            if (attempts % 20 == 0 && DateTime.UtcNow < pressUntil)
            {
                client.Ask(new AskRequest { Question = "pressgate" });
                presses++;
            }

            attempts++;

            values = new Dictionary<string, string>(
                client.Ask(new AskRequest { Question = "ribbon" }).Values, StringComparer.Ordinal);

            runs = values.GetValueOrDefault("ribbon:gateRuns") ?? "0";

            // A run first: whatever else happened, a command that ran is the answer.
            if (runs != "0")
                return true;

            // Every poll, because both are a field or two already in hand.
            if (values.GetValueOrDefault("ribbon:gatePress") == PressUnmatched)
            {
                stoppedBecause = "no command id matched on the edition's tab, so it was not pressed again";
                return true;
            }

            if (watcher.BlockedEvents > blockedBefore
                || watcher.Dialogs.Count > dialogsBefore
                || (!blockedAtStart && watcher.CurrentPhase == RevitPhase.Blocked))
            {
                var dialogs = watcher.Dialogs.Skip(dialogsBefore).ToList();

                if (watcher.BlockedBy.Length > 0 && !dialogs.Contains(watcher.BlockedBy))
                    dialogs.Add(watcher.BlockedBy);

                stoppedBecause = "Revit blocked after the press, so it was not pressed again - dialog(s) "
                                 + (dialogs.Count > 0 ? string.Join(", ", dialogs) : "without an identifier")
                                 + ", last press " + Or(values.GetValueOrDefault("ribbon:gatePress"), "(not recorded)");

                // Once more, because our own refusal is a dialog too: Failed with a message is shown by
                // Revit, and the Blocked phase can arrive before the next scheduled read of the log.
                refusals = Refusals(values.GetValueOrDefault("log"));
                return true;
            }

            // Only just before a repeat press, as before: it reads the whole probe log. The lines it finds
            // are noted one by one below, so no second reason is recorded for them.
            if (attempts % 20 == 0)
            {
                refusals = Refusals(values.GetValueOrDefault("log"));

                if (refusals.Count > 0)
                    return true;
            }

            return false;
        }, timeout);

        report.Check("a command in a feature's Entry assembly runs when its button is pressed", runs != "0");

        var host = values.GetValueOrDefault("ribbon:gateAddInId") ?? "(none)";

        report.Check("and it was handed the probe's interface host, found by its feature",
            string.Equals(host, ProbeDeployment.AddInId, StringComparison.OrdinalIgnoreCase));

        report.Note("Entry command",
            $"runs {runs}, host {host}, hosts declaring its feature {values.GetValueOrDefault("ribbon:gateHosts") ?? "(missing)"}");

        report.Note("ActiveAddInId inside the Entry command, raw",
            values.GetValueOrDefault("ribbon:gateActiveAddInId") ?? "(none)");

        foreach (var refusal in refusals)
            report.Note("the Entry command was refused, so it was not pressed again", refusal);

        // What the last press came to, whatever ended the wait: "posted" beside zero runs is a press
        // Revit took and did nothing with, or refused before our code ran; empty is a press that never
        // left the external event queue.
        report.Note("Entry press",
            $"pressed {presses} time(s), last press {Or(values.GetValueOrDefault("ribbon:gatePress"), "(never executed)")}");

        if (stoppedBecause.Length > 0)
            report.Note("Entry pressing stopped", stoppedBecause);

        // Where the feature assembly came in, against the first call to the Entry rule. Ping's press
        // loaded it, and that is the expected answer; the number says how long it stayed out after the
        // Entry assembly was already in and being asked.
        var featureLoaded = Moment(values, "ribbon:featureLoadedUtc");
        var firstCall = Moment(values, "ribbon:gateFirstCallUtc");

        report.Note("feature assembly loaded after the first call to the Entry rule",
            featureLoaded is null || firstCall is null
                ? "not both recorded (loaded when the ribbon was built: "
                  + (values.GetValueOrDefault("ribbon:featureLoadedAtRibbonBuild") ?? "(missing)") + ")"
                : (featureLoaded.Value - firstCall.Value).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");
    }

    /// <summary>What the probe's press handler records for a press no spelling found - its <c>Unmatched</c>.</summary>
    private const string PressUnmatched = "unmatched";

    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    /// <summary>
    /// The lines of the probe log that say a press of the Entry button was refused.
    /// </summary>
    /// <remarks>
    /// Matched on what the framework writes at <c>ERR</c>: every refusal names the command's full type
    /// name - "cannot run", "has nowhere to run", "command ... failed" - except the one for several hosts,
    /// which names the feature instead. Brittle against a reworded message, and acceptable for that: it
    /// decides only whether to stop pressing and what to note, never whether a check passes.
    /// </remarks>
    private static List<string> Refusals(string? logPath)
    {
        var found = new List<string>();

        foreach (var line in ReadLog(logPath ?? string.Empty).Split('\n'))
        {
            if (line.IndexOf("  ERR  ", StringComparison.Ordinal) < 0)
                continue;

            if (line.Contains("BHS.Revit.Probe.Feature.GateCommand", StringComparison.Ordinal)
                || line.Contains("BHS.Revit.Probe.Declaration.ProbeGateFeature is declared by more than one host", StringComparison.Ordinal))
            {
                found.Add(line.TrimEnd('\r'));
            }
        }

        return found;
    }

    /// <summary>Warnings and errors in the probe log that mention something, a few at most.</summary>
    private static IEnumerable<string> LogLines(string? logPath, string about)
    {
        return ReadLog(logPath ?? string.Empty)
               .Split('\n')
               .Where(line => line.Contains(about, StringComparison.Ordinal)
                              && (line.Contains("  ERR  ", StringComparison.Ordinal)
                                  || line.Contains("  WRN  ", StringComparison.Ordinal)))
               .Select(line => line.TrimEnd('\r'))
               .Take(5);
    }

    /// <summary>A moment the probe wrote as round-trip text, or null when it wrote none.</summary>
    private static DateTime? Moment(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text)
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.ToUniversalTime()
            : null;

    /// <summary>
    /// A command Revit built itself, finding its host and loading its feature to do it.
    /// </summary>
    /// <remarks>
    /// Three answers in one press, and none of them reachable any other way. Whether the registry
    /// keyed by add-in id is found from inside a command Revit constructed from a string; what
    /// <c>ActiveAddInId</c> actually returns there, documented and first recorded raw on 2026-09-14; and
    /// whether the feature assembly stays unloaded until the button is used.
    /// </remarks>
    private static async Task CheckFeatureCommandAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        var runs = "0";
        var addInId = "(none)";
        var active = "(none)";
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
            active = values.GetValueOrDefault("ribbon:pingActiveAddInId") ?? "(none)";
            loaded = values.GetValueOrDefault("ribbon:featureLoaded") ?? "False";
            return runs != "0";
        }, 90_000);

        report.Check("a feature command runs through a one-line entry point", pressed);

        report.Check("and it found its own host, keyed by add-in id",
            string.Equals(addInId, ProbeDeployment.AddInId, StringComparison.OrdinalIgnoreCase));

        report.Check("and the feature assembly loaded only once it was needed", loaded == "True");

        report.Note("feature command", $"runs {runs}, host {addInId}");

        // The control for the Entry command's raw answer: the same question, asked of a button the
        // probe's own manifest built, whose entry point looks its host up by this very id first and by
        // assembly second - never by feature.
        report.Note("ActiveAddInId inside the Ping command, raw", active);
    }

    /// <summary>
    /// What a real project holds, reported so that code is written against it rather than a guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counts are notes, because nothing here can pass or fail: there is no right answer to
    /// "how many trays does this building have". That the survey <i>answered</i> is a check, and a
    /// real one - it walks <c>ElectricalSystem</c>, <c>RevitLinkInstance</c> and a circuit collector
    /// on four runtimes, and compiling against four target frameworks says nothing about whether the
    /// call survives on any of them.
    /// </para>
    /// <para>
    /// <b>It runs on the synthetic model too, and the earlier reason not to was wrong.</b> This was
    /// gated on a model named at the command line, on the grounds that an empty file would print a
    /// screen of zeroes; it prints one line, because the counts are omitted where nothing was found.
    /// So the gate bought nothing and cost the only place where the new code path runs unattended on
    /// every release.
    /// </para>
    /// <para>
    /// <b>Counts and category names only.</b> These are somebody's real project files and this
    /// repository is public; family, level, panel and circuit names would identify the building.
    /// The probe is written to refuse them at the source rather than to filter them here.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What a modal window can still do while it owns the API thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These were notes for exactly one run, and now they are assertions.</b> The claim under
    /// test - that an <c>ExternalEvent</c> raised from inside a modal window never fires, because
    /// Revit never reaches <c>Idling</c> while the window is up - is written into the routing core's
    /// project file as a reason for its shape, and nobody had run it. All four releases answered
    /// identically, so the notes now guard the decision instead of informing it: the day any of them
    /// changes, the shape of the cabling command has to change with it, and that is news worth a red
    /// line rather than a number nobody rereads.
    /// </para>
    /// <para>
    /// The order was the point. A check written before its answer agrees with whoever wrote it,
    /// which is how <c>IsSessionReady</c> kept a wrong name until somebody measured.
    /// </para>
    /// <para>
    /// <b>The half the first pass forgot to ask has since been asked.</b> The API was called before
    /// the await and not after, and "after" is what decides whether a command can compute in the
    /// background and apply in the continuation - being on the API thread is not the same as
    /// standing in a context Revit will serve. It does serve it, on all four releases.
    /// </para>
    /// <para>
    /// <b>Still not measured, and named here so it is not mistaken for settled: a transaction.</b>
    /// Every read above is a read. Applying a route is a write, and a write means
    /// <c>Transaction.Start</c> in that same continuation. The gate on modification is the
    /// transaction rather than the call, so a read succeeding does not answer for one. It will be
    /// measured with the apply phase, against a real write rolled back in a group - the shape the
    /// schema check already uses, so that the production path is exercised rather than a rehearsal
    /// of it.
    /// </para>
    /// </remarks>
    private static async Task CheckModalWindowAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        Report.Heading("a modal window on the API thread");

        var during = await client.AskAsync(new AskRequest { Question = "modal" });

        foreach (var pair in during.Values.OrderBy(one => one.Key, StringComparer.Ordinal))
            report.Note(pair.Key, pair.Value);

        var after = await client.AskAsync(new AskRequest { Question = "modalafter" });

        foreach (var pair in after.Values.OrderBy(one => one.Key, StringComparer.Ordinal))
            report.Note(pair.Key, pair.Value);

        report.Check(
            "the modal window was raised on the API thread",
            during.Values.TryGetValue("modal:apiThread", out var api)
            && during.Values.TryGetValue("modal:windowThread", out var shown)
            && api == shown);

        report.Check("and it closed itself, with nobody there to close it",
            after.Values.GetValueOrDefault("modal:windowClosed") == "True");

        // The claim the routing core's shape rests on, now guarded rather than believed.
        report.Check(
            "work posted to the pump does not run while the window is up",
            during.Values.GetValueOrDefault("modal:pumpRanWhileModal") == "False");

        // And its other half, which is what a command can count on the moment a dialog is
        // dismissed: the pump drains its whole queue in one pass, so the wait is not until the next
        // idle - it is until this stack unwinds.
        report.Check(
            "and it runs as soon as the window closes",
            after.Values.GetValueOrDefault("modal:pumpRanAfterClose") == "True");

        // The finding that changes the design rather than confirming it: a dialog can show progress
        // while a background search runs, because the continuation comes back to the API thread.
        report.Check(
            "an await inside the window resumes",
            during.Values.GetValueOrDefault("modal:awaitResumed") == "True");

        report.Check(
            "and it resumes on the API thread",
            during.Values.GetValueOrDefault("modal:awaitResumedOnApiThread") == "True");

        // Reads need no pump from inside a dialog: the command is already standing in a valid API
        // context, on the API thread, and the pump exists for callers who are not.
        report.Check(
            "the Revit API answers a direct read from inside the window",
            during.Values.GetValueOrDefault("modal:apiCallWorked") == "True");

        // The one that makes "collect, compute in the background, apply in the continuation" a shape
        // a command can have.
        report.Check(
            "and it still answers after an await has resumed",
            during.Values.GetValueOrDefault("modal:apiCallWorkedAfterAwait") == "True");

        // And the write, which is a different gate from the read and was named unmeasured until now.
        // A route is applied inside a transaction, and Transaction.Start is what a modification is
        // refused at - a successful read does not answer for it. Skipped, loudly, when no document is
        // open: a check that quietly passes on a model-less run reports success by default.
        var write = during.Values.GetValueOrDefault("modal:transactionSkipped") ?? string.Empty;

        if (write.Length > 0)
        {
            report.Note("a transaction inside the window was not attempted", write);
        }
        else
        {
            report.Check(
                "a transaction opens inside the window after an await",
                during.Values.GetValueOrDefault("modal:transactionStartedAfterAwait") == "True");

            report.Check(
                "and commits a real modification there",
                during.Values.GetValueOrDefault("modal:transactionCommittedAfterAwait") == "True");

            // The write is undone by the group, so the run leaves the document as it found it -
            // otherwise Revit asks about saving on the way out, and that is a modal window in a run
            // nobody is watching. Same form as the schema check.
            report.Check(
                "and the group puts the document back as it was",
                during.Values.GetValueOrDefault("modal:transactionRolledBack") == "True");
        }

        report.Check(
            "the continuation lands back in the window's own context",
            during.Values.GetValueOrDefault("modal:contextAfterAwait") == "DispatcherSynchronizationContext");

        // Both contexts are asserted, and the outer one is the surprise: on the API thread inside an
        // external event Revit's context is WinForms, not WPF. An await taken before a dialog opens
        // therefore resumes by a different mechanism than one taken inside it - worth knowing before
        // something is built on the assumption that they are the same.
        report.Check(
            "the context inside the window is WPF's dispatcher",
            during.Values.GetValueOrDefault("modal:contextInside") == "DispatcherSynchronizationContext");

        report.Check(
            "and outside it Revit's own context is WinForms",
            during.Values.GetValueOrDefault("modal:contextOutside") == "WindowsFormsSynchronizationContext");
    }

    /// <summary>
    /// The shared parameter scheme, which until now had no automated check of any kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three claims, and each of them is load-bearing for a decision already taken. That the swap of
    /// <c>SharedParametersFilename</c> puts the user's own choice back - the whole reason writing our
    /// own file was acceptable at all. That two files carrying the same GUIDs under different names
    /// really are one parameter - the owner's two-file requirement rests on it. And that a document
    /// reads as bound by GUID rather than by name, which is what lets a model built in one language
    /// be understood by a Revit running in the other.
    /// </para>
    /// <para>
    /// The last one is the one that would have failed silently: a wrong answer there does not throw,
    /// it re-binds a parameter the model already has and leaves two names for one thing.
    /// </para>
    /// </remarks>
    private static async Task CheckSharedParametersAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        Report.Heading("the shared parameter scheme");

        AskResponse answer;

        try
        {
            answer = await client.AskAsync(new AskRequest { Question = "parameters" });
        }
        catch (RpcException error)
        {
            report.Check("the parameter scheme answers", false);
            report.Note("parameters failed", error.Status.StatusCode.ToString());
            return;
        }

        foreach (var pair in answer.Values.OrderBy(one => one.Key, StringComparer.Ordinal))
            report.Note(pair.Key, pair.Value);

        report.Check(
            "a file is written for every language, not only this Revit's",
            answer.Values.GetValueOrDefault("parameters:filesWritten") == "2");

        report.Check(
            "and Revit finds our parameter in each of them",
            answer.Values.GetValueOrDefault("parameters:name:en") == "BHS_Prb_FirstFact"
            && answer.Values.GetValueOrDefault("parameters:name:ru") == "BHS_Prb_ПервыйФакт");

        // The point of two files. Same GUID, different name: if these matched, the check would be
        // asserting nothing and would keep passing after the translation was lost.
        report.Check(
            "the two files call one parameter by two names",
            answer.Values.GetValueOrDefault("parameters:name:en")
            != answer.Values.GetValueOrDefault("parameters:name:ru"));

        // The swap is the one liberty this mechanism takes with state that belongs to everybody, so
        // the promise that it is put back is the one that has to be checked rather than believed.
        report.Check(
            "the user's own shared parameter file is put back afterwards",
            answer.Values.GetValueOrDefault("parameters:filePutBack") == "True");

        var skipped = answer.Values.GetValueOrDefault("parameters:documentSkipped");

        if (skipped != "False")
        {
            // Said out loud, for the same reason the modal window's transaction is: a binding check
            // that quietly does nothing on a model-less run reads as one that passed.
            report.Note("binding was not attempted", "no document is open - run with --with-model");
            return;
        }

        report.Check(
            "binding puts every declared parameter into the model",
            answer.Values.GetValueOrDefault("parameters:missingAfter") == "0");

        report.Check(
            "and the parameter is found by its identifier, not its name",
            answer.Values.GetValueOrDefault("parameters:foundByGuid") == "True");

        // What the model ended up calling it, against what this Revit's language should have given.
        // A mismatch here means the name a document takes is not the one we think it is - and every
        // claim about the two files being interchangeable is built on knowing which one lands.
        //
        // Both sides are required to be present, and that is not belt and braces: comparing two
        // absent keys makes null equal null, so the check would have been green on a probe that
        // reported neither - the exact shape of failure this file spends a chapter on.
        var inModel = answer.Values.GetValueOrDefault("parameters:nameInModel") ?? string.Empty;
        var expected = answer.Values.GetValueOrDefault("parameters:nameExpected") ?? string.Empty;

        report.Check(
            "under the name from the file this Revit reads",
            inModel.Length > 0 && string.Equals(inModel, expected, StringComparison.Ordinal));

        // And the run leaves the model as it found it, or Revit asks about saving on the way out.
        report.Check(
            "and the group leaves the document without it",
            answer.Values.GetValueOrDefault("parameters:goneAfterRollback") == "True");
    }

    /// <summary>
    /// What a real model says about the family chosen as a recommended-box indicator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three questions the design is currently guessing at: the category of the owner's family,
    /// whether it is placed with a point or wants a host, and whether an instance of it is connected
    /// to anything. The last one carries the hazard - a fitting-category indicator is collected as a
    /// carrier by category alone, and the network then holds a node the project does not have.
    /// </para>
    /// <para>
    /// <b>Notes rather than checks, deliberately, and the same order as everywhere else here.</b>
    /// An assertion written before its answer agrees with whoever wrote it. Only that the survey
    /// answered is a check: it walks <c>FamilyInstanceFilter</c>, <c>FamilyPlacementType</c> and a
    /// connector manager on four runtimes, and compiling against four target frameworks says nothing
    /// about whether the calls survive on any of them.
    /// </para>
    /// </remarks>
    private static async Task RecommendedBoxAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        AskResponse answer;

        try
        {
            answer = await client.AskAsync(new AskRequest { Question = "box" });
        }
        catch (RpcException error)
        {
            report.Check("the recommended-box survey answers", false);
            report.Note("box survey failed", error.Status.StatusCode.ToString());
            return;
        }

        if (answer.Values.GetValueOrDefault("box:documentSkipped") == "True")
        {
            report.Note("recommended box", "no document was open");
            return;
        }

        report.Check("the recommended-box survey answers", answer.Values.ContainsKey("box:ourSymbols"));

        Report.Heading("the indicator family, and what a text parameter holds");

        foreach (var pair in answer.Values.OrderBy(one => one.Key, StringComparer.Ordinal))
            report.Note(pair.Key, pair.Value);
    }

    /// <summary>
    /// Runs the test suites the probe declares, and turns each case into a line of this report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where the sweep stops being about the probe.</b> Until now every check here was a
    /// question about the framework or about Revit itself; the code an edition actually runs -
    /// <c>source/Features</c>, <c>source/BHS.FullEdition</c> - had no automated check of any kind,
    /// and its three known defects were all found by a person installing the edition and pressing
    /// the button. See CLAUDE.md on why the runner grew into a test runner rather than a headless
    /// engine being lifted into a process of ours.
    /// </para>
    /// <para>
    /// <b>One case, one check.</b> The floor that catches a check which stopped running counts
    /// lines, so a case has to be a line; and the record CI compares against names checks by their
    /// text, so a case that is renamed reads as one disappearing and one appearing. Case names are
    /// therefore as stable as check names, which is to say they are written once.
    /// </para>
    /// <para>
    /// <b>Skipped is said out loud and counts as nothing.</b> Not as a passing check, because it
    /// asserted nothing; not silently, because a skipped check nobody mentions reads exactly like a
    /// passing one - this repository has spent whole runs proving nothing that way. Instead the
    /// count of skips is noted, and the arithmetic below insists that every declared case came back
    /// as one of the three outcomes.
    /// </para>
    /// </remarks>
    private static async Task CheckDeclaredTestsAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        AskResponse answer;

        try
        {
            answer = await client.AskAsync(new AskRequest { Question = "tests" });
        }
        catch (RpcException error)
        {
            // One red line rather than the abandonment of the release: the suites failing to run is
            // a finding about them, and everything after this still has questions worth asking.
            report.Check("the declared test suites ran", false);
            report.Note("tests failed", error.Status.StatusCode + ": " + error.Status.Detail);
            return;
        }

        Report.Heading("declared tests, run inside this Revit");

        var reported = Number(answer, "tests:reported");
        var suites = Number(answer, "tests:suites");

        report.Check("the declared test suites ran", reported >= 0 && suites > 0);
        report.Note("suites declared", suites.ToString(CultureInfo.InvariantCulture));

        if (reported < 0)
            return;

        var seen = 0;
        var skipped = 0;

        for (var index = 0; ; index++)
        {
            var prefix = "tests:" + index.ToString("D3", CultureInfo.InvariantCulture) + ":";

            if (!answer.Values.TryGetValue(prefix + "name", out var name))
                break;

            seen++;

            var suite = answer.Values.GetValueOrDefault(prefix + "suite") ?? string.Empty;
            var outcome = answer.Values.GetValueOrDefault(prefix + "outcome") ?? string.Empty;
            var detail = answer.Values.GetValueOrDefault(prefix + "detail") ?? string.Empty;
            var what = suite.Length > 0 ? suite + ": " + name : name;

            if (string.Equals(outcome, "Skipped", StringComparison.Ordinal))
            {
                skipped++;
                report.Note("skipped - " + what, detail);
            }
            else
            {
                report.Check(what, string.Equals(outcome, "Passed", StringComparison.Ordinal));

                if (detail.Length > 0)
                    report.Note("why", detail);
            }

            // The case's own measurements, which is where a test puts a count it refuses to assert
            // against somebody's live building.
            for (var note = 0; ; note++)
            {
                var key = prefix + "note:" + note.ToString("D2", CultureInfo.InvariantCulture);

                if (!answer.Values.TryGetValue(key + ":what", out var label))
                    break;

                report.Note(label, answer.Values.GetValueOrDefault(key + ":value") ?? string.Empty);
            }
        }

        // The arithmetic, and it is not ceremony: the results cross a channel as a flat map, and a
        // map that lost entries on the way would arrive looking like a smaller run that went
        // perfectly. Both sides count independently and the two are made to agree.
        report.Check(
            "every declared case came back",
            seen == reported &&
            reported == Number(answer, "tests:passed") + Number(answer, "tests:failed") + Number(answer, "tests:skipped"));

        // A suite list that lost its contents, or a mode in which everything is skipped, would
        // otherwise be a clean run with nothing in it.
        report.Check(
            "at least one case actually asserted something",
            Number(answer, "tests:passed") + Number(answer, "tests:failed") > 0);

        report.Note("cases skipped", skipped.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A count from the answer, or -1 when it is missing or not a number.</summary>
    /// <remarks>
    /// Negative rather than zero for absent, because zero is a legitimate answer to every one of
    /// these questions and the two must not be confused: "no case failed" and "the run did not say"
    /// are the difference between a green sweep and one that proved nothing.
    /// </remarks>
    private static int Number(AskResponse answer, string key) =>
        answer.Values.TryGetValue(key, out var text) &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;

    private static async Task SurveyCablingAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        AskResponse answer;

        try
        {
            answer = await client.AskAsync(new AskRequest { Question = "survey" });
        }
        catch (RpcException error)
        {
            // Caught narrowly rather than left to abandon the release: a survey that throws is worth
            // one red line, not the seventy checks that would follow it.
            report.Check("the model survey answers", false);
            report.Note("survey failed", error.Status.StatusCode.ToString());
            return;
        }

        if (answer.Values.GetValueOrDefault("survey:document") == "(none)")
        {
            report.Note("survey", "no document was open");
            return;
        }

        report.Check("the model survey answers", answer.Values.ContainsKey("survey:links"));

        Report.Heading("what this model holds");

        foreach (var pair in answer.Values.OrderBy(one => one.Key, StringComparer.Ordinal))
            report.Note(pair.Key, pair.Value);
    }

    /// <summary>
    /// How permanent an Extensible Storage schema really is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three-field shape of <c>BHS.ModelSettings</c> rests on one sentence in <c>CLAUDE.md</c>:
    /// a schema that has reached somebody else's model can never be changed. That is true of one
    /// GUID and silent about the price - a schema under a new GUID is a different schema, and a
    /// reader that tries the new one and falls back to the old migrates on the next write. What the
    /// shape should be depends on which of those is the real cost, so it is worth measuring before
    /// the identity leaves this machine.
    /// </para>
    /// <para>
    /// <b>Notes first, assertions second, and the order was the point.</b> When this was written
    /// nobody knew the answers, so it reported and asserted almost nothing - a check written before
    /// its answer agrees with whoever wrote it, which is how <c>IsSessionReady</c> got its name and
    /// kept it until somebody measured. Revit 2024, 2025 and 2027 then answered identically, so the
    /// notes have become assertions. They now guard the decision rather than inform it: the day
    /// Autodesk lets a schema grow a field, this goes red, and that is precisely the news worth
    /// hearing the same day rather than a year later.
    /// </para>
    /// </remarks>
    private static async Task CheckSchemaEvolutionAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Report report)
    {
        var answer = await client.AskAsync(new AskRequest { Question = "schema" });
        var values = answer.Values;

        if (values.GetValueOrDefault("schema:document") == "(none)")
        {
            report.Note("schema", "no document, so nothing was measured - run with --with-model");
            return;
        }

        report.Check("a throwaway schema registers inside Revit",
            values.GetValueOrDefault("schema:registered") == "ok");

        // Rebuilding the identical definition succeeds, and that is what lets two editions sharing
        // one BHS.Revit.Host.dll in Revit 2024's AppDomain each call Build and meet on one schema.
        report.Check("the same schema definition may be built again",
            values.GetValueOrDefault("schema:sameAgain") == "ok");

        // The one the whole shape rests on. Asserted on the exception type rather than its text:
        // the message is Revit's, and Revit speaks the language it was installed in.
        report.Check("but a field may never be added to it",
            values.GetValueOrDefault("schema:extraField")?.StartsWith("InvalidOperationException", StringComparison.Ordinal) == true);

        // Both sides read from the same answer, so they must be compared against something as well
        // as against each other: two absent keys are equal, and the check would pass by saying
        // nothing. Every sibling here compares to a known value; this one had to be told to.
        var built = values.GetValueOrDefault("schema:fields");

        report.Check("and the refusal leaves the registered definition untouched",
            !string.IsNullOrEmpty(built) && values.GetValueOrDefault("schema:fieldsAfter") == built);

        report.Check("an entity round-trips through the document",
            values.GetValueOrDefault("schema:roundTrip") == "ok"
            && values.GetValueOrDefault("schema:readBack") == "v");

        // Not the tolerance mechanism its name suggests: it answers for the entity's own schema,
        // which is why it cannot help a definition change. Asserted so that a future release
        // quietly changing the answer is noticed.
        var recognized = values.Where(one => one.Key.StartsWith("schema:recognized:", StringComparison.Ordinal)).ToList();

        report.Check("RecognizedField answers for every field of the entity's own schema",
            recognized.Count == 3 && recognized.All(one => one.Value == "True"));

        report.Note("fields as built", values.GetValueOrDefault("schema:fields") ?? "(missing)");
        report.Note("a fourth field under the same GUID", values.GetValueOrDefault("schema:extraField") ?? "(missing)");

        // This one is an assertion whatever the rest says: the measurement must not leave the
        // model dirty, or the sweep meets the save dialog it exists to avoid.
        report.Check("and the measurement left the document unmodified",
            values.GetValueOrDefault("schema:clean") == "True");
    }

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

        // Set changes one key and keeps the rest; Write replaces the layer. A feature that owns one
        // setting and wrote it through Write would erase every other project rule the model holds.
        report.Check("one key is set inside a transaction the caller holds open",
            answer.Values.GetValueOrDefault("model:setAlone") == "set-inside-a-transaction"
            && answer.Values.GetValueOrDefault("model:setCommitted") == "Committed");

        report.Check("and setting it keeps what the layer already held, a cleared key included",
            answer.Values.GetValueOrDefault("model:keptBesideIt") == "written-by-the-probe"
            && answer.Values.GetValueOrDefault("model:clearedBesideIt") == "(unset)");

        report.Check("one key is cleared with no transaction open",
            answer.Values.GetValueOrDefault("model:clearedAlone") == "(unset)");

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
        var fromOurOther = new List<string>();

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

            if (!shipped.Contains(name) || ours)
                continue;

            // Ours winning over ours is a different finding, and calling it "Revit's copy" was
            // wrong the moment a second add-in of ours existed. Measured: with the edition
            // installed beside the probe on Revit 2027, six BHS assemblies loaded out of the
            // edition's folder and this check failed them for not being on RefCheck's watchlist -
            // a list about third-party surfaces, which ours are not.
            //
            // It is still worth a line: which of our two add-ins won is exactly what explains a
            // missing method later. The judgement about it belongs to FrameworkAssemblyCheck,
            // inside Revit, where both versions are in hand.
            if (name.StartsWith("BHS.", StringComparison.Ordinal))
                fromOurOther.Add(name);
            else
                shadowed.Add(name);
        }

        report.Note("assemblies loaded", answer.Values["assembly:count"]);

        foreach (var name in shadowed)
            report.Note("Revit's copy won", name);

        foreach (var name in fromOurOther)
            report.Note("another add-in of ours won", name);

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
