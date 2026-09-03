using System.Globalization;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.WinSide;

/// <summary>
/// Runs the host, or talks to one that is already running.
/// </summary>
/// <remarks>
/// One executable for both because the second is how the first is used: everything the client mode
/// does goes over the same channel a Revit-side add-in or a future window would use, so exercising
/// it here means exercising the real path rather than a shortcut past it.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args.Length == 0
                ? await RunHostAsync()
                : await RunClientAsync(args);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
        {
            Console.WriteLine($"Nothing is serving {PipeNames.WinSide}. Start the host first.");
            return 1;
        }
        catch (RpcException error)
        {
            Console.WriteLine($"{error.StatusCode}: {error.Status.Detail}");
            return 1;
        }
    }

    private static async Task<int> RunHostAsync()
    {
        using var host = new WinSideHost();
        host.Start();

        Console.WriteLine(host.IsMain
            ? "this instance is the main one"
            : $"another instance holds the well-known name; serving only {PipeNames.WinSideInstance(host.ProcessId)}");

        var recovered = await host.RecoverAsync();
        if (recovered > 0)
            Console.WriteLine($"recovered {recovered} Revit instance(s) by enumeration");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("stopping");
            host.RequestStop();
        };

        Console.WriteLine("running - Ctrl+C to stop");
        await host.RunAsync();
        return 0;
    }

    private static async Task<int> RunClientAsync(string[] args)
    {
        var client = new WinSideChannel.WinSideChannelClient(PipeTransport.CreateClient(PipeNames.WinSide));

        switch (args[0])
        {
            case "--status":
                await StatusAsync(client);
                return 0;

            case "--launch" when args.Length >= 2:
                return await LaunchAsync(client, args[1], args.Length >= 3 ? args[2] : null);

            case "--close" when args.Length >= 2:
                var closed = await AskAsync(client, "close", ("instance", args[1]));
                Console.WriteLine(closed.Values["closed"] == "True" ? "closed" : "not closed");
                return closed.Values["closed"] == "True" ? 0 : 1;

            case "--recover":
                var recovered = await AskAsync(client, "recover");
                Console.WriteLine($"recovered {recovered.Values["recovered"]} instance(s)");
                return 0;

            case "--stop":
                await client.ShutdownAsync(new ShutdownRequest { Reason = "asked from the command line" });
                Console.WriteLine("asked the host to stop");
                return 0;

            default:
                PrintUsage();
                return 2;
        }
    }

    private static async Task StatusAsync(WinSideChannel.WinSideChannelClient client)
    {
        var ping = await AskAsync(client, "ping");
        Console.WriteLine($"host {ping.Values["instance"]}, pid {ping.Values["pid"]}, main {ping.Values["main"]}");

        var instances = await AskAsync(client, "instances");
        Console.WriteLine($"{instances.Values["count"]} Revit instance(s):");

        foreach (var pair in instances.Values.Where(one => one.Key.StartsWith("instance:", StringComparison.Ordinal))
                                             .OrderBy(one => one.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {pair.Key.Substring("instance:".Length)}  {pair.Value}");
        }
    }

    /// <summary>
    /// Asks the host to start a Revit, then watches for it to appear.
    /// </summary>
    /// <remarks>
    /// The host answers with a correlation token before Revit has finished starting, so the wait
    /// belongs here. Polling rather than a stream because this is a one-shot command; a window
    /// would watch the registry directly instead.
    /// </remarks>
    private static async Task<int> LaunchAsync(WinSideChannel.WinSideChannelClient client, string release, string? model)
    {
        var arguments = new List<(string, string)> { ("release", release) };

        if (!string.IsNullOrEmpty(model))
            arguments.Add(("model", model!));

        var accepted = await AskAsync(client, "launch", arguments.ToArray());
        var token = accepted.Values["token"];

        Console.WriteLine($"starting Revit {accepted.Values["release"]}, token {token}");

        var deadline = DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            var instances = await AskAsync(client, "instances");

            var found = instances.Values.FirstOrDefault(one =>
                one.Key.StartsWith("token:", StringComparison.Ordinal) && one.Value == token);

            if (!string.IsNullOrEmpty(found.Key))
            {
                var instanceId = found.Key.Substring("token:".Length);
                Console.WriteLine($"registered as {instanceId}");
                Console.WriteLine($"  {instances.Values["instance:" + instanceId]}");
                return 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Console.WriteLine("it did not register within five minutes - look at the host's output");
        return 1;
    }

    private static async Task<AskResponse> AskAsync(
        WinSideChannel.WinSideChannelClient client,
        string question,
        params (string Key, string Value)[] arguments)
    {
        var request = new AskRequest { Question = question };

        foreach (var (key, value) in arguments)
            request.Arguments.Add(key, value);

        return await client.AskAsync(request);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            BHS.WinSide - the companion process for Revit-side add-ins.

            With no arguments it runs the host: serves the channel, keeps the register of running
            Revit instances, and starts or closes them on request.

              --status                 what a running host knows
              --launch <year> [model]  ask it to start a Revit, and wait for it to register
              --close <instance>       ask one Revit to close itself
              --recover                rebuild the register by enumerating the pipe namespace
              --stop                   ask the host to stop
            """);
    }
}
