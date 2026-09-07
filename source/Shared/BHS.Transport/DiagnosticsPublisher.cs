using BHS.Transport.Protocol;
using Grpc.Core;

namespace BHS.Transport;

/// <summary>
/// Carries what Revit is doing to whoever is watching, without ever making Revit wait for them.
/// </summary>
/// <remarks>
/// <para>
/// The publishing side of <c>WatchDiagnostics</c>, and the shape is deliberately different from
/// <see cref="ConfigurationPublisher"/> beside it. Configuration is a state: a watcher that misses
/// two snapshots and reads the third has missed nothing. Diagnostics are a history, and the parts
/// that matter most - a phase beginning, a dialog appearing - are exactly the ones that would be
/// lost by keeping only the latest.
/// </para>
/// <para>
/// <b>Nothing here may block the caller.</b> These events are raised on Revit's API thread, inside
/// Revit's own progress and dialog handling, so a publisher that waited on a slow reader would make
/// Revit itself wait on it. Publishing takes a lock, appends, and returns; a reader that cannot
/// keep up loses events rather than slowing anything down.
/// </para>
/// <para>
/// <b>And it says when it lost them.</b> The consumer is watching for silence - a phase that stops
/// producing events is the signal that something is stuck - so it has to be able to tell thinning
/// from stillness. Every event carries how many were dropped immediately before it, and the
/// sequence numbers leave a gap of exactly that size.
/// </para>
/// </remarks>
public sealed class DiagnosticsPublisher
{
    /// <summary>
    /// How many events are held for a reader that has fallen behind.
    /// </summary>
    /// <remarks>
    /// Small on purpose. The buffer is not a queue to be drained but a window: a consumer far
    /// enough behind to overflow it has already lost the thread of what is happening, and holding
    /// more would only delay telling it so. Progress events arrive in the hundreds during a model
    /// load - measured before this was written - and the publisher thins them at the source.
    /// </remarks>
    public const int Capacity = 256;

    private readonly object _gate = new();
    private readonly Queue<DiagnosticEvent> _pending = new();

    private long _sequence;
    private int _droppedSinceLastSent;
    private TaskCompletionSource<bool> _arrived = NewWaiter();

    /// <summary>Events discarded because no reader kept up. Reported, never hidden.</summary>
    public int Dropped { get; private set; }

    /// <summary>The number of the last event handed out, for a caller that wants to see movement.</summary>
    public long Sequence
    {
        get { lock (_gate) return _sequence; }
    }

    /// <summary>
    /// Records one event. Returns immediately, whatever the reader is doing.
    /// </summary>
    public void Publish(DiagnosticEvent evt)
    {
        if (evt is null)
            throw new ArgumentNullException(nameof(evt));

        TaskCompletionSource<bool> waiting;

        lock (_gate)
        {
            evt.Sequence = ++_sequence;
            evt.DroppedBefore = _droppedSinceLastSent;
            _droppedSinceLastSent = 0;

            if (evt.AtUnixMs == 0)
                evt.AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _pending.Enqueue(evt);

            // The oldest goes, not the newest. What a late reader needs is the recent past: the
            // event that says a dialog is up matters more than the one that said 40% five minutes
            // ago, and dropping the newest would lose the very transition being waited for.
            while (_pending.Count > Capacity)
            {
                _pending.Dequeue();
                Dropped++;
                _droppedSinceLastSent++;
            }

            waiting = _arrived;
            _arrived = NewWaiter();
        }

        // Outside the lock, so that a continuation running inline cannot run while it is held -
        // which on this path would mean running consumer code on Revit's API thread.
        waiting.TrySetResult(true);
    }

    /// <summary>
    /// Serves one <c>WatchDiagnostics</c> call: everything buffered, and then everything after.
    /// </summary>
    /// <remarks>
    /// The buffer is sent first rather than skipped, because a watcher usually attaches after
    /// Revit has already started doing something, and the first thing it needs is what it missed.
    /// </remarks>
    public async Task WatchAsync(
        IServerStreamWriter<DiagnosticEvent> responseStream,
        CancellationToken cancellationToken)
    {
        if (responseStream is null)
            throw new ArgumentNullException(nameof(responseStream));

        var delivered = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            List<DiagnosticEvent> batch;
            Task waiting;

            lock (_gate)
            {
                batch = _pending.Where(one => one.Sequence > delivered).ToList();
                waiting = _arrived.Task;
            }

            foreach (var evt in batch)
            {
                await responseStream.WriteAsync(evt);
                delivered = evt.Sequence;
            }

            if (batch.Count > 0)
                continue;

            var cancelled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(waiting, cancelled.Task) == cancelled.Task)
                    return;
            }
        }
    }

    private static TaskCompletionSource<bool> NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
