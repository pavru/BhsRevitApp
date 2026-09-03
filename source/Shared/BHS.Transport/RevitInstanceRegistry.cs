using System.Collections.Concurrent;
using System.Diagnostics;
using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Transport;

/// <summary>Carries an instance into an <see cref="EventHandler{TEventArgs}"/>.</summary>
public sealed class RevitInstanceEventArgs : EventArgs
{
    internal RevitInstanceEventArgs(RevitInstance instance) => Instance = instance;

    public RevitInstance Instance { get; }
}

/// <summary>
/// The live register of Revit processes Win-side can talk to.
/// </summary>
/// <remarks>
/// Registration is the supported way to be found: a Revit process connects to the well-known name
/// and says where to call it back. Everything else here exists for the cases registration cannot
/// cover - <see cref="RecoverAsync"/> for a Win-side that restarted and lost what it knew, and
/// <see cref="SweepAsync"/> for an instance whose death went unnoticed.
/// <para>
/// Departure is normally noticed at once rather than swept for: a recorded instance names a
/// process, and a process can be watched. The sweep is the fallback for the instance whose handle
/// could not be opened, which <see cref="Watched"/> makes visible.
/// </para>
/// <para>
/// Keyed by instance id, looked up by correlation token. Not by process id: that would work - the
/// process that reports for duty is measurably the one that was started - but a token is what
/// tells one of several running Revits from another, and what distinguishes a Revit Win-side
/// started from one a person opened while it was running.
/// </para>
/// </remarks>
public sealed class RevitInstanceRegistry : IDisposable
{
    /// <summary>
    /// How long a check of the other end may take. Short because it happens inside a call the peer
    /// is waiting on, and the library gives that call ten seconds in total.
    /// </summary>
    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _instances = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<RevitInstance>> _expected =
        new(StringComparer.Ordinal);

    private readonly TimeSpan _checkTimeout;
    private bool _disposed;

    public RevitInstanceRegistry(TimeSpan? checkTimeout = null) =>
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;

    /// <summary>Raised once per instance, after it has been recorded.</summary>
    public event EventHandler<RevitInstanceEventArgs>? Arrived;

    /// <summary>Raised once per instance, after it has left the registry.</summary>
    /// <remarks>Raised on whichever thread noticed - a process exit callback, or a sweep.</remarks>
    public event EventHandler<RevitInstanceEventArgs>? Departed;

    /// <summary>Everything currently known to be alive, oldest first within a release.</summary>
    public IReadOnlyList<RevitInstance> Instances
    {
        get
        {
            lock (_gate)
            {
                var all = new List<RevitInstance>(_instances.Count);
                foreach (var entry in _instances.Values)
                    all.Add(entry.Instance);

                all.Sort((left, right) =>
                {
                    var byRelease = left.Release.CompareTo(right.Release);
                    return byRelease != 0 ? byRelease : left.RegisteredAt.CompareTo(right.RegisteredAt);
                });

                return all;
            }
        }
    }

    /// <summary>Instances whose process is being watched for exit.</summary>
    /// <remarks>
    /// Anything outside this set only leaves the registry when <see cref="SweepAsync"/> runs, so a
    /// host that never sweeps should be looking at the difference.
    /// </remarks>
    public IReadOnlyList<RevitInstance> Watched
    {
        get
        {
            lock (_gate)
            {
                var watched = new List<RevitInstance>();

                foreach (var entry in _instances.Values)
                {
                    if (entry.Process is not null)
                        watched.Add(entry.Instance);
                }

                return watched;
            }
        }
    }

