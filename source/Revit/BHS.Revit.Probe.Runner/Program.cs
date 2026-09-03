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
                await RunAsync(installation, launcher, registry, options, report);
        }
        finally
        {
            server.Kill();
        }

        // Anything still here either was never ours or never left, and the two are worth telling
        // apart: a stranger is somebody's own Revit, a leftover is a registry that missed a death.
        foreach (var left in registry.Instances)
        {
            Report.Note(
                left.StartedByUs ? "still registered after the sweep" : "another Revit registered without a token of ours",
                $"release {left.Release}, pid {left.ProcessId} - left alone");
        }

        report.Summarise();
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
        Report report)
    {
        Report.Heading($"Revit {installation.Release} - {installation.ExecutablePath}");

        using var session = await launcher.LaunchAsync(
            installation,
            new RevitLaunchOptions
            {
                RegistrationTimeout = options.RegistrationTimeout,
                ShutdownTimeout = options.ShutdownTimeout,
            });

        if (session.Process is not null)
            Report.Note("launched", "pid " + session.Process.Id.ToString(CultureInfo.InvariantCulture));

        try
        {
            if (!report.Check("the add-in registers over the well-known pipe", session.Registered))
            {
                ExplainFailedLaunch(session);
                return;
            }

            Report.Note("registered after", session.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

            await InspectAsync(installation, registry, session, options, report);
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
    private static void ExplainFailedLaunch(RevitSession session)
    {
        switch (session.Outcome)
        {
            case RevitLaunchOutcome.NotStarted:
                Report.Note("Revit would not start at all", session.Installation.ExecutablePath);
                break;

            case RevitLaunchOutcome.ExitedBeforeRegistering:
                Report.Note("Revit exited before registering", "exit code " + ExitCode(session));
                break;

            default:
                Report.Note("probe log", ProbeLogPath(session.Process?.Id ?? 0));
                Report.Note("no log there means", "the add-in was never loaded - check the Revit journal");
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
        Report.Note("build", Value(snapshot, "Revit:VersionBuild") + ", language " + Value(snapshot, "Revit:Language"));
        Report.Note("loaded from", addInDirectory);
        Report.Note("probe log", Value(snapshot, "AddIn:Log"));

        report.Check("the snapshot names the contract it speaks", snapshot.ContractVersion == Handshake.ContractVersion);

        await CheckConfigurationFlowAsync(instance.PipeName, client, snapshot, report);
        await CheckContextAsync(client, report);
        await CheckAssembliesAsync(client, installation, addInDirectory, report);

        if (options.KeepOpen)
        {
            Report.Note("left running", "pid " + revit.Id.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>Where the call lands, and in which AppDomain the add-in is living.</summary>
    private static async Task CheckContextAsync(RevitSideChannel.RevitSideChannelClient client, Report report)
    {
        var context = await client.AskAsync(new AskRequest { Question = "context" });

        var api = context.Values["thread:api"];
        var calling = context.Values["thread:calling"];

        // Not a defect: it is the constraint every Revit-side feature has to be built around, and
        // the reason closing Revit goes through an external event rather than a direct call.
        report.Check("the call arrives off the Revit API thread", api != calling);

        Report.Note("threads", $"api {api}, call {calling}");
        Report.Note("appdomain", $"{context.Values["appdomain:name"]} (#{context.Values["appdomain:id"]})");
        Report.Note("runtime", context.Values["runtime"]);
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

            Report.Note($"{name} {version}", $"{origin}  {location}");

            if (shipped.Contains(name) && !ours)
                shadowed.Add(name);
        }

        Report.Note("assemblies loaded", answer.Values["assembly:count"]);

        foreach (var name in shadowed)
            Report.Note("Revit's copy won", name);

        // Substitution itself is not the question - Revit loads first and always wins. The question
        // is whether the copy that won has been checked, and RefCheck's watchlist is the record of
        // that. Anything substituted from outside the list went unverified.
        var vetted = RefCheckWatchlist.TryLoad(ProbeInstaller.FindRepositoryRoot());

        if (vetted is null)
        {
            Report.Note("RefCheck watchlist", "not readable from here, so substitutions are unjudged");
            return;
        }

        var unverified = shadowed.Where(name => !vetted.Contains(name)).ToList();
        report.Check("every assembly Revit substituted is one RefCheck vets", unverified.Count == 0);

        foreach (var name in unverified)
            Report.Note("substituted but not on the watchlist", name);
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

        Report.Note("recovered by enumeration", found.ToString(CultureInfo.InvariantCulture) + " instance(s)");
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

        Report.Note("closed after", closing.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

        // Nothing told the registry; it was watching the process. A departure that has to be swept
        // for is a departure nobody hears about until somebody asks.
        var dropped = await WaitForAsync(() => !registry.TryGet(instance.InstanceId, out _), 10000);
        report.Check("the registry drops it when the process goes", dropped);
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

        Report.Note("killing", "pid " + (session.Process?.Id.ToString(CultureInfo.InvariantCulture) ?? "?")
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
                var ours = string.Equals(addIn.AddInId, ProbeDeployment.AddInId, StringComparison.OrdinalIgnoreCase);
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
    private static string ProbeLogPath(int processId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BHS.Revit.Probe",
            $"probe.{processId.ToString(CultureInfo.InvariantCulture)}.log");

    private static bool IsUnder(string path, string directory) =>
        !string.IsNullOrEmpty(path)
        && !string.IsNullOrEmpty(directory)
        && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);

    private static string Value(ConfigurationSnapshot snapshot, string key) =>
        snapshot.Values.TryGetValue(key, out var value) ? value : string.Empty;
}
