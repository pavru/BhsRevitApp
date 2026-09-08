namespace BHS.MEP.Cabling.Routing;

/// <summary>Why a circuit came out the way it did.</summary>
/// <remarks>
/// <para>
/// <b>This exists because its absence was measured as a defect.</b> The predecessor's
/// <c>RoutingUtils.RouteCircuit</c> returns an empty list for at least three unrelated reasons - no
/// carrier near an end, no connectivity between the ends, and a circuit with nothing on it - and
/// from outside they are the same nothing. A user whose circuit "did not compute" then has no way to
/// learn which, and neither does the screen that reports it.
/// </para>
/// <para>
/// Twelve failures out of three hundred and fifty are two or three causes, not twelve problems. The
/// status is what lets them be grouped, and grouping is what turns a number into work somebody can
/// actually do.
/// </para>
/// </remarks>
public enum RouteStatus
{
    /// <summary>A route was found.</summary>
    Found,

    /// <summary>An end has no cable-bearing element within the configured reach.</summary>
    NoCarrierNear,

    /// <summary>Both ends reach the structure, but no path joins them through it.</summary>
    NoConnectivity,

    /// <summary>Nothing to route: a circuit with no devices, or a source that is also its only device.</summary>
    NothingToRoute,
}

/// <summary>What the search found for one circuit.</summary>
public sealed class RouteResult
{
    public RouteResult(CarrierId circuit, RouteStatus status, long networkVersion)
    {
        Circuit = circuit;
        Status = status;
        NetworkVersion = networkVersion;
    }

    public CarrierId Circuit { get; }

    public RouteStatus Status { get; }

    /// <summary>The version of the network this was computed on.</summary>
    /// <remarks>
    /// Carried on the result rather than kept beside it, so that an answer separated from its run -
    /// reopened from the last result, read back from a model - can still say whether it is stale.
    /// </remarks>
    public long NetworkVersion { get; }

    /// <summary>The carriers walked, in order. Empty unless <see cref="Status"/> is Found.</summary>
    public IReadOnlyList<CarrierId> Path { get; init; } = Array.Empty<CarrierId>();

    /// <summary>Length along the carriers, in internal feet.</summary>
    public double AlongCarriers { get; init; }

    /// <summary>The drops from the structure to the two ends, in internal feet.</summary>
    /// <remarks>
    /// Kept apart from <see cref="AlongCarriers"/> rather than folded in, because the result screen
    /// has to explain why the computed length exceeds Revit's own. "Longer by 9 %" invites a bug
    /// report; "longer by the drops to devices, 2.4 m on average" does not.
    /// </remarks>
    public double Approaches { get; init; }

    /// <summary>What could not be reached, when the status says so.</summary>
    /// <remarks>
    /// A cause without an address is not actionable: "no route found" sends the reader looking, while
    /// "no carrier within 2.4 m of Socket 3" is the work itself.
    /// </remarks>
    public string BlockedAt { get; init; } = string.Empty;

    /// <summary>What Revit reports for this same circuit today, in internal feet.</summary>
    /// <remarks>
    /// <b>Carried on the result rather than summed beside it, and that is a shape decision.</b> The
    /// screen owes a comparison - ours against Revit's - and the honest one is over the circuits that
    /// routed. Held apart, the two totals are computed in different places over different sets, and
    /// the first version of this compared our forty against Revit's fifty-five: a difference that is
    /// entirely the missing fifteen, and that the first person to see it reports as a bug in the
    /// search. Travelling together, the pair cannot be taken from different sets.
    /// </remarks>
    public double BuiltInLength { get; init; }

    public double TotalLength => AlongCarriers + Approaches;
}
