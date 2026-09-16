using System.Globalization;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BHS.MEP.Cabling.Declaration;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>One failure Revit processed while a watch was open, copied out of its accessor.</summary>
/// <remarks>
/// Plain values rather than the accessor, and that is not tidiness: a <c>FailuresAccessor</c> stops
/// working the moment the handler returns, and <c>DeleteAllWarnings</c> invalidates every message
/// accessor before that. Anything a case wants to assert about has to be copied out first.
/// </remarks>
internal sealed class ProcessedFailure
{
    private static readonly Guid[] Cabling =
    {
        CablingFeature.NoCarrierNear.Guid,
        CablingFeature.NoConnectivity.Guid,
        CablingFeature.ConnectionUnreadable.Guid,
        CablingFeature.JunctionBoxJoinedToNothing.Guid,
        CablingFeature.IndicatorJoinedIntoNetwork.Guid,
    };

    public ProcessedFailure(string transaction, Guid definition, FailureSeverity severity, IReadOnlyList<long> elements)
    {
        Transaction = transaction;
        Definition = definition;
        Severity = severity;
        Elements = elements;
    }

    /// <summary>The transaction whose commit or rollback raised it.</summary>
    public string Transaction { get; }

    public Guid Definition { get; }

    public FailureSeverity Severity { get; }

    /// <summary>The failing elements, by id.</summary>
    public IReadOnlyList<long> Elements { get; }

    /// <summary>Whether it is one of the five the cabling feature declares.</summary>
    public bool IsCabling => Array.IndexOf(Cabling, Definition) >= 0;

    public bool Is(FailureDefinitionId id) => Definition == id.Guid;

    /// <summary>
    /// Definition, severity and elements - never the text, which is Revit's, localised, and would put
    /// words from the owner's model into a public record.
    /// </summary>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1} [{2}]",
        Definition,
        Severity,
        string.Join(", ", Elements.Select(one => one.ToString(CultureInfo.InvariantCulture))));
}

/// <summary>
/// Sees the failures Revit processes while it is open, and dismisses every warning so that nothing is
/// left for an unattended sweep to put on the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a case needs this at all.</b> The apply phase posts its five conditions with
/// <c>Document.PostFailure</c> inside its own transaction, and gives a caller no hook on that
/// transaction's failure handling. So the one place a test can see what was posted - and the one
/// place it can stop Revit from showing it - is the application-wide
/// <c>Application.FailuresProcessing</c> event. The shape is Autodesk's own, from the ErrorHandling
/// sample of all four SDKs: subscribe from an API context, match on the definition id, answer
/// <c>Continue</c> - which the reference says "is sufficient" when a handler dismisses warnings.
/// </para>
/// <para>
/// <b>What is not measured, and it is most of what this rests on.</b> That the event fires, and fires
/// before any user interface, for a commit made inside the probe's external event. What a warning left
/// in place would do there - a modal dialog, the mini-warning, or nothing. The order in which several
/// add-ins' handlers run: another vendor's handler that deletes warnings first leaves this one seeing
/// fewer, which is why the cases compare what was seen with what the apply reports posting before
/// they compare anything else.
/// </para>
/// <para>
/// <b>A fault in the handler rolls back, never continues.</b> <c>Continue</c> with an error still in
/// the accessor hands that error to Revit's own dialog, inside the one pump post that runs every
/// declared case, and the sweep's question for the tests has no deadline - it would wait for a person.
/// A handler that cannot vouch for what it saw asks for the rollback, and the case then fails with the
/// exception's text instead of the sweep standing still.
/// </para>
/// <para>
/// <b>It also rolls back on request, which is the only way a case can make Revit refuse a commit.</b>
/// The owner's decision of 2026-09-14 is that an apply whose transaction does not come back
/// <c>Committed</c> reports nothing as applied - and a rule about Revit declining a commit is proven
/// only by Revit declining one. The apply takes no failure handling options from its caller, so the
/// handler here is the one hook left: while a <see cref="RollBackRequest"/> is held, every failure
/// processing of the transaction it names is answered with <c>ProceedWithRollBack</c> and
/// clear-after-rollback, the way a fault already is - with one difference: when nothing worse than a
/// warning is there, the warnings are left in the accessor rather than deleted, so the rollback has a
/// failure to act on. By name, because the request is about one transaction of the production path
/// and nothing else a case does in between. Whether the event
/// fires at all for a commit with nothing to process is not measured - the reference speaks of "the
/// errors and/or warnings that caused the event to trigger" - so a case that asks for a rollback gives
/// that transaction a failure of its own. Not measured either: that <c>Commit</c> then returns
/// <c>RolledBack</c> rather than something else, and that Revit shows nothing - the reference names
/// clear-after-rollback as the way "to dismiss the errors and silently cancel the transaction", and
/// the case notes what came back before it asserts anything about it.
/// </para>
/// <para>
/// <b>The delegate is held, not rebuilt.</b> <c>-= OnFailures</c> would construct a second delegate
/// and rely on Revit's remove accessor matching it by equality, which is not measured; one kept
/// instance is removed by the same reference it was added by. And the watch counts itself: a watch
/// that some earlier case failed to dispose would keep deleting warnings and rolling back errors for
/// the rest of the session, and the next watch refuses to start rather than share its view with it.
/// </para>
/// </remarks>
internal sealed class PostedWarnings : IDisposable
{
    private static int _open;

    private readonly Application _application;
    private readonly EventHandler<FailuresProcessingEventArgs> _handler;
    private readonly List<ProcessedFailure> _seen = new();
    private RollBackRequest? _rollBack;
    private bool _watching;

