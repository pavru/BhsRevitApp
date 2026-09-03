using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BHS.Settings;
using BHS.Settings.Configuration;
using BHS.Revit.Launch;
using BHS.Transport;
using BHS.Transport.Protocol;
using GrpcDotNetNamedPipes;
using Microsoft.Extensions.Configuration;

namespace BHS.WinSide;

/// <summary>
/// The Win-side companion: it serves the channel, keeps the register of Revit processes, and
/// starts and stops them on request.
/// </summary>
/// <remarks>
/// Two names are served, always the instance one and, when this process wins the election, the
/// well-known one as well. Losing is ordinary and not fatal: a second instance is still reachable
/// by pid, which is what diagnostics and a second window want.
/// <para>
/// Nothing here depends on Revit-side being up first, and nothing on the other side depends on
/// this. Both read their own configuration from disk and start regardless; what travels over the
/// channel is only what one side alone can know.
/// </para>
/// </remarks>
public sealed class WinSideHost : IDisposable
{
    private readonly RevitInstanceRegistry _registry;
    private readonly ConcurrentDictionary<string, RevitSession> _sessions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<NamedPipeServer> _servers = new();

    private MainInstanceElection? _election;
    private Timer? _sweep;
    private bool _disposed;

    public WinSideHost()
    {
        ProcessId = Process.GetCurrentProcess().Id;
        InstanceId = "winside-" + ProcessId.ToString(CultureInfo.InvariantCulture);

        // Before anything else, and from disk alone. Nothing here waits for a Revit to exist, which
        // is the whole point of the file being the source of truth: the two sides start in either
        // order because neither asks the other for the settings it needs to start.
        Settings = LayeredSettings.Read(new SettingsOptions { Side = ProcessSide.WinSide });

        // The same object seen twice: read directly where a value is wanted, and through
        // IConfiguration where a section, an options monitor or the peer's own published values
        // are. Sharing the instance rather than reading the files twice is what keeps the two
        // views reloading together.
        Configuration = new ConfigurationBuilder().AddBhsSettings(Settings).Build();

        _registry = new RevitInstanceRegistry(
            Settings.Section("Registry").Duration("CheckTimeout", RevitInstanceRegistry.DefaultCheckTimeout));
    }

    /// <summary>The layered files, reloaded when one of them changes.</summary>
    public LayeredSettings Settings { get; }

    /// <summary>The same settings as configuration, for whatever wants a section or a change token.</summary>
    public IConfigurationRoot Configuration { get; }

    public int ProcessId { get; }

    public string InstanceId { get; }

    /// <summary>True when this process serves the well-known name.</summary>
    public bool IsMain => _election?.Won ?? false;

    public RevitInstanceRegistry Registry => _registry;

    /// <summary>Starts serving and returns. Await <see cref="RunAsync"/> to wait for the end.</summary>
    public void Start()
    {
        _election = MainInstanceElection.Hold();

        Serve(PipeNames.WinSideInstance(ProcessId));

        if (_election.Won)
            Serve(PipeNames.WinSide);

        _registry.Arrived += (_, e) => OnArrived(e.Instance);
        _registry.Departed += (_, e) => OnDeparted(e.Instance);

        // The fallback the registry documents: an instance whose process handle could not be
        // opened leaves only when somebody looks. Cheap - a dead process leaves no pipe name
        // behind, so the question is answered by the operating system rather than by a call.
        var interval = Settings.Section("Registry").Duration("SweepInterval", TimeSpan.FromMinutes(1));
        _sweep = new Timer(_ => _ = _registry.SweepAsync(), null, interval, interval);
    }

    /// <summary>
    /// Rebuilds what registration would have told us, for a host that has just started.
    /// </summary>
    /// <remarks>
    /// Ordinary rather than exceptional: Win-side can be restarted while people are working, and
    /// the Revit sessions already running have no reason to announce themselves twice. What cannot
    /// come back this way is the correlation token, so a recovered instance is never one this host
    /// can claim to have started.
    /// </remarks>
    public Task<int> RecoverAsync() => _registry.RecoverAsync();

    /// <summary>Waits until something asks the host to stop.</summary>
    public Task RunAsync() => _stopped.Task;

