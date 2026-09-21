namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// What the search is allowed to do, in internal feet throughout.
/// </summary>
/// <remarks>
/// Every distance here is raw internal units. The user typed "2,4 м" into a box; the view model
/// parsed it against the document's own unit settings and handed the number over. Nothing on this
/// side knows what a metre is, and that is deliberate - see <see cref="Point3"/>.
/// </remarks>
public sealed class RoutingOptions
{
    /// <summary>
    /// How far apart two carriers may be and still count as joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Proximity, not connectors, and that is the owner's decision rather than a shortcut.</b> The
    /// predecessor walked <c>Connector.AllRefs</c>, which is exact and describes a model nobody
    /// builds: trays are routinely drawn butted together with no connection between runs, and a
    /// straight or a tee from another family joins nothing at all. A search over connectors alone
    /// reports "no connectivity" for a route a person can see with their eyes, which is the failure
    /// mode that makes a tool untrusted.
    /// </para>
    /// <para>
    /// The cost is that adjacency is now a spatial question over thousands of elements, so the index
    /// under it is not an optimisation - a pairwise scan is quadratic and the run is measured in
    /// minutes rather than seconds. That is why it is here from the first line rather than added
    /// when somebody complains.
    /// </para>
    /// </remarks>
    public double JoinTolerance { get; init; }

    /// <summary>How far a device may be from the structure before its circuit is reported unreachable.</summary>
    public double MaxApproach { get; init; }

    /// <summary>
    /// How much longer a conduit route may be before a tray route wins anyway.
    /// </summary>
    /// <remarks>
    /// Zero means "shortest wins, whatever it runs in". The predecessor called this
    /// <c>PreferConduitUntil</c> and applied it as a multiplier on the whole route length, which is
    /// why <see cref="CarrierNode.Class"/> has to travel with every node: the preference is between
    /// kinds of carrier, and the search cannot express it without knowing which is which.
    /// </remarks>
    public double PreferConduitUntil { get; init; }


    /// <summary>
    /// Whether the drop from the structure to a device is measured along axes rather than straight.
    /// </summary>
    /// <remarks>
    /// <b>True by default, because a cable does not fly.</b> A straight line from a tray to a socket
    /// crosses whatever is between them; the cable goes across and then down. Measured along axes the
    /// number is larger and closer to what gets installed, which is the point of computing it at all.
    /// The predecessor measured straight and carried a tolerance to absorb the difference.
    /// </remarks>
    public bool AxisAlignedApproach { get; init; } = true;

    /// <summary>Circuits with a single device are usually feeders, and often not worth routing.</summary>
    public bool SkipSingleDeviceCircuits { get; init; }

    /// <summary>
    /// What one splice made in the structure is worth, in internal feet of cable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The exchange rate that lets one number stand for two things - the owner's question of
    /// 2026-09-21, and the answer to it.</b> A cable tree is shorter the more often it splits, and a
    /// split costs a box. The two are in different units, so minimising them together needs a rate;
    /// divide the installed price of a box by the price of a metre of the cable this project usually
    /// runs, and what comes out is how many metres of cable the project would rather run than cut.
    /// Cost in money and cost in metres are then the same objective, one divided by the other's unit -
    /// and metres are the unit already in the model, so no price list has to live anywhere.
    /// </para>
    /// <para>
    /// Zero makes the search split wherever it is even slightly shorter, which is the pure geometric
    /// answer and not usually the buildable one.
    /// </para>
    /// <para>
    /// <b>Part of what a splice costs is already counted elsewhere and must not be counted here.</b>
    /// <see cref="SlackRule.AtBox"/> and <see cref="SlackRule.AtSplice"/> add real cable at every place
    /// the cable is cut, so a split is never free even at zero. This number is only the part that is
    /// not cable: the box, the labour, and the obligation to keep the joint reachable.
    /// </para>
    /// </remarks>
    public double SpliceCost { get; init; }

    /// <summary>How many cables a device's terminals hold when its type does not say.</summary>
    /// <remarks>
    /// Two is a device the cable passes through and no more, which is what was built before there was
    /// a tree. See <see cref="Terminal.Capacity"/> for why absence means this rather than "as many as
    /// it takes".
    /// </remarks>
    public int TerminalCapacity { get; init; } = 2;
}
