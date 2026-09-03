using System.Diagnostics;
using System.Security.Principal;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

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

    public Task<string> ShutdownRequested => _shutdown.Task;

    public override async Task WatchConfiguration(
        ConfigurationRequest request,
        IServerStreamWriter<ConfigurationSnapshot> responseStream,
        ServerCallContext context)
    {
        for (ulong revision = 1; revision <= 3; revision++)
        {
            var snapshot = new ConfigurationSnapshot { InstanceId = "revit-side-1", Revision = revision };
            snapshot.Values.Add("Revit:Release", "2026");
            snapshot.Values.Add("Revit:Document:Title", $"model-{revision}.rvt");
            await responseStream.WriteAsync(snapshot);
        }
    }

    public override Task<ConfigurationSnapshot> GetConfiguration(ConfigurationRequest request, ServerCallContext context)
    {
        var snapshot = new ConfigurationSnapshot { InstanceId = "revit-side-1", Revision = 1 };
        snapshot.Values.Add("Revit:Release", "2026");
        snapshot.Values.Add("Process:Id", Current.ProcessId.ToString());
        return Task.FromResult(snapshot);
    }

    public override Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var response = new AskResponse();
        response.Values.Add("pid", Current.ProcessId.ToString());
        return Task.FromResult(response);
    }

    public override Task<ShutdownResponse> Shutdown(ShutdownRequest request, ServerCallContext context)
    {
        _shutdown.TrySetResult(request.Reason);
        return Task.FromResult(new ShutdownResponse());
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

        using var revitServer = PipeTransport.CreateServer(revitPipe);
        RevitSideChannel.BindService(revitServer.ServiceBinder, new RevitSide());
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
        var snapshots = new List<ConfigurationSnapshot>();
        while (await watch.ResponseStream.MoveNext(CancellationToken.None))
            snapshots.Add(watch.ResponseStream.Current);

        Check(ref failures, "streaming WatchConfiguration",
            snapshots.Count == 3
            && snapshots[0].Revision == 1
            && snapshots[2].Values["Revit:Document:Title"] == "model-3.rvt");

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

            var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(childPipe));

            var configuration = await client.GetConfigurationAsync(new ConfigurationRequest());
            Check(ref failures, "cross-process: configuration answered by the child",
                configuration.Values["Process:Id"] == child.Id.ToString());

            using var watch = client.WatchConfiguration(new ConfigurationRequest());
            var count = 0;
            while (await watch.ResponseStream.MoveNext(CancellationToken.None))
                count++;
            Check(ref failures, "cross-process: streaming across the boundary", count == 3);

            await client.ShutdownAsync(new ShutdownRequest { Reason = "probe finished" });

            var exited = child.WaitForExit(5000);
            Check(ref failures, "cross-process: child exits on Shutdown", exited && child.ExitCode == 0);
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

    private static void Check(ref int failures, string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok) failures++;
    }
}
