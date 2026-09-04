using System.Diagnostics;
using BHS.Transport;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Revit.Launch;

/// <summary>What became of a Revit this side tried to start.</summary>
public enum RevitLaunchOutcome
{
    /// <summary>It started and announced itself. Only here is <see cref="RevitSession.Instance"/> set.</summary>
    Registered,

    /// <summary>The process could not be started at all.</summary>
    NotStarted,

    /// <summary>It started and then died before announcing itself.</summary>
    ExitedBeforeRegistering,

    /// <summary>It was still alive at the deadline and had said nothing.</summary>
    /// <remarks>
    /// In practice this means a modal dialog with nobody in front of it. Revit raises one for an
    /// add-in it has not been told to trust, before any add-in gets its startup call, and from out
    /// here that is indistinguishable from a Revit that is merely slow. <see cref="AddInTrust"/>
    /// answers the question before the launch instead.
    /// </remarks>
    TimedOut,
}

/// <summary>
/// One Revit this side started, for as long as it is ours to talk to.
/// </summary>
/// <remarks>
/// Disposing it withdraws the expectation from the registry and lets go of the process handle. It
/// deliberately does not close Revit: a session may outlive the code that opened it, and killing
/// somebody's Revit on a stray <c>using</c> would be a poor trade.
/// </remarks>
public sealed class RevitSession : IDisposable
{
    private readonly RevitInstanceRegistry _registry;
    private bool _disposed;

    internal RevitSession(
        RevitLaunchOutcome outcome,
        RevitInstallation installation,
        RevitInstanceRegistry registry,
        string correlationToken,
        Process? process,
        RevitInstance? instance,
        TimeSpan elapsed)
    {
        Outcome = outcome;
        Installation = installation;
        _registry = registry;
        CorrelationToken = correlationToken;
        Process = process;
        Instance = instance;
        Elapsed = elapsed;
    }

    public RevitLaunchOutcome Outcome { get; }

    public RevitInstallation Installation { get; }

    /// <summary>The token this Revit was started with, and registered under.</summary>
    public string CorrelationToken { get; }

    /// <summary>The started process, or null when it could not be started.</summary>
    /// <remarks>
    /// Present even when registration failed: the process id is what a caller needs to look for a
    /// log, read an exit code, or kill what is left.
    /// </remarks>
    public Process? Process { get; }

    /// <summary>The registered instance, or null unless <see cref="Outcome"/> is registered.</summary>
    public RevitInstance? Instance { get; }

    /// <summary>How long registration took, measured from the moment the process was started.</summary>
    public TimeSpan Elapsed { get; }

    public bool Registered => Outcome == RevitLaunchOutcome.Registered && Instance is not null;

    /// <summary>True while the process is still there.</summary>
    public bool IsRunning
    {
        get
        {
            try
            {
                return Process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Asks Revit to close itself, and waits for it to finish.
    /// </summary>
    /// <remarks>
    /// From the inside, through the channel, because from the outside it does not work:
    /// <c>CloseMainWindow</c> was honoured once in four attempts and ignored otherwise. The add-in
    /// posts <c>PostableCommand.ExitRevit</c> through an external event, which is the supported
    /// route and the only one that runs on the API thread.
    /// <para>
    /// Returns false if the deadline passes. That is not the same as failing to close - the exit
    /// may simply still be in progress - so <see cref="Kill"/> is a separate decision.
    /// </para>
    /// </remarks>
    public async Task<bool> CloseAsync(TimeSpan timeout, string reason = "closed by Win-side")
    {
        var process = Process;
        var instance = Instance;

        if (process is null)
            return true;

        if (instance is null)
            return await WaitAsync(process, timeout).ConfigureAwait(false);

        // Asked more than once, and that is not belt and braces.
        //
        // The exit travels as PostCommand(ExitRevit) from inside, and Revit drops a posted command
        // it is not ready for - silently, with no error anywhere. Measured three times: each failure
        // came within a second or two of heavy document work, and each time the add-in's own
        // OnShutdown never ran at all, so Revit had not begun leaving. The first reading was
        // mistaken for a slow shutdown and answered with a bigger budget; the second landed on the
        // release that normally leaves in three seconds, which is what a lost message looks like
        // and not what a slow one does.
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;

            try
            {
                var client = new RevitSideChannel.RevitSideChannelClient(PipeTransport.CreateClient(instance.PipeName));
                await client.ShutdownAsync(new ShutdownRequest { Reason = reason }).ConfigureAwait(false);
            }
            catch (RpcException)
            {
                // Gone already, or too busy to answer. Either way the wait below decides.
            }

            var left = deadline - DateTime.UtcNow;
            var slice = left < RetryAfter ? left : RetryAfter;

            if (slice <= TimeSpan.Zero)
                break;

            if (await WaitAsync(process, slice).ConfigureAwait(false))
                return true;

            // Still there. Either it is leaving slowly, in which case asking again costs nothing,
            // or the request was dropped, in which case asking again is the only thing that helps.
        }

        return process.HasExited;
    }

    /// <summary>How long to give one request before asking again.</summary>
    /// <remarks>
    /// Long enough that an ordinary shutdown finishes inside the first slice - measured at 3 to 40
    /// seconds across the releases - and short enough that a dropped request is not paid for in
    /// minutes.
    /// </remarks>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(45);

    private static async Task<bool> WaitAsync(System.Diagnostics.Process process, TimeSpan window)
    {
        using var cancellation = new CancellationTokenSource(window);

        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Kills the process and everything it started.
    /// </summary>
    /// <remarks>
    /// The backstop, never the plan: a killed Revit leaves its journal unfinished and can leave a
    /// licence seat held. Worth doing anyway rather than leaving one behind.
    /// </remarks>
    public bool Kill()
    {
        try
        {
            if (Process is null || Process.HasExited)
                return false;

            Process.Kill(entireProcessTree: true);
            Process.WaitForExit(10_000);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _registry.StopExpecting(CorrelationToken);
        Process?.Dispose();
    }
}
