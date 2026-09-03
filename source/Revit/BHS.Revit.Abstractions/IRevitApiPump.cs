namespace BHS.Revit.Abstractions;

/// <summary>
/// The way onto Revit's API thread from anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Needed because measurement says so: a call arriving over the channel lands on a pool thread,
/// never on the thread Revit will accept API calls from. Anything touching a document has to cross
/// that line, and an external event is the only bridge Revit offers.
/// </para>
/// <para>
/// <b>No promise about when.</b> The queue is Revit's, and it is served when Revit is idle - which
/// on Revit 2024 was measured at twenty-five seconds for a request made during startup. Work posted
/// here is work that can wait; anything that cannot must not be posted here at all.
/// </para>
/// <para>
/// One pump per host rather than one per caller. Revit creates the underlying event from an API
/// context, and <c>OnStartup</c> is the only such context a host ever gets; several would also mean
/// several queues with no order between them, which is harder to reason about and buys nothing.
/// </para>
/// </remarks>
public interface IRevitApiPump
{
    /// <summary>Queues work for the API thread. Returns at once.</summary>
    /// <param name="name">What it is, for the log when it fails.</param>
    void Post(string name, Action<IRevitSession> work);

    /// <summary>Queues work and completes when it has run, or faulted.</summary>
    /// <remarks>
    /// For a caller that has an answer to return - a channel call being the reason this exists.
    /// Never await this on the API thread: that is a deadlock, and an obvious one only in hindsight.
    /// </remarks>
    Task<T> PostAsync<T>(string name, Func<IRevitSession, T> work, CancellationToken cancellationToken = default);
}
