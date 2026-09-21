using System.Globalization;

namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// Finds how a circuit runs through the structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dijkstra over the network, not a search over candidate pairs.</b> The predecessor tried every
/// carrier near one end against every carrier near the other, ran a depth-first walk for each pair,
/// and copied a visited-dictionary into every recursive call. That is exponential in the branching
/// of a tray network and it allocates a dictionary per step; on the models this is meant for, it is
/// the reason a run takes minutes. One shortest-path pass from all the starts at once answers every
/// pair together, and answers it optimally rather than by keeping the best of what it happened to
/// try.
/// </para>
/// <para>
/// Nothing here calls anything of Revit's, and nothing can: this assembly cannot see it. That is the
/// guarantee that lets the whole search run on a background thread while a modal window keeps
/// repainting - see the project file for why that matters more than it looks.
/// </para>
/// </remarks>
public static class Router
{
    /// <summary>Routes one circuit, or says why it could not be routed.</summary>
    public static RouteResult Route(RouteNetwork network, CircuitSnapshot circuit, RoutingOptions options) =>
        Route(network, circuit, options, null);

    /// <summary>Routes one circuit, or says why it could not be routed.</summary>
    /// <param name="existingBoxesOnly">
    /// The boxes already in the model, when the project routes without additional boxes; null when it
    /// does not. Consulted only for a circuit cut in boxes - a circuit cut at the terminal has no box to
    /// be served from, in this mode or any other.
    /// </param>
    public static RouteResult Route(
        RouteNetwork network,
        CircuitSnapshot circuit,
        RoutingOptions options,
        IReadOnlyCollection<ExistingBox>? existingBoxesOnly)
    {
        if (circuit.Devices.Count == 0)
        {
            return new RouteResult(circuit.Id, RouteStatus.NothingToRoute, network.Version)
            {
                BlockedAt = circuit.Number,
                BuiltInLength = circuit.BuiltInLength,
            };
        }

        if (options.SkipSingleDeviceCircuits && circuit.Devices.Count == 1)
        {
            return new RouteResult(circuit.Id, RouteStatus.NothingToRoute, network.Version)
            {
                BlockedAt = circuit.Number,
                BuiltInLength = circuit.BuiltInLength,
            };
        }

        // The structure this circuit is allowed to use, and the search never sees the rest of it. A
        // model where nobody marked anything hands back the same network, so this line costs nothing
        // until somebody uses the feature.
        var allowed = network.Admitting(circuit.CableGroup);

        // And the boxes it may be served from are the ones that admit it. A box is a carrier, so it
        // answers the same question by the same rule - asked of the box itself rather than of its
        // node in the filtered network, because a caller may hand over a box the network never saw
        // and "not a node" would then silently mean "not allowed".
        var boxes = existingBoxesOnly is null
            ? null
            : existingBoxesOnly.Where(one => one.Groups.Admits(circuit.CableGroup)).ToArray();

        // One cable line leaves the panel and branches below it - the owner's model, and what
        // CableTree searches for. The chain that stood here until 2026-09-21 visited the devices in
        // the order the model listed them, which Revit gives nobody a way to set, and could not split.
        var rules = new CableTree.Rules(
            circuit.Connection,
            options.SpliceCost,
            options.TerminalCapacity,
            circuit.Connection == CircuitConnection.AtJunctionBox ? boxes : null,
            options.JoinTolerance);

        var tree = CableTree.Search(allowed, circuit, options, rules);

        if (tree.Status != RouteStatus.Found)
        {
            // "Nothing within reach" and "nothing within reach that will take this cable" send a
            // designer to two different places, so the true one is worth asking for. Reach rather
            // than a second search: the question is only whether the structure is there at all, and
            // it is asked on the failure alone, and only when something was actually filtered out.
            var status = tree.Status == RouteStatus.NoCarrierNear
                         && !ReferenceEquals(allowed, network)
                         && ReachesTheStructure(network, circuit, options)
                ? RouteStatus.NoCarrierAllowed
                : tree.Status;

            // The circuit's own number in front of the end that stopped it. Measured on the first
            // real run: the screen groups by cause and says "26 circuits", then lists addresses that
            // are devices - two different levels, so the list answers a question nobody asked and
            // leaves the circuits unnamed.
            return new RouteResult(circuit.Id, status, network.Version)
            {
                BlockedAt = circuit.Number + " - " + tree.BlockedAt,
                BuiltInLength = circuit.BuiltInLength,

                // Carried on a failure too: the screen counts which groups a run involved at all, and
                // a circuit turned away for its group is exactly the one that must not be missing
                // from that count.
                CableGroup = circuit.CableGroup,
            };
        }

        return new RouteResult(circuit.Id, RouteStatus.Found, network.Version)
        {
            Path = tree.Carriers,
            AlongCarriers = tree.AlongCarriers,
            AlongByClass = tree.AlongByClass,
            Approaches = tree.Approaches,
            BuiltInLength = circuit.BuiltInLength,
            Connection = circuit.Connection,
            Conductors = circuit.Conductors,
            CableGroup = circuit.CableGroup,
            Taps = tree.Taps,
            Branches = tree.Branches,
        };
    }

