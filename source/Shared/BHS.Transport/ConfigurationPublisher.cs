using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Transport;

/// <summary>
/// Holds what one side publishes about itself and hands every change to whoever is watching.
/// </summary>
/// <remarks>
/// The publishing side of <c>WatchConfiguration</c>. It deliberately knows nothing about
/// <c>Microsoft.Extensions.Configuration</c>: the publisher is Revit-side, and every assembly
/// loaded there is loaded into the same AppDomain as every other add-in on Revit 2024. The
/// consuming half, which does depend on that library, is a separate assembly nobody has to
/// reference to publish.
/// <para>
/// What travels is not the layered configuration both sides already read from disk - each side
/// reads that independently and starts without a channel, which is what removes any dependency on
/// startup order. What travels is what only this instance knows: its Revit release, its paths, the
/// document that happens to be open.
/// </para>
/// </remarks>
public sealed class ConfigurationPublisher
{
    private readonly object _gate = new();
    private readonly string _instanceId;

    private ConfigurationSnapshot _current;
    private TaskCompletionSource<ConfigurationSnapshot> _next = NewWaiter();

    public ConfigurationPublisher(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));

        _instanceId = instanceId;
        _current = new ConfigurationSnapshot
        {
            InstanceId = instanceId,
            Revision = 0,
            ContractVersion = Handshake.ContractVersion,
        };
    }

    /// <summary>What a caller asking right now would be told.</summary>
    public ConfigurationSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current.Clone();
        }
    }

    /// <summary>
    /// Replaces everything this instance publishes and wakes every watcher.
    /// </summary>
    /// <remarks>
    /// A whole snapshot rather than a delta, and a revision that only goes up. A consumer that
    /// misses one snapshot is not left with a torn view, and one that reconnects can tell at a
    /// glance whether it already knows this.
    /// </remarks>
    public void Publish(IReadOnlyDictionary<string, string> values)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        TaskCompletionSource<ConfigurationSnapshot> waiting;
        ConfigurationSnapshot published;

        lock (_gate)
        {
            published = new ConfigurationSnapshot
            {
                InstanceId = _instanceId,
                Revision = _current.Revision + 1,

                // On every snapshot, not only on the handshake: a consumer that found this
                // publisher by enumeration never saw a handshake.
                ContractVersion = Handshake.ContractVersion,
            };

            foreach (var pair in values)
                published.Values.Add(pair.Key, pair.Value);

            _current = published;

            waiting = _next;
            _next = NewWaiter();
        }

        // Outside the lock: a continuation that runs inline would otherwise run while holding it.
        waiting.TrySetResult(published);
    }

    /// <summary>
    /// Serves one <c>WatchConfiguration</c> call: the state of things now, and then every change
    /// until the caller goes away.
    /// </summary>
    /// <remarks>
    /// The first snapshot is sent unconditionally, so a consumer needs one call rather than a fetch
    /// followed by a subscription - and cannot miss a change that lands between the two.
    /// </remarks>
    public async Task WatchAsync(
        IServerStreamWriter<ConfigurationSnapshot> responseStream,
        CancellationToken cancellationToken)
    {
        if (responseStream is null)
            throw new ArgumentNullException(nameof(responseStream));

        ConfigurationSnapshot snapshot;
        Task<ConfigurationSnapshot> next;

        lock (_gate)
        {
            snapshot = _current.Clone();
            next = _next.Task;
        }

        await responseStream.WriteAsync(snapshot);

        while (!cancellationToken.IsCancellationRequested)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(next, cancelled.Task) == cancelled.Task)
                    return;
            }

            snapshot = await next;

            lock (_gate)
                next = _next.Task;

            await responseStream.WriteAsync(snapshot);
        }
    }

    // RunContinuationsAsynchronously so that completing a waiter cannot run a consumer's
    // continuation on the thread that called Publish.
    private static TaskCompletionSource<ConfigurationSnapshot> NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
