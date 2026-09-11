using System.ComponentModel;
using System.Runtime.CompilerServices;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Ui;

/// <summary>Where the run has got to, in the order it gets there.</summary>
public enum RoutingPhase
{
    /// <summary>Nothing has started yet.</summary>
    Waiting,

    /// <summary>The search is running.</summary>
    Computing,

    /// <summary>It finished and there is something to look at.</summary>
    Ready,

    /// <summary>Somebody stopped it.</summary>
    Cancelled,

    /// <summary>It threw.</summary>
    Failed,
}

/// <summary>
/// Everything the routing window shows, with no Revit type anywhere in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A delegate instead of a <c>Document</c>.</b> The command hands over "compute this"; this side
/// knows when to call it, what to show while it runs, and nothing else. It can therefore be driven
/// from a playground with a fake delegate - which is the only way a screen gets looked at more than
/// once a day, given that starting Revit costs the better part of a minute.
/// </para>
/// <para>
/// <b>Lengths are formatted by a delegate too, and that is not indirection for its own sake.</b> Our
/// number stands on screen next to Revit's own - in the properties palette, in a schedule - and the
/// precision, the group separator, the decimal symbol and the suppression of trailing zeros were all
/// chosen by the user and are held in the document's <c>Units</c>. A number that disagrees with
/// Revit's on one screen is worse than no number, so the formatting is Revit's; only the call site
/// is ours.
/// </para>
/// <para>
/// There is no apply here. Writing to the model arrives with the phase that does it, together with
/// the command that calls it: a delegate nobody invokes yet is a mechanism with no consumer, and
/// this repository has already named what those turn into.
/// </para>
/// </remarks>
public sealed class RoutingViewModel : INotifyPropertyChanged
{
    private readonly Func<IProgress<RoutingProgress>, CancellationToken, Task<RouteRun>> _compute;
    private readonly Func<double, string> _length;
    private readonly CancellationTokenSource _cancellation = new();

    private RoutingPhase _phase = RoutingPhase.Waiting;
    private string _what = string.Empty;
    private double _done;
    private double _total;
    private RouteRun? _run;
    private string _failure = string.Empty;

