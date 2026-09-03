using System.Diagnostics;
using System.Security.Principal;
using BHS.Transport;
using BHS.Transport.Configuration;
using BHS.Transport.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace BHS.Transport.Probe;

internal sealed class WinSide : WinSideChannel.WinSideChannelBase
{
    public string? LastCaller { get; private set; }

    public RegisterRequest? LastRegistration { get; private set; }

    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        Handshake.EnsureCompatible(request.ContractVersion);

        LastCaller = PeerIdentity.TryGetCallerUserName(context);
        LastRegistration = request;

        return Task.FromResult(new RegisterResponse
        {
            ContractVersion = request.ContractVersion,
            InstanceId = "win-side-1",
        });
    }

    public override Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var response = new AskResponse();
        response.Values.Add("echo", request.Question);
        foreach (var pair in request.Arguments)
            response.Values.Add("arg:" + pair.Key, pair.Value);
        return Task.FromResult(response);
    }
}

internal sealed class RevitSide : RevitSideChannel.RevitSideChannelBase
{
    private readonly TaskCompletionSource<string> _shutdown = new();

    public ConfigurationPublisher Publisher { get; } = new("revit-side-1");

    public Task<string> ShutdownRequested => _shutdown.Task;

    public RevitSide()
    {
        Publisher.Publish(new Dictionary<string, string>
        {
            ["Revit:Release"] = "2026",
            ["Revit:Document:Title"] = "initial.rvt",
            ["Process:Id"] = Current.ProcessId.ToString(),
        });
    }

    public override Task WatchConfiguration(
        ConfigurationRequest request,
        IServerStreamWriter<ConfigurationSnapshot> responseStream,
        ServerCallContext context) =>
        Publisher.WatchAsync(responseStream, context.CancellationToken);

    public override Task<ConfigurationSnapshot> GetConfiguration(ConfigurationRequest request, ServerCallContext context) =>
        Task.FromResult(Publisher.Current);

    public override Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var response = new AskResponse();

        if (request.Question == "publish")
        {
            Publisher.Publish(new Dictionary<string, string>
            {
                ["Revit:Release"] = "2026",
                ["Revit:Document:Title"] = request.Arguments["title"],
                ["Process:Id"] = Current.ProcessId.ToString(),
            });
        }

        response.Values.Add("pid", Current.ProcessId.ToString());
        response.Values.Add("revision", Publisher.Current.Revision.ToString());
        return Task.FromResult(response);
    }

    public override Task<ShutdownResponse> Shutdown(ShutdownRequest request, ServerCallContext context)
    {
        _shutdown.TrySetResult(request.Reason);
        return Task.FromResult(new ShutdownResponse());
    }
}

/// <summary>
/// A Revit-side end that answers, but will not say which contract it speaks.
/// </summary>
/// <remarks>
/// The impostor recovery has to refuse. Everything about it is right - the name parses, the
/// process serving it is the one the name is written for - and the only thing wrong is the one
/// thing a handshake would have caught, which on the recovery path there is no handshake to catch.
/// </remarks>
internal sealed class MuteRevitSide : RevitSideChannel.RevitSideChannelBase
{
    public override Task<ConfigurationSnapshot> GetConfiguration(ConfigurationRequest request, ServerCallContext context) =>
        Task.FromResult(new ConfigurationSnapshot { InstanceId = "mute", Revision = 1 });
}

/// <summary>The Win-side end the registry checks use: registration handled by the framework.</summary>
internal sealed class RegistryWinSide : WinSideService
{
    public RegistryWinSide(RevitInstanceRegistry registry)
        : base(registry, "win-side-registry")
    {
    }
}

internal static class Current
{
    public static int ProcessId { get; } = Process.GetCurrentProcess().Id;

    public static string ExecutablePath { get; } =
        Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public static string UserName { get; } = WindowsIdentity.GetCurrent().Name;
}

internal static class Program
{
    private const string ServeArgument = "serve";

