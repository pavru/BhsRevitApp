using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using BHS.Transport;
using BHS.Transport.Configuration;
using BHS.Transport.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace BHS.Revit.Probe.Runner;

internal static class Program
{
    private static readonly Stopwatch LaunchClock = new();
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
        var channel = new RunnerChannel(() => LaunchClock.Elapsed);

        using var server = PipeTransport.CreateServer(PipeNames.WinSide);
        server.Error += (_, error) => Console.WriteLine($"       win-side server error: {error.Error.Message}");
        WinSideChannel.BindService(server.ServiceBinder, channel);
        server.Start();

        Console.WriteLine($"serving {PipeNames.WinSide} as {CurrentUser}");

        try
        {
            foreach (var installation in selected)
                await RunAsync(installation, channel, options, report);
        }
        finally
        {
            server.Kill();
        }

        foreach (var stranger in channel.Unexpected)
        {
            Report.Note(
                "another Revit registered without a token of ours",
                $"release {stranger.RevitVersion}, pid {stranger.ProcessId} - left alone");
        }

        report.Summarise();
        return report.Failures;
    }

    /// <summary>One release: start it, drive it, close it.</summary>
    private static async Task RunAsync(
        RevitInstallation installation,
        RunnerChannel channel,
        Options options,
        Report report)
    {
        Report.Heading($"Revit {installation.Release} - {installation.ExecutablePath}");

        var token = CorrelationToken.New();
        var expectation = channel.Expect(token);

        var startInfo = new ProcessStartInfo(installation.ExecutablePath)
        {
            WorkingDirectory = installation.InstallDirectory,
            UseShellExecute = false,
        };

        // /nosplash is accepted everywhere and honoured on 2026 and later only; on 2024 and 2025
        // the splash window appears regardless. Harmless either way - nothing here waits on a
        // window, which is the point of registering over the channel instead.
        startInfo.ArgumentList.Add("/nosplash");

        // In the environment rather than on the command line: any user on this machine can read
        // another's command line through WMI, and a token that others can read is not a token.
        startInfo.Environment[CorrelationToken.EnvironmentVariable] = token;

        LaunchClock.Restart();

        using var revit = Process.Start(startInfo);
        if (revit is null)
        {
            report.Check($"Revit {installation.Release} starts", false);
            return;
        }

        Report.Note("launched", "pid " + revit.Id.ToString(CultureInfo.InvariantCulture));

        try
        {
            var registration = await WaitForRegistrationAsync(expectation, revit, options.RegistrationTimeout);

            if (!report.Check("the add-in registers over the well-known pipe", registration is not null))
            {
                // The one path the runner has to know without being told, because a probe that
                // never registered never got to tell it anything.
                Report.Note("probe log", ProbeLogPath(revit.Id));
                Report.Note("no log there means", "the add-in was never loaded - check the Revit journal");
                return;
            }

            await InspectAsync(installation, registration!, revit, options, report);
        }
        finally
        {
            channel.Forget(token);
            Finish(revit, options, report);
        }
    }

    /// <summary>Everything worth asking a Revit that has announced itself.</summary>
    private static async Task InspectAsync(
        RevitInstallation installation,
        Registration registration,
        Process revit,
        Options options,
        Report report)
    {
        var request = registration.Request;
        Report.Note("registered after", registration.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");

        // The launcher question, settled from the inside this time: the process that reports for
        // duty is the process that was started.
        report.Check("the registering process is the one that was launched", request.ProcessId == revit.Id);
        report.Check("the registration names the right release", request.RevitVersion == installation.Release.Year);
        report.Check(
            "the caller is this account",
            string.Equals(registration.CallerAccount, CurrentUser, StringComparison.OrdinalIgnoreCase));

        // Layer two from the client side: who answers to the name the add-in gave us.
        var served = PeerIdentity.TryGetServerProcess(
            request.PipeName, TimeSpan.FromSeconds(5), out var serverPid, out var image);

        report.Check("a pipe server lives inside the Revit process", served && serverPid == revit.Id);
        report.Check(
            "its image is the Revit that was started",
            image is not null && string.Equals(image, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(request.PipeName));

        var snapshot = await client.GetConfigurationAsync(new ConfigurationRequest());
        report.Check(
            "GetConfiguration is answered from inside Revit",
            Value(snapshot, "Revit:Release") == installation.Release.Year.ToString(CultureInfo.InvariantCulture));

        report.Check("the probe saw the correlation token", Value(snapshot, "Instance:StartedByRunner") == "True");

        var addInDirectory = Value(snapshot, "AddIn:Directory");
        Report.Note("build", Value(snapshot, "Revit:VersionBuild") + ", language " + Value(snapshot, "Revit:Language"));
        Report.Note("loaded from", addInDirectory);
        Report.Note("probe log", Value(snapshot, "AddIn:Log"));

        await CheckConfigurationFlowAsync(request.PipeName, client, snapshot, report);
        await CheckContextAsync(client, report);
        await CheckAssembliesAsync(client, installation, addInDirectory, report);

        if (options.KeepOpen)
        {
            Report.Note("left running", "pid " + revit.Id.ToString(CultureInfo.InvariantCulture));
            return;
        }

        await CloseAsync(client, revit, options, report);
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

    /// <summary>Close it the way it is meant to be closed, and measure how long that takes.</summary>
    private static async Task CloseAsync(
        RevitSideChannel.RevitSideChannelClient client,
        Process revit,
        Options options,
        Report report)
    {
        var closing = Stopwatch.StartNew();

        try
        {
            await client.ShutdownAsync(new ShutdownRequest { Reason = "probe sweep finished" });
        }
        catch (RpcException error)
        {
            Report.Note("shutdown call failed", error.Status.Detail);
        }

        var exited = await WaitForExitAsync(revit, options.ShutdownTimeout);
        report.Check("Revit closes on an ExitRevit posted from inside", exited);

        if (exited)
            Report.Note("closed after", closing.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");
    }

    private static HashSet<string> ShippedAssemblies(RevitInstallation installation)
    {
        var lib = Path.Combine(installation.ProbeDirectory, "Lib");
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(lib))
            return shipped;

        foreach (var file in Directory.GetFiles(lib, "*.dll"))
            shipped.Add(Path.GetFileNameWithoutExtension(file));

        return shipped;
    }

    /// <summary>
    /// Waits for a registration, watching the process rather than only the clock.
    /// </summary>
    /// <remarks>
    /// A cold Revit start is around a minute, so a plain timeout would turn "it crashed on
    /// startup" into four minutes of waiting per release. Watching the handle turns it into a
    /// failure at the moment it happens.
    /// </remarks>
    private static async Task<Registration?> WaitForRegistrationAsync(
        Task<Registration> expectation,
        Process revit,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await Task.WhenAny(expectation, Task.Delay(500)) == expectation)
                return await expectation;

            if (revit.HasExited)
            {
                Report.Note("Revit exited before registering", "exit code " + revit.ExitCode.ToString(CultureInfo.InvariantCulture));
                return null;
            }
        }

        return null;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

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
    private static void Finish(Process revit, Options options, Report report)
    {
        if (options.KeepOpen)
            return;

        try
        {
            if (revit.HasExited)
                return;

            Report.Note("killing", "pid " + revit.Id.ToString(CultureInfo.InvariantCulture) + " - it did not close on its own");
            revit.Kill(entireProcessTree: true);
            revit.WaitForExit(10000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            report.Check("the leftover Revit could be killed: " + error.Message, false);
        }
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

        var missing = selected.Where(installation => !installation.IsProbeDeployed).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine("The probe is not installed for: " + string.Join(", ", missing.Select(one => one.Release)));
            Console.WriteLine("Install it with --deploy, or by hand:");
            Console.WriteLine("  dotnet build " + ProbeDeployment.ProjectPath + " -c Release -p:RevitDeploy=Local");
            return new List<RevitInstallation>();
        }

        // Checked rather than assumed, because the failure is silent from out here: an untrusted
        // add-in makes Revit raise a modal dialog whose default answer is "do not load", and the
        // sweep would simply wait out its deadline in front of it.
        var untrusted = selected.Where(installation => !installation.IsProbeTrusted).ToList();
        if (untrusted.Count == 0)
            return selected;

        if (options.AllowUntrusted)
        {
            Console.WriteLine("not trusted for " + string.Join(", ", untrusted.Select(one => one.Release))
                              + " - answer Revit's dialog with \"Always load\" when it appears.");
            return selected;
        }

        Console.WriteLine("The probe is not trusted for: " + string.Join(", ", untrusted.Select(one => one.Release)));
        Console.WriteLine("Revit would stop at its unsigned-add-in dialog. Run --deploy to record the trust.");
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