    /// <param name="compute">Reads the model and searches it, reporting progress as it goes.</param>
    /// <param name="length">Turns internal feet into what this document would show for the same value.</param>
    public RoutingViewModel(
        Func<IProgress<RoutingProgress>, CancellationToken, Task<RouteRun>> compute,
        Func<double, string> length)
    {
        _compute = compute;
        _length = length;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RoutingPhase Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    /// <summary>
    /// What is happening, in words.
    /// </summary>
    /// <remarks>
    /// <b>Never "Loading".</b> A bar without a sentence says that something is going on and nothing
    /// about what, and the first thing anybody does is wonder whether it is stuck. "Reading the
    /// model" and "Routing ЩО-1, гр. 7" cost nothing and answer that.
    /// </remarks>
    public string What
    {
        get => _what;
        private set => Set(ref _what, value);
    }

    public double Done
    {
        get => _done;
        private set => Set(ref _done, value);
    }

    public double Total
    {
        get => _total;
        private set => Set(ref _total, value);
    }

    /// <summary>True while there is work but no count for it yet - the read before the search.</summary>
    /// <remarks>
    /// A determinate bar sitting at zero for the whole read looks stuck, and the read is the long
    /// half on a big model. It is the same bar either way; only its honesty changes.
    /// </remarks>
    public bool IsIndeterminate => IsBusy && Total <= 0;

    public bool IsBusy => Phase == RoutingPhase.Computing;

    public bool CanCancel => IsBusy;

    public RouteRun? Run
    {
        get => _run;
        private set => Set(ref _run, value);
    }

    public bool HasResult => Run is not null;

    /// <summary>What went wrong, when something did. Empty otherwise.</summary>
    public string Failure
    {
        get => _failure;
        private set => Set(ref _failure, value);
    }

    public bool HasFailure => Failure.Length > 0;

    /// <summary>How the run went, in one line.</summary>
    public string Summary =>
        Run is not { } run
            ? string.Empty
            : $"{run.Found} of {run.Results.Count} circuit(s) routed, in {Elapsed(run.Took)}";

    /// <summary>How long something took, at a scale that says something.</summary>
    /// <remarks>
    /// Measured on the first real run: the search over 55 circuits finished in under a tenth of a
    /// second, and "in 0.0 s" reads as a broken clock rather than as a fast one. The reading that
    /// matters is that the search is not where the waiting is - the model read is - and a zero
    /// cannot say that.
    /// </remarks>
    private static string Elapsed(TimeSpan took) =>
        took.TotalSeconds < 1
            ? $"{took.TotalMilliseconds:F0} ms"
            : $"{took.TotalSeconds:F1} s";

    /// <summary>
    /// Our length against Revit's, and why they differ.
    /// </summary>
    /// <remarks>
    /// <b>The explanation is not decoration, it is the point.</b> Ours is longer because it counts
    /// the drop from the structure to each device, which Revit's own circuit length does not. Stated
    /// as a percentage and nothing else, that difference reads as a defect and gets reported as one;
    /// named, it reads as the reason the number was computed at all.
    /// </remarks>
    public string LengthSummary
    {
        get
        {
            if (Run is not { Found: > 0 } run)
                return string.Empty;

            var drops = run.Results
                .Where(one => one.Status == RouteStatus.Found)
                .Sum(one => one.Approaches);

            var line = $"{_length(run.TotalLength)} computed, Revit reports {_length(run.BuiltInLength)}";

            return drops > 0
                ? line + $" - ours includes {_length(drops)} of drops from the structure to devices"
                : line;
        }
    }

    /// <summary>
    /// What the structure looks like, said only when the run failed to cross it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shown on the one condition that makes it an answer.</b> "No connectivity" for half the
    /// circuits has two opposite causes - a structure genuinely drawn in pieces, or a join tolerance
    /// too small to close gaps a person reads as joints - and the status cannot tell them apart. The
    /// number of groups can.
    /// </para>
    /// <para>
    /// Silent on a clean run, and silent when nothing failed to cross. A line about the shape of the
    /// structure printed after a run where every circuit routed is a true sentence that buries the
    /// ones that matter, which this file already says costs the whole report.
    /// </para>
    /// </remarks>
    public string StructureSummary
    {
        get
        {
            if (Run is not { } run || run.Count(RouteStatus.NoConnectivity) == 0)
                return string.Empty;

            var shape = run.Shape;

            if (shape.Carriers == 0)
                return string.Empty;

            var line = $"A route runs inside one connected group. This structure reads as "
                + $"{shape.Groups} group(s) over {shape.Carriers} carrier(s), the largest holding {shape.Largest}"
                + $", of which {shape.Junctions} join at more than two points.";

            // Named because the alternative is guessing with a press of the button per guess. It
            // is a hint about where to look, not a diagnosis: a riser nobody drew produces few
            // large groups, and no tolerance closes that.
            if (run.Tolerances.Count == 0)
                return line;

            // The table rather than the advice. Which value to take is a judgement about this model
            // - a wider tolerance joins runs a person reads as joined, and also joins two that
            // merely pass near each other - and the table is what that judgement is made from.
            var tried = run.Tolerances
                .Select(one => $"{_length(one.Tolerance)} would give {one.Shape.Groups}")
                .ToList();

            return line + " Cabling:JoinToleranceMm at " + string.Join(", ", tried) + ".";
        }
    }

    public bool HasStructureSummary => StructureSummary.Length > 0;

    /// <summary>
    /// What measuring the drop along a carrier, rather than to its ends, would change.
    /// </summary>
    /// <remarks>
    /// <b>A question on screen, not a feature.</b> The router measures a device to a carrier's
    /// terminals, so a socket under the middle of a long run is measured to an end. Whether that
    /// matters is a property of the model, and this says so out loud until the answer is acted on -
    /// then both the line and the study go.
    /// </remarks>
    public string ApproachSummary
    {
        get
        {
            if (Run?.Approach is not { } study || study.Agree)
                return string.Empty;

            // The decomposition rather than three bare numbers. Measured on a real model, two
            // thirds of what "measure along the carrier" appeared to save was projection onto
            // conduits - which a cable cannot leave except where it joins something. Stating the
            // total alone would promise a saving that is not there, and hide the one that is.
            var line = $"Drops total {_length(study.ByTerminals)} over {study.ReachedByTerminals} of "
                + $"{study.Terminals} terminal(s), measured to the ends of carriers.";

            if (study.Gained > 0)
                line += $" Measured along a carrier, {study.Gained} more terminal(s) would find one.";

            if (study.FreeSaving > 0)
                line += $" Tapping a tray along its length would save {_length(study.FreeSaving)}"
                    + " and needs nothing added to the model.";

            if (study.BoxSaving > 0)
                line += $" A further {_length(study.BoxSaving)} needs junction boxes on conduits, which"
                    + " a cable can leave only where they join something.";

            return line + $" The largest single drop would shorten by {_length(study.Worst)}.";
        }
    }

    public bool HasApproachSummary => ApproachSummary.Length > 0;

    /// <summary>What the circuits cut in boxes ask for, said only when there are any.</summary>
    /// <remarks>
    /// Counts, and nothing placed yet: this is the compute phase, which writes nothing into the
    /// model. The number is what the designer weighs before letting the apply phase put indicators
    /// into somebody's building, so it is on screen first.
    /// </remarks>
    public string BoxSummary
    {
        get
        {
            if (Run is not { } run)
                return string.Empty;

            var circuits = run.Results.Count(one =>
                one.Status == RouteStatus.Found && one.Connection == CircuitConnection.AtJunctionBox);

            if (circuits == 0)
                return string.Empty;

            var recommended = run.Boxes.Count(box => box.IsRecommendation);
            var existing = run.Boxes.Count - recommended;
            var line = $"{circuits} circuit(s) cut in junction boxes: {recommended} box(es) to recommend";

            if (existing > 0)
                line += $", {existing} existing box(es) used";

            return line + $", {run.Boxes.Sum(box => box.Spurs)} device(s) served.";
        }
    }

    public bool HasBoxSummary => BoxSummary.Length > 0;

    /// <summary>What the read of the model left behind, when it left anything.</summary>
    /// <remarks>
    /// Shown on every run that has any, not only a failed one. These are the counts written so that
    /// nothing is lost silently, and a run where every circuit routed can still have been computed
    /// over a structure missing a link - which makes every length on the screen quietly short.
    /// </remarks>
    public IReadOnlyList<string> Reading => Run?.Reading ?? Array.Empty<string>();

    public bool HasReading => Reading.Count > 0;

    /// <summary>
    /// The failures, grouped by cause, each naming where it stopped.
    /// </summary>
    /// <remarks>
    /// Causes first and circuits under them, because twelve failures are two or three causes and a
    /// person acts on a cause. The addresses are capped: a model with three hundred unreachable
    /// devices has one problem, and printing it three hundred times hides it rather than showing it.
    /// </remarks>
    public IReadOnlyList<string> Causes
    {
        get
        {
            if (Run is not { } run)
                return Array.Empty<string>();

            var lines = new List<string>();

            foreach (var cause in run.Causes)
            {
                var blocked = run.Blocked(cause).ToList();
                lines.Add($"{Explain(cause)} - {blocked.Count} circuit(s)");

                foreach (var one in blocked.Take(Addresses))
                {
                    if (one.BlockedAt.Length > 0)
                        lines.Add("    " + one.BlockedAt);
                }

                if (blocked.Count > Addresses)
                    lines.Add($"    and {blocked.Count - Addresses} more");
            }

            return lines;
        }
    }

    public bool HasCauses => Causes.Count > 0;

    /// <summary>Runs the search, and says what stopped it when something does.</summary>
    public async Task ComputeAsync()
    {
        if (Phase != RoutingPhase.Waiting)
            return;

        Phase = RoutingPhase.Computing;
        What = "Reading the model";

        // Progress captured on the thread that constructs it, which is the UI thread: the search
        // reports from a background one and this is what carries each step back across.
        var progress = new Progress<RoutingProgress>(step =>
        {
            What = step.What;
            Done = step.Done;
            Total = step.Total;
        });

        try
        {
            Run = await _compute(progress, _cancellation.Token).ConfigureAwait(true);
            Phase = RoutingPhase.Ready;
            What = Run.Found == Run.Results.Count && Run.Found > 0
                ? "Every circuit was routed"
                : "Finished";
        }
        catch (OperationCanceledException)
        {
            Phase = RoutingPhase.Cancelled;
            What = "Stopped";
        }
        catch (Exception error)
        {
            Phase = RoutingPhase.Failed;
            What = "It did not finish";

            // The type as well as the message. A message alone reads as prose and gets guessed at;
            // the type is the half somebody can search for.
            Failure = error.GetType().Name + ": " + error.Message;
        }
    }

    public void Cancel() => _cancellation.Cancel();

    /// <summary>How many circuits are named under one cause before the rest are counted.</summary>
    private const int Addresses = 5;

    private static string Explain(RouteStatus status) => status switch
    {
        RouteStatus.NoCarrierNear => "No tray or conduit within reach",
        RouteStatus.NoConnectivity => "Both ends reachable, but nothing joins them",
        RouteStatus.NothingToRoute => "Nothing to route",
        _ => status.ToString(),
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        Raise(name);

        // The derived ones by hand, and deliberately. A framework that worked them out would be a
        // package, and inside Revit a package is an assembly somebody else may also ship.
        switch (name)
        {
            case nameof(Phase):
                Raise(nameof(IsBusy));
                Raise(nameof(CanCancel));
                Raise(nameof(IsIndeterminate));
                break;

            case nameof(Total):
                Raise(nameof(IsIndeterminate));
                break;

            case nameof(Run):
                Raise(nameof(HasResult));
                Raise(nameof(Summary));
                Raise(nameof(LengthSummary));
                Raise(nameof(StructureSummary));
                Raise(nameof(HasStructureSummary));
                Raise(nameof(ApproachSummary));
                Raise(nameof(HasApproachSummary));
                Raise(nameof(BoxSummary));
                Raise(nameof(HasBoxSummary));
                Raise(nameof(Reading));
                Raise(nameof(HasReading));
                Raise(nameof(Causes));
                Raise(nameof(HasCauses));
                break;

            case nameof(Failure):
                Raise(nameof(HasFailure));
                break;
        }
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One step of a run, as the search reports it.</summary>
/// <remarks>
/// A struct with three fields rather than an event with three arguments: it crosses a thread
/// boundary through <see cref="IProgress{T}"/> once per circuit, and the search must not pay for the
/// screen.
/// </remarks>
public readonly struct RoutingProgress
{
    public RoutingProgress(string what, double done, double total)
    {
        What = what;
        Done = done;
        Total = total;
    }

    public string What { get; }

    public double Done { get; }

    public double Total { get; }
}