    private static async Task<int> Main(string[] args)
    {
        // Child mode: be a Revit-side process for the parent to talk to.
        if (args.Length == 2 && args[0] == ServeArgument)
            return await ServeAsync(args[1]);

        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        Console.WriteLine($"== {framework}, pid {Current.ProcessId}");

        var failures = 0;
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        failures += await InProcessAsync(suffix);
        failures += await RegistryAsync(suffix);
        failures += await CrossProcessAsync(suffix);

        Console.WriteLine(failures == 0 ? "== all checks passed" : $"== {failures} check(s) FAILED");
        return failures;
    }

    /// <summary>Both ends in this process: the contract itself, and the shapes of a call.</summary>
    private static async Task<int> InProcessAsync(string suffix)
    {
        var failures = 0;

        var winPipe = PipeNames.WinSide + ".probe." + suffix;
        var revitPipe = PipeNames.RevitSideInstance(Current.ProcessId, 2026) + ".probe." + suffix;

        var winService = new WinSide();
        var serverErrors = new List<Exception>();

        using var winServer = PipeTransport.CreateServer(winPipe);
        winServer.Error += (_, e) => serverErrors.Add(e.Error);
        WinSideChannel.BindService(winServer.ServiceBinder, winService);
        winServer.Start();

        var revitService = new RevitSide();
        using var revitServer = PipeTransport.CreateServer(revitPipe);
        RevitSideChannel.BindService(revitServer.ServiceBinder, revitService);
        revitServer.Start();

        var winClient = new WinSideChannel.WinSideChannelClient(PipeTransport.CreateClient(winPipe));

        var registered = await winClient.RegisterAsync(new RegisterRequest
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = "revit-side-1",
            CorrelationToken = "token-" + suffix,
            PipeName = revitPipe,
            RevitVersion = 2026,
            ProcessId = Current.ProcessId,
        });
        Check(ref failures, "unary Register",
            registered.InstanceId == "win-side-1" && registered.ContractVersion == Handshake.ContractVersion);

        // Layer three: a peer on another contract is refused rather than tolerated.
        var rejected = StatusCode.OK;
        try
        {
            await winClient.RegisterAsync(new RegisterRequest { ContractVersion = "0", InstanceId = "stranger" });
        }
        catch (RpcException wrong)
        {
            rejected = wrong.StatusCode;
        }
        Check(ref failures, "handshake refuses another contract", rejected == StatusCode.FailedPrecondition);

        var unstated = StatusCode.OK;
        try
        {
            await winClient.RegisterAsync(new RegisterRequest { InstanceId = "stranger" });
        }
        catch (RpcException silent)
        {
            unstated = silent.StatusCode;
        }
        Check(ref failures, "handshake refuses an unstated contract", unstated == StatusCode.InvalidArgument);

        Check(ref failures, "correlation token is absent when nobody set one",
            CorrelationToken.FromEnvironment() is null && CorrelationToken.New().Length == 32);

        var ask = new AskRequest { Question = "who-are-you" };
        ask.Arguments.Add("k1", "v1");
        ask.Arguments.Add("k2", "значение");
        var answer = await winClient.AskAsync(ask);
        Check(ref failures, "map round-trip",
            answer.Values["echo"] == "who-are-you"
            && answer.Values["arg:k1"] == "v1"
            && answer.Values["arg:k2"] == "значение");