    /// <summary>Whether the carrier a tap sits on is one cable may be spliced in.</summary>
    /// <remarks>
    /// A carrier the network does not know cannot say, and says no: the loud answer, for the same
    /// reason <see cref="CarrierNode.AllowsSplicing"/> defaults to it.
    /// </remarks>
    internal static bool Splices(RouteNetwork network, CarrierId carrier) =>
        network.Node(carrier)?.AllowsSplicing ?? false;

    /// <summary>Whether every end of the circuit has some carrier within reach, permission aside.</summary>
    /// <remarks>
    /// Asked of the whole structure, to tell a circuit that has nowhere to go from one that has
    /// somewhere it is not allowed. It answers about reach alone and never about a path: a circuit
    /// whose ends both reach carriers that do not join up is a different failure, and the search has
    /// already named it by the time this is asked.
    /// </remarks>
    private static bool ReachesTheStructure(
        RouteNetwork network,
        CircuitSnapshot circuit,
        RoutingOptions options)
    {
        if (Approachable(network, circuit.Source, options).Count == 0)
            return false;

        foreach (var device in circuit.Devices)
        {
            if (Approachable(network, device, options).Count == 0)
                return false;
        }

        return true;
    }

    /// <summary>The carriers a terminal can reach, where it meets each one, and what that costs.</summary>
    /// <remarks>
    /// <b>Where, and not only how far.</b> A tray is open along its length, so a cable joins it at
    /// the point nearest the device; a conduit is a pipe and is joined where it ends. The old form
    /// measured to the nearest terminal of everything, which put a socket under the middle of a
    /// twenty-metre tray eleven metres from a run it was one metre below.
    /// </remarks>
    internal static Dictionary<CarrierId, (Point3 At, double Cost)> Approachable(
        RouteNetwork network,
        Terminal terminal,
        RoutingOptions options)
    {
        var found = new Dictionary<CarrierId, (Point3 At, double Cost)>();

        foreach (var node in network.Near(terminal.At))
        {
            var at = node.OpenAlongItsLength
                ? node.NearestPointTo(terminal.At)
                : NearestTerminal(node, terminal.At);

            var distance = Approach(terminal.At, at, options);

            if (distance > options.MaxApproach)
                continue;

            if (!found.TryGetValue(node.Id, out var known) || distance < known.Cost)
                found[node.Id] = (at, distance);
        }

        return found;
    }

    /// <summary>The terminal of this carrier nearest a point, whatever the distance.</summary>
    private static Point3 NearestTerminal(CarrierNode node, Point3 at)
    {
        var best = node.Terminals.Count > 0 ? node.Terminals[0] : node.Start;
        var distance = at.DistanceTo(best);

        for (var i = 1; i < node.Terminals.Count; i++)
        {
            var candidate = at.DistanceTo(node.Terminals[i]);

            if (candidate >= distance)
                continue;

            distance = candidate;
            best = node.Terminals[i];
        }

        return best;
    }