    private PostedWarnings(Application application)
    {
        _application = application;
        _handler = OnFailures;
        _application.FailuresProcessing += _handler;
        _watching = true;
        _open++;
    }

    /// <summary>Starts watching. Always inside a <c>using</c>, so the subscription ends in a finally.</summary>
    public static PostedWarnings Watch(Application application)
    {
        Expect.Same(
            0,
            _open,
            "failure watches still subscribed when this case began, so what it sees would be shared with a case that should have finished");

        return new PostedWarnings(application ?? throw new ArgumentNullException(nameof(application)));
    }

    /// <summary>How many failures have been seen so far, to take a slice from later.</summary>
    public int Mark => _seen.Count;

    /// <summary>What was seen after a mark.</summary>
    public IReadOnlyList<ProcessedFailure> Since(int mark) =>
        _seen.Skip(Math.Max(0, mark)).ToList();

    /// <summary>The first exception the handler caught, or nothing.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>
    /// Rolls back every failure processing of the named transaction until the request is disposed.
    /// </summary>
    /// <remarks>
    /// One request at a time: two would share the answers the handler gives, and a count that belongs
    /// to neither says nothing about either. Always inside a <c>using</c>, so a case that throws while
    /// it holds one does not leave the next transaction of that name to be rolled back as well.
    /// </remarks>
    /// <param name="transaction">The transaction's name, exactly as the code that opens it spells it.</param>
    public RollBackRequest RollingBack(string transaction)
    {
        Expect.That(_watching, "a rollback was requested of a failure watch that had already stopped watching");
        Expect.That(_rollBack is null, "a rollback was requested while another request of the same watch was still held");

        return _rollBack = new RollBackRequest(this, transaction ?? throw new ArgumentNullException(nameof(transaction)));
    }

    public void Dispose()
    {
        if (!_watching)
            return;

        _watching = false;
        _rollBack = null;
        _open--;
        _application.FailuresProcessing -= _handler;
    }

    internal void Release(RollBackRequest request)
    {
        if (ReferenceEquals(_rollBack, request))
            _rollBack = null;
    }

    private void OnFailures(object? sender, FailuresProcessingEventArgs arguments)
    {
        FailuresAccessor? accessor = null;

        try
        {
            accessor = arguments.GetFailuresAccessor();

            // No filter by document, deliberately. The watch brackets synchronous calls a case makes
            // on the one document it was given; a filter would only add a second, unmeasured way to
            // miss a warning - and to leave it for Revit's own dialog.
            var transaction = accessor.GetTransactionName() ?? string.Empty;
            var warnings = 0;
            var worse = 0;

            foreach (var message in accessor.GetFailureMessages())
            {
                var severity = message.GetSeverity();
                var elements = new List<long>();

                foreach (var id in message.GetFailingElementIds())
                    elements.Add(id.Value);

                _seen.Add(new ProcessedFailure(transaction, message.GetFailureDefinitionId().Guid, severity, elements));

                if (severity == FailureSeverity.Warning)
                    warnings++;
                else
                    worse++;
            }

            // Asked for by name, and answered the way a fault is: see the remarks. Counted apart from
            // the failures themselves, so a case can tell "rolled back because it asked" from "never
            // saw the transaction it asked about".
            var request = _rollBack;
            var requested = request is not null && string.Equals(transaction, request.Transaction, StringComparison.Ordinal);

            if (requested)
                request!.Answered++;

            // Copied above, deleted here: this call invalidates the message accessors. Not for a
            // requested rollback with nothing worse than a warning in it: those warnings are the only
            // failures left, and Revit's reference ties rollback to failures that remain - "if some
            // failures are still present and Continue is returned, it will be treated as
            // ProceedWithRollback". Whether it still honours the rollback with nothing left is not
            // measured, so they stay for it to act on, and clear-after-rollback dismisses them silently.
            if (warnings > 0 && !(requested && worse == 0))
                accessor.DeleteAllWarnings();

            if (worse > 0 || requested)
            {
                RollBack(accessor, arguments);
                return;
            }

            // When several handlers answer, the most prohibitive result wins, so this never overrides
            // a stricter one.
            arguments.SetProcessingResult(FailureProcessingResult.Continue);
        }
        catch (Exception error)
        {
            Fault ??= error;

            try
            {
                accessor?.DeleteAllWarnings();
            }
            catch (Exception)
            {
                // Already failing; the first fault is the one reported.
            }

            // See the remarks: a handler that faulted asks for the rollback, whatever it had seen.
            RollBack(accessor, arguments);
        }
    }

    private static void RollBack(FailuresAccessor? accessor, FailuresProcessingEventArgs arguments)
    {
        try
        {
            if (accessor is not null)
            {
                // Cleared so the rolled-back failures are not shown afterwards either.
                var options = accessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                accessor.SetFailureHandlingOptions(options);
            }
        }
        catch (Exception)
        {
            // The result below is what matters; the options are a courtesy on top of it.
        }

        try
        {
            arguments.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
        }
        catch (Exception)
        {
            // Nothing further this handler can do from inside Revit's own callback.
        }
    }
}

/// <summary>A failure watch asked to roll one transaction back, for as long as this is held.</summary>
internal sealed class RollBackRequest : IDisposable
{
    private readonly PostedWarnings _watch;

    internal RollBackRequest(PostedWarnings watch, string transaction)
    {
        _watch = watch;
        Transaction = transaction;
    }

    /// <summary>The name of the transaction to roll back.</summary>
    public string Transaction { get; }

    /// <summary>How many times the watch answered a failure processing of it with a rollback.</summary>
    /// <remarks>Still readable after the request is disposed, which is when a case asks.</remarks>
    public int Answered { get; internal set; }

    public void Dispose() => _watch.Release(this);
}