        // Server streaming: the reason a ready-made RPC library is worth having, and the thing
        // .NET Framework gRPC cannot do over HTTP/2 at all.
        var revitClient = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(revitPipe));
        using var watch = revitClient.WatchConfiguration(new ConfigurationRequest());

        var first = await watch.ResponseStream.MoveNext(CancellationToken.None)
            ? watch.ResponseStream.Current
            : null;

        revitService.Publisher.Publish(new Dictionary<string, string> { ["Revit:Document:Title"] = "changed.rvt" });

        var second = await watch.ResponseStream.MoveNext(CancellationToken.None)
            ? watch.ResponseStream.Current
            : null;

        Check(ref failures, "streaming WatchConfiguration",
            first is not null && second is not null
            && first.Values["Revit:Document:Title"] == "initial.rvt"
            && second.Values["Revit:Document:Title"] == "changed.rvt"
            && second.Revision > first.Revision);

        // Layer two, server side: the account the call came from.
        Check(ref failures, "caller identified by impersonation",
            string.Equals(winService.LastCaller, Current.UserName, StringComparison.OrdinalIgnoreCase));

        // Layer two, client side: which process is answering to this name.
        var found = PeerIdentity.TryGetServerProcess(winPipe, TimeSpan.FromSeconds(2), out var serverPid, out var image);
        Check(ref failures, "server process identified",
            found && serverPid == Current.ProcessId
                  && string.Equals(image, Current.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        // A liveness probe has to fail quickly against a name nobody serves.
        var absent = new WinSideChannel.WinSideChannelClient(
            PipeTransport.CreateClient("BHS.WinSide.nobody." + suffix, TimeSpan.FromMilliseconds(300)));
        var refused = false;
        try
        {
            await absent.AskAsync(new AskRequest { Question = "anyone?" });
        }
        catch (RpcException)
        {
            refused = true;
        }
        Check(ref failures, "absent server fails fast", refused);

        Check(ref failures, "absent server has no process",
            !PeerIdentity.TryGetServerProcess("BHS.WinSide.nobody." + suffix, TimeSpan.FromMilliseconds(300), out _, out _));

        var enumerated = PipeNames.Enumerate();
        Check(ref failures, "enumeration finds both servers",
            enumerated.Contains(winPipe) && enumerated.Contains(revitPipe));

        Check(ref failures, "name parses back",
            PipeNames.TryParseRevitSide(PipeNames.RevitSideInstance(4242, 2027), out var pid, out var release)
            && pid == 4242 && release == 2027);

        // Identifying the peer costs a connection of its own, because the library keeps its pipe
        // handle private. Whether that connection upsets the server is worth knowing rather than
        // assuming.
        Check(ref failures, "peer check leaves the server unbothered", serverErrors.Count == 0);
        foreach (var error in serverErrors)
            Console.WriteLine($"       server error: {error.GetType().Name}: {error.Message}");

        return failures;
    }

    /// <summary>
    /// The register Win-side keeps of the Revit processes it can talk to.
    /// </summary>
    /// <remarks>
    /// Everything here except a process actually dying, which needs a second process and is
    /// checked in <see cref="CrossProcessAsync"/>. Worth doing on every runtime rather than only
    /// where Win-side runs: the transport is built for .NET Framework as well, and a registry that
    /// quietly stops compiling or working there would be found by a Revit sweep at the earliest.
    /// </remarks>
    private static async Task<int> RegistryAsync(string suffix)
    {
        var failures = 0;

        // Named the way a real Revit-side names itself. Recovery reads a process id and a release
        // back out of the name, and a name with a probe suffix on it does not parse.
        var revitPipe = PipeNames.RevitSideInstance(Current.ProcessId, 2026);
        var mutePipe = PipeNames.RevitSideInstance(Current.ProcessId, 2099);
        var ghostPipe = PipeNames.RevitSideInstance(Current.ProcessId, 2098);
        var winPipe = PipeNames.WinSide + ".registry." + suffix;

        // Short on purpose: everything here is in one process, and one check runs against a name
        // nobody serves, where the timeout is what is being waited out.
        using var registry = new RevitInstanceRegistry(TimeSpan.FromMilliseconds(700));

        var arrived = new System.Collections.Concurrent.ConcurrentBag<RevitInstance>();
        var departed = new System.Collections.Concurrent.ConcurrentBag<RevitInstance>();
        registry.Arrived += (_, e) => arrived.Add(e.Instance);
        registry.Departed += (_, e) => departed.Add(e.Instance);

        using var winServer = PipeTransport.CreateServer(winPipe);
        WinSideChannel.BindService(winServer.ServiceBinder, new RegistryWinSide(registry));
        winServer.Start();

        using var revitServer = PipeTransport.CreateServer(revitPipe);
        RevitSideChannel.BindService(revitServer.ServiceBinder, new RevitSide());
        revitServer.Start();

        using var muteServer = PipeTransport.CreateServer(mutePipe);
        RevitSideChannel.BindService(muteServer.ServiceBinder, new MuteRevitSide());
        muteServer.Start();

        var client = new WinSideChannel.WinSideChannelClient(PipeTransport.CreateClient(winPipe));

        // Expect before register, the way the runner does it before starting a process.
        var token = "registry-" + suffix;
        var expectation = registry.Expect(token);

        await client.RegisterAsync(new RegisterRequest
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = "revit-side-1",
            CorrelationToken = token,
            PipeName = revitPipe,
            RevitVersion = 2026,
            ProcessId = Current.ProcessId,
        });

        var claimed = await Task.WhenAny(expectation, Task.Delay(5000)) == expectation ? await expectation : null;
        Check(ref failures, "registry: an expected token completes when it arrives", claimed is not null);

        if (claimed is not null)
        {
            Check(ref failures, "registry: the instance is the one that was expected",
                claimed.CorrelationToken == token && claimed.StartedByUs && claimed.Release == 2026);

            // Layer two applied by the registry rather than merely available to it.
            Check(ref failures, "registry: the peer is verified against its own pipe", claimed.Verified);

            Check(ref failures, "registry: the caller account is recorded",
                string.Equals(claimed.Account, Current.UserName, StringComparison.OrdinalIgnoreCase));

            Check(ref failures, "registry: it is watched for exit",
                registry.Watched.Any(one => one.InstanceId == claimed.InstanceId));
        }

        Check(ref failures, "registry: lookup by correlation token",
            registry.ByCorrelationToken(token)?.InstanceId == "revit-side-1");

        // A registration nobody started. This is the ordinary case - most Revit sessions begin
        // with somebody double-clicking - and the registry has to record it without claiming it.
        await client.RegisterAsync(new RegisterRequest
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = "revit-side-nobodys",
            PipeName = mutePipe,
            RevitVersion = 2099,
            ProcessId = Current.ProcessId,
        });

        Check(ref failures, "registry: an untokened instance is recorded but not claimed",
            registry.TryGet("revit-side-nobodys", out var strangers) && strangers is { StartedByUs: false });

        // A peer whose pipe nobody serves. Recorded, because a momentarily busy companion is not
        // an impostor - and marked, because an impostor is what it might be.
        await client.RegisterAsync(new RegisterRequest
        {
            ContractVersion = Handshake.ContractVersion,
            InstanceId = "revit-side-ghost",
            PipeName = ghostPipe,
            RevitVersion = 2098,
            ProcessId = Current.ProcessId,
        });

        Check(ref failures, "registry: a peer that cannot be verified is recorded unverified",
            registry.TryGet("revit-side-ghost", out var ghost) && ghost is { Verified: false });

        Check(ref failures, "registry: three instances, one arrival event each",
            registry.Instances.Count == 3 && arrived.Count == 3);

        // A Win-side that restarted: nothing in hand, everything to be found again.
        using (var restarted = new RevitInstanceRegistry(TimeSpan.FromMilliseconds(700)))
        {
            await restarted.RecoverAsync();

            var recovered = restarted.Instances.FirstOrDefault(one => one.PipeName == revitPipe);

            Check(ref failures, "registry: enumeration recovers a live instance",
                recovered is { Origin: RevitInstanceOrigin.Recovered, Verified: true }
                && recovered.InstanceId == "revit-side-1"
                && recovered.Release == 2026);

            Check(ref failures, "registry: a recovered instance is never claimed as ours",
                recovered is { StartedByUs: false });

            // The one thing recovery has no handshake for, so the snapshot has to carry it.
            Check(ref failures, "registry: recovery refuses a peer that names no contract",
                restarted.Instances.All(one => one.PipeName != mutePipe));
        }

        var dropped = await registry.SweepAsync();
        Check(ref failures, "registry: a sweep drops what no longer answers",
            dropped == 1 && !registry.TryGet("revit-side-ghost", out _));

        Check(ref failures, "registry: departure is announced",
            departed.Any(one => one.InstanceId == "revit-side-ghost"));

        Check(ref failures, "registry: forgetting an instance twice is not an error",
            registry.Forget("revit-side-nobodys") && !registry.Forget("revit-side-nobodys"));

        return failures;
    }

    /// <summary>The real shape: two processes, one pipe between them.</summary>
    private static async Task<int> CrossProcessAsync(string suffix)
    {
        var failures = 0;
        var childPipe = PipeNames.Prefix + "RevitSide.child." + suffix;

        if (string.IsNullOrEmpty(Current.ExecutablePath))
        {
            Check(ref failures, "cross-process: executable path known", false);
            return failures;
        }

        using var child = new Process
        {
            StartInfo =
            {
                FileName = Current.ExecutablePath,
                Arguments = ServeArgument + " " + childPipe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            },
        };

        child.Start();

        // Wait for the child to say the pipe exists, rather than racing it.
        var ready = await Task.Run(() => child.StandardOutput.ReadLine());
        if (ready != "ready")
        {
            Check(ref failures, "cross-process: child announced itself", false);
            TryKill(child);
            return failures;
        }

        try
        {
            var found = PeerIdentity.TryGetServerProcess(childPipe, TimeSpan.FromSeconds(5), out var serverPid, out var image);
            Check(ref failures, "cross-process: peer is the child process",
                found && serverPid == child.Id && !string.IsNullOrEmpty(image));

            // The one thing a single process cannot check: a registered instance dying. Nothing
            // tells the registry - it watches the process it was given, which is the only way a
            // departure is noticed at the moment it happens rather than at the next sweep.
            using var registry = new RevitInstanceRegistry(TimeSpan.FromSeconds(2));
            var recorded = registry.Record(
                new RegisterRequest
                {
                    ContractVersion = Handshake.ContractVersion,
                    InstanceId = "child-" + suffix,
                    PipeName = childPipe,
                    RevitVersion = 2026,
                    ProcessId = child.Id,
                },
                Current.UserName);

            Check(ref failures, "cross-process: another process verifies as itself",
                recorded.Verified && registry.Watched.Count == 1);

            var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(childPipe));

            var snapshot = await client.GetConfigurationAsync(new ConfigurationRequest());
            Check(ref failures, "cross-process: configuration answered by the child",
                snapshot.Values["Process:Id"] == child.Id.ToString());

            // The whole point of the channel: what the companion publishes shows up as ordinary
            // configuration, and a change reaches it through the same reload path a file would.
            var source = new PeerConfigurationSource { PipeName = childPipe };
            var configuration = new ConfigurationBuilder().Add(source).Build();

            var arrived = await WaitForAsync(() => configuration["Revit:Document:Title"] == "initial.rvt");
            Check(ref failures, "cross-process: first snapshot arrives as configuration", arrived);

            var reloaded = false;
            using (ChangeToken.OnChange(configuration.GetReloadToken, () => reloaded = true))
            {
                var publish = new AskRequest { Question = "publish" };
                publish.Arguments.Add("title", "renamed.rvt");
                await client.AskAsync(publish);

                var updated = await WaitForAsync(() => configuration["Revit:Document:Title"] == "renamed.rvt");
                Check(ref failures, "cross-process: a change reaches the consumer", updated);
                Check(ref failures, "cross-process: the change token fired", reloaded);
            }

            Check(ref failures, "cross-process: section binding works",
                configuration.GetSection("Revit")["Release"] == "2026");

            await client.ShutdownAsync(new ShutdownRequest { Reason = "probe finished" });

            var exited = child.WaitForExit(5000);
            Check(ref failures, "cross-process: child exits on Shutdown", exited && child.ExitCode == 0);

            var forgotten = await WaitForAsync(() => !registry.TryGet("child-" + suffix, out _), 5000);
            Check(ref failures, "cross-process: the registry drops it when the process goes", forgotten);
        }
        finally
        {
            TryKill(child);
        }

        return failures;
    }

    private static async Task<int> ServeAsync(string pipeName)
    {
        var service = new RevitSide();

        using var server = PipeTransport.CreateServer(pipeName);
        RevitSideChannel.BindService(server.ServiceBinder, service);
        server.Start();

        Console.WriteLine("ready");
        Console.Out.Flush();

        var reason = await service.ShutdownRequested;

        // Let the reply reach the caller before the pipe goes away with the process.
        await Task.Delay(200);

        server.Kill();
        return string.IsNullOrEmpty(reason) ? 1 : 0;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int millisecondsTimeout = 10000)
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

    private static void Check(ref int failures, string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok) failures++;
    }
}
