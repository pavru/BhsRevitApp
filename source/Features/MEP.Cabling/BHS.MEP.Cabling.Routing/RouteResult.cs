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

    /// <summary>
    /// A circuit cut in boxes, routed without additional boxes, has a device that no existing box
    /// reaches through the structure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failure, not a fallback - the owner's decision of 2026-09-17.</b> The mode says "do not
    /// invent boxes", so a device no existing box can serve has no length this mode can honestly give:
    /// counting it as cut at the terminal, or recommending a box after all, would write a number
    /// computed by a method nobody chose, and it would go into a cable schedule looking like the rest.
    /// </para>
    /// <para>
    /// Last in the enumeration, so that every value already stored or compared keeps its number.
    /// Distinct from <see cref="NoCarrierNear"/>: the device reaches the structure, and there is no box
    /// on the part of it the device reaches.
    /// </para>
    /// </remarks>
    NoBoxReachable,

    /// <summary>
    /// An end of the circuit has carriers within reach, and none of them admits its cable group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Apart from <see cref="NoCarrierNear"/> because the two want opposite actions.</b> "Nothing
    /// within reach" sends a designer to draw a tray that is missing; this one sends him to a tray
    /// that is already there, to decide whether it may carry this cable. Reported as the same warning
    /// - the owner's decision of 2026-09-22, and the registered text was widened to stay true of both
    /// - but told apart on the screen, which costs nothing and is where the difference is read.
    /// </para>
    /// <para>
    /// Last in the enumeration, so every value already compared keeps its number.
    /// </para>
    /// </remarks>
    NoCarrierAllowed,
}

/// <summary>Where the cable leaves the structure for one device.</summary>
/// <remarks>
/// <para>
/// <b>The point the search already found and used to throw away.</b> Each leg ends where the cable
/// leaves the last carrier for the device - the nearest point along an open tray, the end of a
/// closed conduit - and that point is exactly where a junction box would stand. It was computed for
/// the length and dropped before the result was returned, so the apply phase had nowhere to put an
/// indicator.
/// </para>
/// <para>
/// Recorded in both connection modes. In the terminal mode nobody puts a box there, but it is still
/// where the cable comes down, and the screen and the route's geometry want it either way.
/// </para>
/// </remarks>
public sealed class Tap
{
    public Tap(Terminal device, CarrierId carrier, Point3 at, double spur)
    {
        Device = device;
        Carrier = carrier;
        At = at;
        Spur = spur;
    }

    /// <summary>The device this tap serves.</summary>
    public Terminal Device { get; }

    /// <summary>The carrier the cable leaves.</summary>
    public CarrierId Carrier { get; }

    /// <summary>Where on that carrier it leaves, in internal feet, host coordinates.</summary>
    public Point3 At { get; }

    /// <summary>How far it then travels to the device, in internal feet.</summary>
    public double Spur { get; }

    /// <summary>
    /// The existing box this device is served from, when the circuit is routed without additional boxes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null in every other case, and then the planner decides the box by the radius, as it always has.
    /// When it is set the router has already decided - the box nearest along the structure, however far
    /// - and the planner must not second-guess it by distance: the whole point of the mode is that a box
    /// one radius away is not a reason to recommend another.
    /// </para>
    /// <para>
    /// <see cref="Carrier"/> and <see cref="At"/> still say where the cable leaves the structure for the
    /// device, which in this mode is where the spur ends rather than where a box stands.
    /// </para>
    /// </remarks>
    public CarrierId? Box { get; init; }

    /// <summary>Whether the carrier the cable leaves is one cable may be spliced in.</summary>
    /// <remarks>
    /// <para>
    /// Carried on the tap rather than looked up later, because the planner has no network: it is
    /// given taps and boxes and a radius, and asking it to resolve a <see cref="CarrierId"/> back to
    /// a node would hand it a second way to know the structure.
    /// </para>
    /// <para>
    /// What it means to the planner is narrow and worth stating: no junction box is recommended here,
    /// because the branch of the cable graph happens in this element itself. It does not move the
    /// point, and it never beats a box already in the model - the owner's answer, and the reason is
    /// that a box somebody drew was drawn on purpose.
    /// </para>
    /// </remarks>
    public bool AllowsSplicing { get; init; }

    /// <summary>How far the spur runs along the structure, from its box to where it leaves it, in internal feet.</summary>
    /// <remarks>
    /// Zero unless <see cref="Box"/> is set. Kept apart from <see cref="Spur"/>, which stays the drop from
    /// the structure to the device in both modes: the one is carried in a tray, the other is not, and a
    /// breakdown of length by carrier will need to tell them apart.
    /// </remarks>
    public double SpurAlongCarriers { get; init; }
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

    /// <summary>The carriers the cable runs through, each once. Empty unless <see cref="Status"/> is Found.</summary>
    /// <remarks>
    /// <b>A set in ascending order, and since 2026-09-21 it no longer claims to be a traversal.</b>
    /// A circuit's cable is a tree, and a tree has no order to walk it in; what it has is the carriers
    /// it occupies. Sorted rather than left in whatever order the search laid them, because this list
    /// becomes <c>BHS_Cbl_ОтпечатокМаршрута</c> and is compared against what a previous run stored: an
    /// order that depended on the search's tie-breaking would report circuits stale for nothing.
    /// </remarks>
    public IReadOnlyList<CarrierId> Path { get; init; } = Array.Empty<CarrierId>();

