using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB.Events;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Host;

/// <summary>
/// Watches what Revit is doing and hands it on, one phase at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Our code is loaded in <c>OnStartup</c>, well before a model - measured
/// at 13 to 25 seconds against a usable main window at roughly twice that - so from here the whole
/// of a load is visible. Outside the process it is not: the runner waits on fixed budgets, and the
/// same wait has measured 696 seconds and then 16.3 on the same machine, a spread of 43. A budget
/// tuned to the lucky run is a false alarm postponed. With phases, waiting stops being a number and
/// becomes "wait while something is still happening, and say what it was when it stopped".
/// </para>
/// <para>
/// <b>Only what RevitAPI declares.</b> Progress and document events live on
/// <c>ControlledApplication</c>, which both add-in forms have; dialogs and idling are RevitAPIUI and
/// belong to the UI form. That is the seam the host is already built on, and it means a DBApplication
/// edition gets phases without pretending it has an interface.
/// </para>
/// <para>
/// <b>Nothing here may be slow.</b> These handlers run on Revit API thread, inside Revit own
/// progress reporting - the hottest callback in the process during a model load. Everything is a
/// comparison and a hand-off; whoever receives the events must not block either.
/// </para>
/// </remarks>
public sealed class RevitDiagnostics : IDisposable
{
    /// <summary>
    /// How often a bare position update is passed on.
    /// </summary>
    /// <remarks>
    /// Progress arrives far faster than anything should carry it, and thinning is at the source
    /// rather than the sink because the cheapest event is the one never created. What is never
    /// thinned is a change of meaning: a caption, a start, a finish. Those are the transitions a
    /// consumer waits on. A position only proves something is still moving, and one every quarter
    /// second proves that as well as forty do.
    /// </remarks>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly ControlledApplication _controlled;
    private readonly ILog _log;
    private readonly Action<RevitDiagnostic> _observer;

    private readonly object _gate = new();
    private DateTime _lastProgress = DateTime.MinValue;
    private string _caption = string.Empty;
    private RevitPhase _phase = RevitPhase.Starting;
    private bool _disposed;

    /// <summary>Every event Revit raised, including the ones thinned away.</summary>
    /// <remarks>
    /// Kept because the first question about this mechanism is what it costs, and that has to be
    /// measured rather than argued. The ratio of Raised to Published is the measurement.
    /// </remarks>
    public long Raised { get; private set; }

    /// <summary>Events actually handed on.</summary>
    public long Published { get; private set; }

    public RevitDiagnostics(ControlledApplication controlled, ILog log, Action<RevitDiagnostic> observer)
    {
        _controlled = controlled ?? throw new ArgumentNullException(nameof(controlled));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));

        _controlled.ProgressChanged += OnProgress;
        _controlled.DocumentOpening += OnDocumentOpening;
        _controlled.DocumentOpened += OnDocumentOpened;
        _controlled.DocumentClosing += OnDocumentClosing;
        _controlled.ApplicationInitialized += OnInitialized;

        Observe(new RevitDiagnostic(RevitPhase.Starting, "OnStartup"));
    }

    /// <summary>The phase as of the last event.</summary>
    public RevitPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    /// <summary>
    /// Hands an event on from outside this class - the UI half, which owns the dialog events.
    /// </summary>
    /// <remarks>
    /// Public because <c>DialogBoxShowing</c> is declared in RevitAPIUI, which this class must not
    /// name: the host DB/UI split rests on the composition never mentioning a UI type. The UI form
    /// subscribes and calls this, and the phase machine stays in one place.
    /// </remarks>
    public void Observe(RevitDiagnostic diagnostic)
    {
        if (diagnostic is null || _disposed)
            return;

        lock (_gate)
        {
            Raised++;
            Published++;
            _phase = diagnostic.Phase;
        }

        Hand(diagnostic);
    }

    private void Hand(RevitDiagnostic diagnostic)
    {
        try
        {
            _observer(diagnostic);
        }
        catch (Exception error)
        {
            // A diagnostic that can fail the thing it observes is worse than no diagnostic. This
            // runs on the API thread during a model load, and an exception escaping here would
            // surface as a fault inside Revit own progress reporting.
            _log.Warn(error, "diagnostics: an observer threw and was ignored");
        }
    }

    private void OnProgress(object? sender, ProgressChangedEventArgs args)
    {
        if (_disposed)
            return;

        RevitDiagnostic? diagnostic = null;

        lock (_gate)
        {
            Raised++;

            var caption = args.Caption ?? string.Empty;

            // A change of meaning always travels; a change of number travels on a clock. Started
            // and Finished are what bracket a phase, and a new caption is Revit saying it moved on
            // to something else - none of those are worth thinning, and all of them are rare.
            var meaningful =
                args.Stage == ProgressStage.Started ||
                args.Stage == ProgressStage.Finished ||
                args.Stage == ProgressStage.CaptionChanged ||
                !string.Equals(caption, _caption, StringComparison.Ordinal);

            var now = DateTime.UtcNow;

            if (meaningful || now - _lastProgress >= ProgressInterval)
            {
                _lastProgress = now;
                _caption = caption;

                var phase = args.Stage == ProgressStage.Finished ? RevitPhase.Idle : RevitPhase.Working;
                _phase = phase;
                Published++;

                diagnostic = new RevitDiagnostic(phase, caption)
                {
                    Position = args.Position,
                    Lower = args.LowerRange,
                    Upper = args.UpperRange,
                    ApiThread = true,
                };
            }
        }

        if (diagnostic is not null)
            Hand(diagnostic);
    }

    private void OnDocumentOpening(object? sender, DocumentOpeningEventArgs args) =>
        Observe(new RevitDiagnostic(RevitPhase.OpeningDocument, "opening", args.PathName ?? string.Empty)
        {
            ApiThread = true,
        });

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs args) =>
        Observe(new RevitDiagnostic(RevitPhase.DocumentReady, "opened", args.Document?.Title ?? string.Empty)
        {
            ApiThread = true,
        });

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args) =>
        Observe(new RevitDiagnostic(RevitPhase.Closing, "closing document") { ApiThread = true });

    private void OnInitialized(object? sender, ApplicationInitializedEventArgs args) =>
        Observe(new RevitDiagnostic(RevitPhase.Idle, "application initialized") { ApiThread = true });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _controlled.ProgressChanged -= OnProgress;
            _controlled.DocumentOpening -= OnDocumentOpening;
            _controlled.DocumentOpened -= OnDocumentOpened;
            _controlled.DocumentClosing -= OnDocumentClosing;
            _controlled.ApplicationInitialized -= OnInitialized;
        }
        catch (Exception error)
        {
            _log.Debug("diagnostics: unsubscribing threw ({0})", error.GetType().Name);
        }
    }
}
