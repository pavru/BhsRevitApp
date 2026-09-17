using System.ComponentModel;
using System.Globalization;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Ui;

/// <summary>
/// The screen for "which circuits carry a length that no longer describes the model".
/// </summary>
/// <remarks>
/// <para>
/// <b>On the plain axis, like the routing screen and for the same reason.</b> It is driven across a
/// thread boundary while a search runs, and it must not be able to touch a <c>Document</c> - which it
/// cannot, because this assembly cannot see one. What it shows arrives as <see cref="LengthReview"/>,
/// a type the search owns, and what it can do about it arrives as two delegates.
/// </para>
/// <para>
/// <b>It reads and never writes - the owner's decision of 2026-09-17.</b> Recomputing is what "Route
/// cables" is for; this window says what is stale and can select it, so that the person sees the same
/// circuits in a schedule and on the plan. A second place that writes lengths would be a second place
/// to keep in step with the first.
/// </para>
/// </remarks>
public sealed class ReviewViewModel : INotifyPropertyChanged
{
    private readonly Func<IProgress<RoutingProgress>, CancellationToken, Task<LengthReview>> _compute;
    private readonly Func<double, string> _length;
    private readonly Action<IReadOnlyList<long>>? _select;
    private readonly CancellationTokenSource _cancel = new();

    private LengthReview? _review;
    private string _what = "Reading the model";
    private string _failure = string.Empty;
    private bool _busy = true;
    private bool _selected;

    public ReviewViewModel(
        Func<IProgress<RoutingProgress>, CancellationToken, Task<LengthReview>> compute,
        Func<double, string> length,
        Action<IReadOnlyList<long>>? select = null)
    {
        _compute = compute;
        _length = length;
        _select = select;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LengthReview? Review
    {
        get => _review;
        private set
        {
            _review = value;
            Raise(null);
        }
    }

    /// <summary>What is happening right now, for the bar that would otherwise say nothing.</summary>
    public string What
    {
        get => _what;
        private set
        {
            _what = value;
            Raise(nameof(What));
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            _busy = value;
            Raise(null);
        }
    }

    public string Failure
    {
        get => _failure;
        private set
        {
            _failure = value;
            Raise(null);
        }
    }

    public bool HasFailure => Failure.Length > 0;

    /// <summary>What the check found, in one line.</summary>
    public string Summary =>
        Review is not { } review
            ? string.Empty
            : review.Any
                ? $"{review.Stale.Count} of {review.Examined} circuit(s) carry a stored length that no longer "
                  + $"describes this model; {review.Current} are current"
                  + (review.NeverWritten > 0 ? $", and {review.NeverWritten} routed but were never written" : string.Empty)
                : $"Nothing is stale: {review.Current} of {review.Examined} circuit(s) carry a stored length "
                  + "that still holds"
                  + (review.NeverWritten > 0 ? $", and {review.NeverWritten} routed but were never written" : string.Empty);

    /// <summary>One line per stale circuit: which one, why, and by how much.</summary>
    public IReadOnlyList<string> Lines =>
        Review is not { } review
            ? Array.Empty<string>()
            : review.Stale.Select(Describe).ToArray();

    public bool HasLines => Lines.Count > 0;

    /// <summary>Whether the model can be asked to select what was found.</summary>
    /// <remarks>
    /// Offered once, and only for something to select. The button stays out of the way on a clean
    /// model, and pressing it twice would ask the same question of a selection that already answers it.
    /// </remarks>
    public bool CanSelect => _select is not null && !_selected && !IsBusy && HasLines;

    public CancellationToken Token => _cancel.Token;

    public bool IsFinished => !IsBusy;

    public async Task ComputeAsync()
    {
        try
        {
            var progress = new Progress<RoutingProgress>(one => What = one.What);
            Review = await _compute(progress, _cancel.Token).ConfigureAwait(true);
            What = "Done";
        }
        catch (OperationCanceledException)
        {
            Failure = "The check was stopped.";
        }
        catch (Exception error)
        {
            Failure = error.Message;
        }
        finally
        {
            IsBusy = false;
            Raise(null);
        }
    }

    /// <summary>Asks the model to select the stale circuits.</summary>
    public void Select()
    {
        if (!CanSelect || Review is not { } review)
            return;

        _selected = true;
        _select?.Invoke(review.Stale.Select(one => one.Circuit.Value).ToArray());
        Raise(null);
    }

    public void Cancel() => _cancel.Cancel();

    /// <summary>
    /// One stale circuit in one sentence: which circuit, then each reason with its numbers.
    /// </summary>
    /// <remarks>
    /// <b>The address comes first and carries the id</b>, for the reason the first real run proved: an
    /// id is what Revit's own "select by ID" takes, and a type name is the same for hundreds of sockets.
    /// Everything after it is why the stored answer stopped being true, with both sides of each
    /// difference - "longer by 9 %" invites a bug report, "13,2 m stored against 14,4 m" does not.
    /// </remarks>
    private string Describe(StaleCircuit stale)
    {
        var said = new List<string>();

        if (stale.Has(StaleReason.NoRouteNow))
        {
            said.Add(stale.BlockedAt.Length > 0
                ? $"no route is found any more - {stale.BlockedAt}"
                : "no route is found any more");

            said.Add($"{_length(stale.StoredLength)} is stored against {Carriers(stale.Left)}");
        }

        if (stale.Has(StaleReason.CarriersDiffer))
        {
            var left = stale.Left.Count > 0 ? $"{stale.Left.Count} left" : string.Empty;
            var arrived = stale.Arrived.Count > 0 ? $"{stale.Arrived.Count} arrived" : string.Empty;
            var both = string.Join(", ", new[] { left, arrived }.Where(one => one.Length > 0));

            said.Add(both.Length > 0
                ? $"it runs through other carriers now ({both}): {Carriers(stale.Left.Concat(stale.Arrived).ToArray())}"
                : "it runs through the same carriers in another order");
        }

        if (stale.Has(StaleReason.LengthDiffers))
            said.Add($"{_length(stale.StoredLength)} is stored, {_length(stale.ComputedLength)} is computed now");

        if (stale.Has(StaleReason.ConnectionDiffers))
        {
            said.Add($"it was computed as {Word(stale.StoredConnection)} and is routed as "
                + $"{Word(stale.ComputedConnection)} now");
        }

        var number = stale.Number.Length > 0 ? stale.Number + " " : string.Empty;

        return $"{number}[{stale.Circuit.Value.ToString(CultureInfo.InvariantCulture)}]: "
            + string.Join("; ", said);
    }

    /// <summary>A handful of carrier ids, in the spelling Revit's "select by ID" takes.</summary>
    /// <remarks>
    /// Cut at five, and the cut is said out loud. A route through forty trays would otherwise put forty
    /// numbers on one line and hide the sentence they belong to; "and 35 more" keeps the line readable
    /// and keeps the reader from believing the list is the whole of it.
    /// </remarks>
    private static string Carriers(IReadOnlyList<CarrierId> carriers)
    {
        if (carriers.Count == 0)
            return "no carriers this model still has";

        var named = string.Join(", ", carriers.Take(5).Select(one => one.ToString()));

        return carriers.Count > 5 ? $"{named} and {carriers.Count - 5} more" : named;
    }

    private static string Word(CircuitConnection? connection) => connection switch
    {
        CircuitConnection.AtJunctionBox => "cut in junction boxes",
        CircuitConnection.AtTerminal => "cut at the terminals",
        _ => "an unrecorded connection",
    };

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