    /// <summary>Where the cable is cut so that it can go more than one way.</summary>
    /// <remarks>
    /// Empty for a circuit whose cable never splits - two devices in a row on one tray, or a
    /// point-to-point circuit. <see cref="BoxPlanner"/> turns these into boxes; a branch made in a
    /// device's terminals needs none.
    /// </remarks>
    public IReadOnlyList<Branch> Branches { get; init; } = Array.Empty<Branch>();

    /// <summary>Length along the carriers, in internal feet, without the slack.</summary>
    public double AlongCarriers { get; init; }

    /// <summary>
    /// <see cref="AlongCarriers"/> divided by the class of carrier it was walked along - "tray", "conduit",
    /// or whatever the project named - in internal feet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For the cable journal and for estimates, which order cable by where it is laid</b> - the owner's
    /// request of 2026-09-16. Keyed by the class rather than split into two fixed numbers, because the
    /// class is an open string: a project may name a third kind, and its length has to land somewhere a
    /// reader can find rather than inside the conduits.
    /// </para>
    /// <para>
    /// Summed as the walk goes, carrier by carrier, with the same measure the length is summed with - so the
    /// parts add up to <see cref="AlongCarriers"/> by construction rather than by a subtraction somebody
    /// could get wrong. A spur walked from an existing box counts under the class it was walked along, like
    /// any other stretch of carrier. Keys compare without regard to case, as the conduit preference does.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, double> AlongByClass { get; init; } = NoClasses;

    /// <summary>The length walked along carriers of one class, zero when the route walked none.</summary>
    public double AlongClass(string carrierClass) =>
        AlongByClass.TryGetValue(carrierClass, out var length) ? length : 0;

    private static readonly IReadOnlyDictionary<string, double> NoClasses =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The drops from the structure to the two ends, in internal feet.</summary>
    /// <remarks>
    /// <para>
    /// Kept apart from <see cref="AlongCarriers"/> rather than folded in, because the result screen
    /// has to explain why the computed length exceeds Revit's own. "Longer by 9 %" invites a bug
    /// report; "longer by the drops to devices, 2.4 m on average" does not.
    /// </para>
    /// <para>
    /// <b>Each leg pays a drop at both ends, so an intermediate device's drop is counted twice - and
    /// that is one of two real cases, not the shape of the world.</b> Told by the owner: a device is
    /// connected either with the cable cut at the terminal, where a doubled cable comes down and a
    /// new run leaves, and twice is right; or with a single cable cut at a junction box, where the
    /// trunk stays up and only a spur descends, and twice is roughly double the truth. On the first
    /// real model the drops were 38 % of the total, so the difference is not a detail.
    /// </para>
    /// <para>
    /// <b>Only the first is implemented, and it arrived as an unstated assumption rather than as a
    /// decision.</b> The junction box may also be absent from the model where that is nonetheless how
    /// it is wired, which the owner asks be reported rather than guessed at. The open questions are
    /// in CLAUDE.md under the compute phase; nothing here should grow past them until they are
    /// answered.
    /// </para>
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

    /// <summary>How the circuit was routed - carried so that what is done with the taps can tell.</summary>
    public CircuitConnection Connection { get; init; } = CircuitConnection.AtTerminal;

    /// <summary>How many conductors its cable has, zero when Revit said nothing.</summary>
    /// <remarks>
    /// Carried here for the same reason as <see cref="Connection"/> and <see cref="BuiltInLength"/>:
    /// the box plan is computed from routes alone, and a number it has to multiply by must travel
    /// with the route rather than be looked up beside it. A second lookup is a second chance to pair
    /// the wrong circuit with the wrong count.
    /// </remarks>
    public int Conductors { get; init; }

    /// <summary>Which group of cables this circuit belongs to, carried for the same reason as above.</summary>
    /// <remarks>
    /// The planner needs it after the search is over - a box admits some groups and not others, and
    /// which one a spur belongs to is a property of the circuit it came from. Travelling with the
    /// route rather than looked up beside it, so there is no second chance to pair the wrong group
    /// with the wrong circuit.
    /// </remarks>
    public string CableGroup { get; init; } = string.Empty;

    /// <summary>Where the cable leaves the structure, one per device, in the order the circuit visits them.</summary>
    /// <remarks>Empty unless <see cref="Status"/> is Found.</remarks>
    public IReadOnlyList<Tap> Taps { get; init; } = Array.Empty<Tap>();

    /// <summary>What the route measures: along the carriers and down to the two ends.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not the cable's length, and since 2026-09-21 it no longer pretends to be.</b> Slack used to
    /// live here too, as a fraction of this number, and the total was the sum of the three. The
    /// owner's model of slack counts the places the cable is cut - the panel, each device, each
    /// junction box, each splice in a carrier - and how many boxes a circuit's cable is cut in is
    /// decided by <see cref="BoxPlanner"/>, after every circuit has been routed: taps closer than the
    /// box radius share a box, and an existing box may take two of them.
    /// </para>
    /// <para>
    /// So a route cannot state a cable length, and does not. <see cref="RouteRun"/> knows both the
    /// routes and the plan, and it is the one that adds slack and answers for the total. Leaving
    /// <c>Slack</c> here to be filled in afterwards would have been cheaper by a refactor and would
    /// have meant a route straight from the router silently reporting a total short by its slack.
    /// </para>
    /// </remarks>
    public double Measured => AlongCarriers + Approaches;
}
