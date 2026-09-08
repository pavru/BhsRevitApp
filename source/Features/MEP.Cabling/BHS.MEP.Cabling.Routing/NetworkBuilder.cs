namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// Turns a bag of carriers into a network, by working out which of them touch.
/// </summary>
/// <remarks>
/// <para>
/// Adjacency is decided here rather than by the caller, and deliberately: the Revit side knows how
/// to read a tray out of a model, and nothing else. Whether two trays count as joined is a rule
/// about routing, it changes with a setting the user controls, and it has to be the same rule
/// whether the carriers came from the host model, a link, or a test.
/// </para>
/// <para>
/// Two carriers are joined when an end of one is within the tolerance of an end of the other. Ends,
/// not bodies: a cable enters a tray at its end and leaves at the other, and a tray passing a metre
/// above another one is not a junction however close it comes.
/// </para>
/// </remarks>
public static class NetworkBuilder
{
    /// <summary>Builds the network the router will search.</summary>
    /// <remarks>
    /// <b>Takes the whole options object rather than the two distances it needs, and that is not
    /// convenience.</b> The network carries an index sized for the router's reach, and an index
    /// whose cells are smaller than the radius asked of it does not fail - it quietly returns fewer
    /// candidates, and circuits come back unroutable for no reason anybody can see. Passing the two
    /// numbers separately is one call site away from that at all times; passing the options makes it
    /// impossible to disagree with itself.
    /// </remarks>
    public static RouteNetwork Build(
        long version,
        IReadOnlyList<CarrierNode> carriers,
        RoutingOptions options)
    {
        var joinTolerance = options.JoinTolerance;
        var approachRadius = options.MaxApproach;

        // The cell is the tolerance, so a query looks at twenty-seven cells and finds every
        // candidate within reach. A tolerance of zero means "ends must coincide", which is still a
        // legitimate answer and still has to work.
        var index = new SpatialIndex(Math.Max(joinTolerance, 1e-6));

        for (var i = 0; i < carriers.Count; i++)
            index.AddSpan(i, carriers[i].Start, carriers[i].End);

        var adjacency = new Dictionary<CarrierId, IReadOnlyList<CarrierId>>(carriers.Count);
        var neighbours = new List<CarrierId>();
        var seen = new HashSet<int>();

        for (var i = 0; i < carriers.Count; i++)
        {
            var one = carriers[i];
            neighbours.Clear();
            seen.Clear();
            seen.Add(i);

            foreach (var candidate in Candidates(index, one))
            {
                if (!seen.Add(candidate))
                    continue;

                if (Touches(one, carriers[candidate], joinTolerance))
                    neighbours.Add(carriers[candidate].Id);
            }

            adjacency[one.Id] = neighbours.ToArray();
        }

        return new RouteNetwork(version, carriers, adjacency, approachRadius);
    }

    private static IEnumerable<int> Candidates(SpatialIndex index, CarrierNode one)
    {
        foreach (var candidate in index.Near(one.Start))
            yield return candidate;

        foreach (var candidate in index.Near(one.End))
            yield return candidate;
    }

    /// <summary>Whether any end of one carrier is within reach of any end of the other.</summary>
    private static bool Touches(CarrierNode a, CarrierNode b, double tolerance) =>
        a.Start.DistanceTo(b.Start) <= tolerance
        || a.Start.DistanceTo(b.End) <= tolerance
        || a.End.DistanceTo(b.Start) <= tolerance
        || a.End.DistanceTo(b.End) <= tolerance;
}
