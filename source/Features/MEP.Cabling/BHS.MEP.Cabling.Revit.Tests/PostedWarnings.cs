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

    /// <summary>Whether it is one of the four the cabling feature declares.</summary>
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
/// <b>Why a case needs this at all.</b> The apply phase posts its four conditions with
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

    public void Dispose()
    {
        if (!_watching)
            return;

        _watching = false;
        _open--;
        _application.FailuresProcessing -= _handler;
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

            // Copied above, deleted here: this call invalidates the message accessors.
            if (warnings > 0)
                accessor.DeleteAllWarnings();

            if (worse > 0)
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
