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
    public static RouteResult Route(RouteNetwork network, CircuitSnapshot circuit, RoutingOptions options)
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

        // A circuit is a chain: panel to the first device, then device to device. Each leg is routed
        // on its own and the legs are concatenated, which is what the predecessor did and is right -
        // the order is the electrician's, not the search's, and reordering it would silently produce
        // a route nobody wired.
        var path = new List<CarrierId>();
        var alongCarriers = 0.0;
        var approaches = 0.0;
        var from = circuit.Source;

        foreach (var to in circuit.Devices)
        {
            var leg = Leg(network, from, to, options);

            if (leg.Status != RouteStatus.Found)
            {
                // The circuit's own number in front of the end that stopped it. Measured on the
                // first real run: the screen groups by cause and says "26 circuits", then lists
                // addresses that are devices - two different levels, so the list answers a question
                // nobody asked and leaves the circuits unnamed. A leg does not know which circuit it
                // belongs to; this is the only place that does.
                return new RouteResult(circuit.Id, leg.Status, network.Version)
                {
                    BlockedAt = circuit.Number + " - " + leg.BlockedAt,
                    BuiltInLength = circuit.BuiltInLength,
                };
            }

            // A leg that doubles back over carriers the previous leg already used is normal - two
            // sockets on one tray share it - and the length is counted once per leg because the
            // cable runs the distance once per leg. The path is deduplicated only for display.
            foreach (var step in leg.Path)
            {
                if (path.Count == 0 || path[path.Count - 1] != step)
                    path.Add(step);
            }

            alongCarriers += leg.AlongCarriers;
            approaches += leg.Approaches;
            from = to;
        }

        var total = alongCarriers + approaches;

        return new RouteResult(circuit.Id, RouteStatus.Found, network.Version)
        {
            Path = path,
            AlongCarriers = alongCarriers + (total * options.LengthExtend),
            Approaches = approaches,
            BuiltInLength = circuit.BuiltInLength,
        };
    }

    /// <summary>One leg: from one terminal to the next, through the structure.</summary>
    /// <remarks>
    /// <para>
    /// <b>The vertex is a terminal of a carrier, not the carrier.</b> With carriers as vertices a
    /// carrier has one cost, so every route that touches it pays its whole length - which was
    /// deliberate and guarded, because the alternative at the time was paying nothing at all. But a
    /// cable that joins a twenty-foot tray at its middle and leaves at one end walks ten feet, and a
    /// single number per carrier cannot say ten to one route and twenty to another.
    /// </para>
    /// <para>
    /// So a carrier is entered at a point and left at a terminal, and what it costs is the distance
    /// between those two. Carriers that touch are joined by an edge of nothing, which is what
    /// touching means. The graph grows by the number of terminals - two to four apiece - and answers
    /// a question the old one could only approximate.
    /// </para>
    /// <para>
    /// <b>And the finish is taken on the way in, never on the way out.</b> A device hanging under a
    /// carrier is reached from the point where the cable enters that carrier; asking at the terminal
    /// we arrived at would walk the carrier to its end and then back down it.
    /// </para>
    /// </remarks>
    private static RouteResult Leg(RouteNetwork network, Terminal from, Terminal to, RoutingOptions options)
    {
        var entries = Approachable(network, from, options);

        if (entries.Count == 0)
            return Blocked(RouteStatus.NoCarrierNear, from);

        var exits = Approachable(network, to, options);

        if (exits.Count == 0)
            return Blocked(RouteStatus.NoCarrierNear, to);

        var best = new Dictionary<Port, double>();
        var came = new Dictionary<Port, Port>();
        var entered = new Dictionary<Port, Point3>();
        var queue = new PriorityQueue();

        var finish = double.MaxValue;
        var finishAt = default(CarrierId);
        var finishFrom = default(Port);
        var finishSeeded = false;
        var finishEntry = default(Point3);

        void Arrive(CarrierNode node, Point3 at, double before, Port previous, bool hasPrevious)
        {
            var factor = Factor(node, options);

            if (exits.TryGetValue(node.Id, out var exit))
            {
                var whole = before + (Along(node, at, exit.At) * factor) + exit.Cost;

                if (whole < finish)
                {
                    finish = whole;
                    finishAt = node.Id;
                    finishFrom = previous;
                    finishSeeded = !hasPrevious;
                    finishEntry = at;
                }
            }

            for (var t = 0; t < node.Terminals.Count; t++)
            {
                var port = new Port(node.Id, t);
                var cost = before + (Along(node, at, node.Terminals[t]) * factor);

                if (best.TryGetValue(port, out var known) && known <= cost)
                    continue;

                best[port] = cost;
                entered[port] = at;

                if (hasPrevious)
                    came[port] = previous;
                else
                    came.Remove(port);

                queue.Push(port, cost);
            }
        }

        foreach (var entry in entries)
        {
            if (network.Node(entry.Key) is { } node)
                Arrive(node, entry.Value.At, entry.Value.Cost, default, false);
        }

        var settled = new HashSet<Port>();

        while (queue.TryPop(out var port, out var cost))
        {
            if (!settled.Add(port) || cost >= finish)
                continue;

            if (network.Node(port.Carrier) is not { } node)
                continue;

            var reached = node.Terminals[port.Terminal];

            foreach (var next in network.Neighbours(port.Carrier))
            {
                if (network.Node(next) is not { } other)
                    continue;

                // Which terminal of the neighbour this one meets. Adjacency says the two carriers
                // touch somewhere; the search needs to know where, because that is where the cable
                // enters and what it then has to walk is measured from it.
                var touch = Touching(other, reached, options.JoinTolerance);

                if (touch >= 0)
                    Arrive(other, other.Terminals[touch], cost, port, true);
            }
        }

        if (finish >= double.MaxValue)
            return Blocked(RouteStatus.NoConnectivity, to);

        var carriers = new List<CarrierId>();

        // The true length, walked again over the chosen path - not the cost the search minimised.
        // The two differ by the conduit preference, which is a thumb on the scale for choosing a
        // route and has no business in the number we write into a parameter. Reporting the cost
        // would inflate every tray route by the preference and quietly disagree with a tape measure.
        var length = 0.0;

        if (!finishSeeded)
        {
            var walk = new List<Port>();
            var cursor = finishFrom;

            while (true)
            {
                walk.Add(cursor);

                if (!came.TryGetValue(cursor, out var previous))
                    break;

                cursor = previous;
            }

            walk.Reverse();

            foreach (var step in walk)
            {
                if (network.Node(step.Carrier) is not { } node)
                    continue;

                carriers.Add(step.Carrier);
                length += Along(node, entered[step], node.Terminals[step.Terminal]);
            }
        }

        if (network.Node(finishAt) is { } last)
        {
            carriers.Add(finishAt);
            length += Along(last, finishEntry, exits[finishAt].At);
        }

        var seed = carriers.Count > 0 ? carriers[0] : finishAt;
        var approach = entries.TryGetValue(seed, out var seeded) ? seeded.Cost : 0;

        return new RouteResult(default, RouteStatus.Found, network.Version)
        {
            Path = carriers,
            AlongCarriers = length,
            Approaches = approach + exits[finishAt].Cost,
        };
    }

    /// <summary>The carriers a terminal can reach, where it meets each one, and what that costs.</summary>
    /// <remarks>
    /// <b>Where, and not only how far.</b> A tray is open along its length, so a cable joins it at
    /// the point nearest the device; a conduit is a pipe and is joined where it ends. The old form
    /// measured to the nearest terminal of everything, which put a socket under the middle of a
    /// twenty-metre tray eleven metres from a run it was one metre below.
    /// </remarks>
    private static Dictionary<CarrierId, (Point3 At, double Cost)> Approachable(
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
    private static int Touching(CarrierNode node, Point3 at, double tolerance)
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
    private static double Along(CarrierNode node, Point3 a, Point3 b)
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
    private static double Factor(CarrierNode node, RoutingOptions options) =>
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
