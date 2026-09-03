using System.Diagnostics;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Transport.Probe;

internal sealed class WinSide : WinSideChannel.WinSideChannelBase
{
    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        Console.WriteLine($"  server saw Register: revit={request.RevitVersion} pid={request.ProcessId} pipe={request.PipeName}");
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
}

internal static class Program
{
    private static async Task<int> Main()
    {
        var runtime = Environment.Version.ToString();
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        Console.WriteLine($"== {framework} (CLR {runtime}), pid {Process.GetCurrentProcess().Id}");

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var winPipe = PipeNames.WinSide + ".test." + suffix;
        var revitPipe = PipeNames.RevitSideInstance(Process.GetCurrentProcess().Id, 2026) + ".test." + suffix;

        using var winServer = PipeTransport.CreateServer(winPipe);
        WinSideChannel.BindService(winServer.ServiceBinder, new WinSide());
        winServer.Start();

        using var revitServer = PipeTransport.CreateServer(revitPipe);
        RevitSideChannel.BindService(revitServer.ServiceBinder, new RevitSide());
        revitServer.Start();

        var failures = 0;

        // 1. Unary call across the pipe.
        var winClient = new WinSideChannel.WinSideChannelClient(PipeTransport.CreateClient(winPipe));
        var registered = await winClient.RegisterAsync(new RegisterRequest
        {
            ContractVersion = "1",
            InstanceId = "revit-side-1",
            CorrelationToken = "token-" + suffix,
            PipeName = revitPipe,
            RevitVersion = 2026,
            ProcessId = Process.GetCurrentProcess().Id,
        });
        Check(ref failures, "unary Register", registered.InstanceId == "win-side-1" && registered.ContractVersion == "1");

        // 2. map<string, string> round-trip, both directions.
        var ask = new AskRequest { Question = "who-are-you" };
        ask.Arguments.Add("k1", "v1");
        ask.Arguments.Add("k2", "значение");
        var answer = await winClient.AskAsync(ask);
        Check(ref failures, "map round-trip",
            answer.Values["echo"] == "who-are-you"
            && answer.Values["arg:k1"] == "v1"
            && answer.Values["arg:k2"] == "значение");

        // 3. Server streaming - the part that decides whether a ready-made RPC library is worth
        //    having at all, and the part .NET Framework gRPC cannot do over HTTP/2.
        var revitClient = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(revitPipe));
        using var watch = revitClient.WatchConfiguration(new ConfigurationRequest());
        var snapshots = new List<ConfigurationSnapshot>();
        while (await watch.ResponseStream.MoveNext(CancellationToken.None))
            snapshots.Add(watch.ResponseStream.Current);

        Check(ref failures, "streaming WatchConfiguration",
            snapshots.Count == 3
            && snapshots[0].Revision == 1
            && snapshots[2].Values["Revit:Document:Title"] == "model-3.rvt");

        // 4. Deadlines, the way a liveness probe would use them.
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

        // 5. Discovery by enumeration, the recovery path.
        var enumerated = PipeNames.Enumerate();
        Check(ref failures, "enumeration finds both servers",
            enumerated.Contains(winPipe) && enumerated.Contains(revitPipe));
        Check(ref failures, "name parses back",
            PipeNames.TryParseRevitSide(PipeNames.RevitSideInstance(4242, 2027), out var pid, out var release)
            && pid == 4242 && release == 2027);

        Console.WriteLine(failures == 0 ? "== all checks passed" : $"== {failures} check(s) FAILED");
        return failures;
    }

    private static void Check(ref int failures, string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok) failures++;
    }
}