    public void RequestStop() => _stopped.TrySetResult(true);

    /// <summary>
    /// Starts a Revit and returns the token it will register under, without waiting for it.
    /// </summary>
    /// <remarks>
    /// The waiting happens on a thread of its own so that a caller - a user interface, or another
    /// process on the channel - is not held for the minute a cold start takes.
    /// </remarks>
    public Task<string> BeginLaunchAsync(RevitInstallation installation, RevitLaunchOptions options)
    {
        var launcher = new RevitLauncher(_registry);
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            RevitSession? session = null;

            try
            {
                session = await launcher.LaunchAsync(installation, options).ConfigureAwait(false);
                started.TrySetResult(session.CorrelationToken);

                if (session.Registered)
                {
                    _sessions[session.Instance!.InstanceId] = session;
                    Console.WriteLine($"  started Revit {installation.Release} as {session.Instance.InstanceId}"
                                      + $" after {session.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
                }
                else
                {
                    Console.WriteLine($"  Revit {installation.Release} did not register: {session.Outcome}");
                    session.Dispose();
                }
            }
            catch (Exception error)
            {
                started.TrySetException(error);
                session?.Dispose();
            }
        });

        return started.Task;
    }

    /// <summary>Asks one Revit to close, the way it is meant to be closed.</summary>
    public async Task<bool> CloseAsync(string instanceId)
    {
        if (_sessions.TryRemove(instanceId, out var session))
        {
            using (session)
                return await session.CloseAsync(LaunchOptions().ShutdownTimeout).ConfigureAwait(false);
        }

        // Not one of ours - recovered, or somebody's own Revit. It can still be asked, because the
        // channel is the same; what is missing is a process handle to wait on afterwards.
        if (!_registry.TryGet(instanceId, out var instance) || instance is null)
            return false;

        try
        {
            var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(instance.PipeName));
            await client.ShutdownAsync(new ShutdownRequest { Reason = "closed by Win-side" }).ConfigureAwait(false);
            return true;
        }
        catch (Grpc.Core.RpcException)
        {
            return false;
        }
    }

    /// <summary>What the settings currently say about starting Revit.</summary>
    /// <remarks>
    /// Read at each use rather than kept, so a file edited while the host runs takes effect on the
    /// next launch instead of the next restart. That is what the reload is for.
    /// </remarks>
    public RevitLaunchOptions LaunchOptions() => RevitLaunchOptions.ReadFrom(Settings);

    private void Serve(string pipeName)
    {
        var server = PipeTransport.CreateServer(pipeName);
        server.Error += (_, error) => Console.WriteLine($"  channel error on {pipeName}: {error.Error.Message}");
        WinSideChannel.BindService(server.ServiceBinder, new HostChannel(this, _registry, InstanceId));
        server.Start();

        _servers.Add(server);
        Console.WriteLine($"serving {pipeName}");
    }

    /// <summary>
    /// Says what turned up, and how.
    /// </summary>
    /// <remarks>
    /// One place for both routes in, because there are two and they mean different things: a
    /// registration is a Revit announcing itself, a recovery is this host finding one that was
    /// already running. Only the first can be ours.
    /// </remarks>
    private static void OnArrived(RevitInstance instance)
    {
        var how = instance.Origin == RevitInstanceOrigin.Recovered
            ? "found running"
            : instance.StartedByUs ? "ours" : "somebody else's";

        Console.WriteLine($"  + Revit {instance.Release} pid {instance.ProcessId} ({how})"
                          + (instance.Verified ? string.Empty : " UNVERIFIED"));
    }

    private void OnDeparted(RevitInstance instance)
    {
        Console.WriteLine($"  - Revit {instance.Release} pid {instance.ProcessId} has gone");

        if (_sessions.TryRemove(instance.InstanceId, out var session))
            session.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _sweep?.Dispose();

        foreach (var server in _servers)
        {
            server.Kill();
            server.Dispose();
        }

        foreach (var session in _sessions.Values)
            session.Dispose();

        _registry.Dispose();
        _election?.Dispose();
        Settings.Dispose();
    }
}