    public bool TryGet(string instanceId, out RevitInstance? instance)
    {
        instance = null;

        if (string.IsNullOrEmpty(instanceId))
            return false;

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
                return false;

            instance = entry.Instance;
            return true;
        }
    }

    /// <summary>The instance carrying a token this side issued, or null when it has not arrived.</summary>
    public RevitInstance? ByCorrelationToken(string correlationToken)
    {
        if (string.IsNullOrEmpty(correlationToken))
            return null;

        lock (_gate)
        {
            foreach (var entry in _instances.Values)
            {
                if (string.Equals(entry.Instance.CorrelationToken, correlationToken, StringComparison.Ordinal))
                    return entry.Instance;
            }
        }

        return null;
    }

    /// <summary>
    /// Declares that a token has been issued, and hands back what completes when it arrives.
    /// </summary>
    /// <remarks>
    /// Called before the process it belongs to is started, so that a Revit which registers
    /// unusually fast cannot arrive before anybody is listening for it.
    /// <para>
    /// Deliberately without a timeout. The wait belongs to the caller, which holds the process
    /// handle and can tell "still starting" from "died on startup" - a distinction a clock cannot
    /// make, and one worth a minute of somebody's time on every cold start.
    /// </para>
    /// </remarks>
    public Task<RevitInstance> Expect(string correlationToken)
    {
        if (string.IsNullOrEmpty(correlationToken))
            throw new ArgumentException("A correlation token is required.", nameof(correlationToken));

        var waiter = new TaskCompletionSource<RevitInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        _expected[correlationToken] = waiter;

        // It may already be here: nothing stops a caller from expecting a token it has seen.
        var arrived = ByCorrelationToken(correlationToken);
        if (arrived is not null)
            waiter.TrySetResult(arrived);

        return waiter.Task;
    }

    /// <summary>Withdraws an expectation. Registrations for that token are still recorded.</summary>
    public void StopExpecting(string correlationToken)
    {
        if (!string.IsNullOrEmpty(correlationToken))
            _expected.TryRemove(correlationToken, out _);
    }

    /// <summary>
    /// Records a registration and checks, against the operating system, that the caller is where
    /// it says it is.
    /// </summary>
    /// <remarks>
    /// The check costs a short connection to the pipe the caller named, made while the caller
    /// waits on its own call. That is safe in the direction it is done: the registering side calls
    /// from a thread of its own, so its listener pool - four threads, a hard constant in the
    /// library - is not what gets blocked.
    /// <para>
    /// A caller that fails the check is still recorded, marked unverified. Refusing outright would
    /// turn a peer that was momentarily busy into a peer that does not exist, and the account is
    /// already narrowed to ours by the pipe descriptor; what the check adds is evidence about an
    /// impostor running as the same user, which is a thing to report rather than to hide.
    /// </para>
    /// </remarks>
    public RevitInstance Record(RegisterRequest request, string? account)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var verified = PeerIdentity.TryGetServerProcess(request.PipeName, _checkTimeout, out var servingPid, out _)
                       && servingPid == request.ProcessId;

        var instance = new RevitInstance(
            request.InstanceId,
            string.IsNullOrEmpty(request.CorrelationToken) ? null : request.CorrelationToken,
            request.PipeName,
            request.RevitVersion,
            request.ProcessId,
            account,
            verified,
            RevitInstanceOrigin.Registered);

        return Add(instance);
    }

    /// <summary>Drops an instance, whether or not it is still alive.</summary>
    public bool Forget(string instanceId)
    {
        RevitInstance? removed = null;

        lock (_gate)
        {
            if (_instances.TryGetValue(instanceId, out var entry))
            {
                _instances.Remove(instanceId);
                entry.StopWatching();
                removed = entry.Instance;
            }
        }

        if (removed is null)
            return false;

        Departed?.Invoke(this, new RevitInstanceEventArgs(removed));
        return true;
    }

    /// <summary>
    /// Drops every instance that no longer answers, and returns how many were dropped.
    /// </summary>
    /// <remarks>
    /// The fallback for a death that went unnoticed. Cheap: a pipe disappears when its last handle
    /// closes, so a process that died leaves no name behind, and the question is answered by the
    /// operating system rather than by a call.
    /// </remarks>
    public Task<int> SweepAsync()
    {
        var dropped = 0;

        foreach (var instance in Instances)
        {
            if (PeerIdentity.TryGetServerProcess(instance.PipeName, _checkTimeout, out var pid, out _)
                && pid == instance.ProcessId)
            {
                continue;
            }

            if (Forget(instance.InstanceId))
                dropped++;
        }

        return Task.FromResult(dropped);
    }

    /// <summary>
    /// Rebuilds what registration would have told us, by enumerating the pipe namespace, and
    /// returns how many instances were added.
    /// </summary>
    /// <remarks>
    /// For a Win-side that restarted, and for diagnostics. Not the ordinary path, and not a
    /// complete substitute for one: a name only says a server is there, so each candidate is
    /// confirmed by a short call with a deadline, and only what answers is recorded.
    /// <para>
    /// What cannot be recovered is the correlation token, which lives in the registration message
    /// and in the started process's environment. A recovered instance is therefore never one this
    /// side can claim to have started, even when it did.
    /// </para>
    /// </remarks>
    public async Task<int> RecoverAsync()
    {
        var added = 0;

        foreach (var pipeName in PipeNames.Enumerate())
        {
            if (!PipeNames.TryParseRevitSide(pipeName, out var processId, out var release))
                continue;

            if (KnownPipe(pipeName))
                continue;

            // Layer two first, because it costs no call and answers "is anyone there" at the same
            // time as "who".
            if (!PeerIdentity.TryGetServerProcess(pipeName, _checkTimeout, out var servingPid, out _)
                || servingPid != processId)
            {
                continue;
            }

            var identified = await IdentifyAsync(pipeName).ConfigureAwait(false);
            if (identified is null)
                continue;

            Add(new RevitInstance(
                identified,
                correlationToken: null,
                pipeName,
                release,
                processId,
                account: null,
                verified: true,
                RevitInstanceOrigin.Recovered));

            added++;
        }

        return added;
    }

    public void Dispose()
    {
        List<Entry> entries;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            entries = new List<Entry>(_instances.Values);
            _instances.Clear();
        }

        foreach (var entry in entries)
            entry.StopWatching();
    }

    /// <summary>
    /// Asks a candidate who it is, and refuses one that will not name the contract it speaks.
    /// </summary>
    /// <remarks>
    /// The third layer of trust, reached differently than on the registration path: there is no
    /// handshake message to inspect here, so the snapshot every Revit-side has to serve carries the
    /// version instead. Refusing a peer that names none is the stance
    /// <see cref="Handshake.EnsureCompatible"/> already takes on a caller that names none.
    /// </remarks>
    private async Task<string?> IdentifyAsync(string pipeName)
    {
        try
        {
            var client = new RevitSideChannel.RevitSideChannelClient(
                PipeTransport.CreateClient(pipeName, _checkTimeout));

            var snapshot = await client
                .GetConfigurationAsync(new ConfigurationRequest(), deadline: DateTime.UtcNow + _checkTimeout)
                .ConfigureAwait(false);

            Handshake.EnsureCompatible(snapshot.ContractVersion);

            return string.IsNullOrEmpty(snapshot.InstanceId) ? null : snapshot.InstanceId;
        }
        catch (RpcException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private bool KnownPipe(string pipeName)
    {
        lock (_gate)
        {
            foreach (var entry in _instances.Values)
            {
                if (string.Equals(entry.Instance.PipeName, pipeName, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private RevitInstance Add(RevitInstance instance)
    {
        Entry? replaced = null;

        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RevitInstanceRegistry));

            if (_instances.TryGetValue(instance.InstanceId, out var existing))
            {
                // A repeat. Registration is retried by design - a companion that was starting at
                // the same moment should not be missed for it - so a second one replaces the first
                // rather than arriving as a stranger.
                replaced = existing;
                _instances.Remove(instance.InstanceId);
            }

            var entry = new Entry(instance);
            _instances[instance.InstanceId] = entry;
            entry.WatchForExit(() => Forget(instance.InstanceId));
        }

        replaced?.StopWatching();

        if (replaced is null)
            Arrived?.Invoke(this, new RevitInstanceEventArgs(instance));

        if (instance.CorrelationToken is { Length: > 0 } token
            && _expected.TryGetValue(token, out var waiter))
        {
            waiter.TrySetResult(instance);
        }

        return instance;
    }

    /// <summary>One instance, and the handle that says when it goes away.</summary>
    private sealed class Entry
    {
        private EventHandler? _exited;

        public Entry(RevitInstance instance) => Instance = instance;

        public RevitInstance Instance { get; }

        public Process? Process { get; private set; }

        public void WatchForExit(Action onExit)
        {
            Process process;

            try
            {
                process = Process.GetProcessById(Instance.ProcessId);
                process.EnableRaisingEvents = true;
            }
            catch (ArgumentException)
            {
                // Already gone, or never was. A sweep will settle it.
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Another account's process, most likely. Nothing here to watch with.
                return;
            }

            _exited = (_, _) => onExit();
            process.Exited += _exited;
            Process = process;

            // Subscribing after the fact is subscribing too late, so ask once more.
            if (process.HasExited)
                onExit();
        }

        public void StopWatching()
        {
            var process = Process;
            if (process is null)
                return;

            Process = null;

            if (_exited is not null)
                process.Exited -= _exited;

            process.Dispose();
        }
    }
}
