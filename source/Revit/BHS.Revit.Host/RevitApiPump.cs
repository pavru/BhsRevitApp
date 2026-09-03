using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// The one way onto Revit's API thread, and the queue behind it.
/// </summary>
/// <remarks>
/// <para>
/// An external event is the only bridge Revit offers, and it can only be created from an API
/// context - which a host gets exactly once, in <c>OnStartup</c>. Hence one pump per host rather
/// than one per caller: several would mean several queues with no order between them, bought
/// nothing, and each would need a context nobody has.
/// </para>
/// <para>
/// <b>It promises to run the work, not to run it soon.</b> Revit serves the queue when it is idle,
/// and idle is its definition, not ours: a request made during startup on Revit 2024 waited
/// twenty-five seconds. Everything here is written around that being normal.
/// </para>
/// </remarks>
internal sealed class RevitApiPump : IExternalEventHandler, IRevitApiPump, IDisposable
{
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ILog _log = Log.For<RevitApiPump>();

    private ExternalEvent? _event;
    private volatile bool _closed;

    /// <summary>Attaches the pump to an external event. Called from the API context that created it.</summary>
    public void Attach(ExternalEvent externalEvent) => _event = externalEvent;

    public void Post(string name, Action<IRevitSession> work)
    {
        if (work is null)
            throw new ArgumentNullException(nameof(work));

        Enqueue(new WorkItem(name, session =>
        {
            work(session);
            return null;
        }, null));
    }

    public Task<T> PostAsync<T>(string name, Func<IRevitSession, T> work, CancellationToken cancellationToken = default)
    {
        if (work is null)
            throw new ArgumentNullException(nameof(work));

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => completion.TrySetCanceled());

        Enqueue(new WorkItem(name, session => work(session), completion));

        return Cast(completion.Task);

        static async Task<T> Cast(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;
    }

    private void Enqueue(WorkItem item)
    {
        if (_closed)
        {
            // Revit is going away. Failing the caller is honest; queueing would be a promise
            // nothing will keep.
            item.Completion?.TrySetException(new InvalidOperationException("The Revit host is shutting down."));
            return;
        }

        _queue.Enqueue(item);

        try
        {
            _event?.Raise();
        }
        catch (Exception error)
        {
            _log.Warn(error, "could not raise the external event for {0}", item.Name);
        }
    }

    /// <summary>
    /// Runs whatever is queued. Revit calls this, on the API thread.
    /// </summary>
    /// <remarks>
    /// The whole queue rather than one item: a raise can coalesce with another, so the count of
    /// calls here is not the count of things posted, and anything left behind would wait for the
    /// next unrelated raise.
    /// </remarks>
    public void Execute(UIApplication application)
    {
        var session = new RevitSession(application);

        while (_queue.TryDequeue(out var item))
        {
            try
            {
                var result = item.Work(session);
                item.Completion?.TrySetResult(result);
            }
            catch (Exception error)
            {
                // Never rethrown. This is Revit's thread and an escaping exception here is a
                // dialog at best; the caller who is waiting gets the failure, and the caller who
                // is not gets a line in the log.
                if (item.Completion is null)
                    _log.Error(error, "queued work {0} failed", item.Name);

                item.Completion?.TrySetException(error);
            }
        }
    }

    public string GetName() => "BHS Revit API pump";

    /// <summary>Stops accepting work. The queue is deliberately not drained.</summary>
    /// <remarks>
    /// Revit is already leaving; running what was deferred would hold it there, and what was
    /// deferred was by definition something that could wait.
    /// </remarks>
    public void Dispose()
    {
        _closed = true;

        while (_queue.TryDequeue(out var item))
            item.Completion?.TrySetException(new InvalidOperationException("The Revit host is shutting down."));
    }

    private sealed class WorkItem
    {
        public WorkItem(string name, Func<IRevitSession, object?> work, TaskCompletionSource<object?>? completion)
        {
            Name = name;
            Work = work;
            Completion = completion;
        }

        public string Name { get; }

        public Func<IRevitSession, object?> Work { get; }

        public TaskCompletionSource<object?>? Completion { get; }
    }

    private sealed class RevitSession : IRevitSession
    {
        public RevitSession(UIApplication application) => Application = application;

        public UIApplication Application { get; }
    }
}
