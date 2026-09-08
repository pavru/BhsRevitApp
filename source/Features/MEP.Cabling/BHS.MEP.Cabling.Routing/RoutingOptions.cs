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

    /// <summary>Added to every computed length, as a fraction, for slack and terminations.</summary>
    public double LengthExtend { get; init; }

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
}