    /// <summary>Which terminal of a carrier meets this point, or -1 when none is within reach.</summary>
    internal static int Touching(CarrierNode node, Point3 at, double tolerance)
    {
        var best = -1;
        var distance = double.MaxValue;

        for (var i = 0; i < node.Terminals.Count; i++)
        {
            var candidate = at.DistanceTo(node.Terminals[i]);

            if (candidate > tolerance || candidate >= distance)
                continue;

            distance = candidate;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// How far a cable travels between a device and the structure.
    /// </summary>
    /// <remarks>
    /// One definition, shared with <see cref="ApproachStudy"/>, which exists to question this one.
    /// Two copies would let the study measure something the search does not do - the one way a
    /// measurement can be worse than none.
    /// </remarks>
    private static double Approach(Point3 from, Point3 to, RoutingOptions options) =>
        Approaches.Measure(from, to, options);

    /// <summary>
    /// How much of a carrier lies between two points on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scaled to the length the model reports rather than taken off the geometry.</b> Revit says
    /// what a run is, and that number is the one that gets ordered and cut; the endpoints are where
    /// its connectors sit, and the two need not agree to the millimetre. Taking the fraction and
    /// applying it to the reported length keeps a whole traversal exactly equal to that length,
    /// which is what every check here and every schedule already expects.
    /// </para>
    /// <para>
    /// A fitting is not a straight anything - its body turns - so passing through one costs what it
    /// says it is, and entering and leaving by the same connector costs nothing.
    /// </para>
    /// </remarks>
    internal static double Along(CarrierNode node, Point3 a, Point3 b)
    {
        if (node.Kind != CarrierKind.Segment)
            return a.DistanceTo(b) <= 1e-9 ? 0 : node.Length;

        var span = node.Start.DistanceTo(node.End);

        return span <= 1e-9 ? node.Length : a.DistanceTo(b) * (node.Length / span);
    }

    /// <summary>
    /// What a foot of this carrier costs the search, as against what it measures.
    /// </summary>
    /// <remarks>
    /// The preference is expressed on the class of the carrier rather than on the route as a whole,
    /// which is the correction to the predecessor: it multiplied the finished length, so a route
    /// through both conduit and tray was penalised as though all of it were tray.
    /// </remarks>
    internal static double Factor(CarrierNode node, RoutingOptions options) =>
        string.Equals(node.Class, "conduit", StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : 1.0 + options.PreferConduitUntil;

    private static RouteResult Blocked(RouteStatus status, Terminal at) =>
        new(default, status, 0)
        {
            BlockedAt = at.Label,
        };
}

/// <summary>A binary heap, because the search is the hot loop and the framework has no such thing on net48.</summary>
/// <remarks>
/// <c>System.Collections.Generic.PriorityQueue</c> arrived in .NET 6 and this assembly also targets
/// net48, where a sorted list or a linear scan would put the cost back into the inner loop. Thirty
/// lines here rather than a package: the metric that matters on net48 is assemblies somebody else
/// may also ship, and this one ships none.
/// </remarks>
internal sealed class PriorityQueue
{
    private readonly List<(Port Item, double Cost)> _heap = new();

    public void Push(Port item, double cost)
    {
        _heap.Add((item, cost));

        var child = _heap.Count - 1;

        while (child > 0)
        {
            var parent = (child - 1) / 2;

            if (_heap[parent].Cost <= _heap[child].Cost)
                break;

            (_heap[parent], _heap[child]) = (_heap[child], _heap[parent]);
            child = parent;
        }
    }

    public bool TryPop(out Port item, out double cost)
    {
        if (_heap.Count == 0)
        {
            item = default;
            cost = 0;
            return false;
        }

        (item, cost) = _heap[0];
        _heap[0] = _heap[_heap.Count - 1];
        _heap.RemoveAt(_heap.Count - 1);

        var parent = 0;

        while (true)
        {
            var left = (2 * parent) + 1;
            var right = left + 1;
            var smallest = parent;

            if (left < _heap.Count && _heap[left].Cost < _heap[smallest].Cost)
                smallest = left;

            if (right < _heap.Count && _heap[right].Cost < _heap[smallest].Cost)
                smallest = right;

            if (smallest == parent)
                return true;

            (_heap[parent], _heap[smallest]) = (_heap[smallest], _heap[parent]);
            parent = smallest;
        }
    }
}
