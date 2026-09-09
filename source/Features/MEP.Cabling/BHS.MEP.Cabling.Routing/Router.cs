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
    private static RouteResult Leg(RouteNetwork network, Terminal from, Terminal to, RoutingOptions options)
    {
        var entries = Reachable(network, from, options);

        if (entries.Count == 0)
            return Blocked(RouteStatus.NoCarrierNear, from);

        var exits = Reachable(network, to, options);

        if (exits.Count == 0)
            return Blocked(RouteStatus.NoCarrierNear, to);

        // Every entry is a start, seeded with the approach to it PLUS the cost of walking it. The
        // second half was missing at first and the probe caught it on its first run: without it, a
        // route that enters and leaves the same carrier never pays for that carrier at all, so a
        // 24-foot conduit and a 20-foot tray between the same two points cost exactly the same and
        // the search picked whichever the heap happened to pop. Every carrier on a path now
        // contributes its weight exactly once, wherever on the path it sits.
        var best = new Dictionary<CarrierId, double>();
        var came = new Dictionary<CarrierId, CarrierId>();
        var queue = new PriorityQueue();

        foreach (var entry in entries)
        {
            var node = network.Node(entry.Key);

            if (node is null)
                continue;

            var seeded = entry.Value + Weight(node, options);
            best[entry.Key] = seeded;
            queue.Push(entry.Key, seeded);
        }

        var settled = new HashSet<CarrierId>();

        while (queue.TryPop(out var at, out var cost))
        {
            if (!settled.Add(at))
                continue;

            if (exits.TryGetValue(at, out var exitCost))
            {
                var path = Unwind(came, at);

                // The true length, walked again over the chosen path - not the cost the search
                // minimised. The two differ by the conduit preference, which is a thumb on the
                // scale for choosing a route and has no business in the number we write into a
                // parameter. Reporting the cost would inflate every tray route by the preference
                // and quietly disagree with a tape measure.
                var length = 0.0;

                foreach (var step in path)
                {
                    var node = network.Node(step);

                    if (node is not null)
                        length += node.Length;
                }

                entries.TryGetValue(path[0], out var entryCost);

                return new RouteResult(default, RouteStatus.Found, network.Version)
                {
                    Path = path,
                    AlongCarriers = length,
                    Approaches = entryCost + exitCost,
                };
            }

            foreach (var next in network.Neighbours(at))
            {
                if (settled.Contains(next))
                    continue;

                var node = network.Node(next);

                if (node is null)
                    continue;

                var step = cost + Weight(node, options);

                if (best.TryGetValue(next, out var known) && known <= step)
                    continue;

                best[next] = step;
                came[next] = at;
                queue.Push(next, step);
            }
        }

        return Blocked(RouteStatus.NoConnectivity, to);
    }

    /// <summary>The carriers a terminal can reach directly, and what reaching each one costs.</summary>
    private static Dictionary<CarrierId, double> Reachable(
        RouteNetwork network,
        Terminal terminal,
        RoutingOptions options)
    {
        var found = new Dictionary<CarrierId, double>();

        foreach (var node in network.Near(terminal.At))
        {
            var distance = Math.Min(Approach(terminal.At, node.Start, options), Approach(terminal.At, node.End, options));

            if (distance > options.MaxApproach)
                continue;

            if (!found.TryGetValue(node.Id, out var known) || distance < known)
                found[node.Id] = distance;
        }

        return found;
    }

    /// <summary>
    /// How far a cable actually travels between a device and the structure.
    /// </summary>
    /// <remarks>
    /// Along the axes by default, because a cable does not fly: it runs across and then down. The
    /// straight line is shorter, always available, and wrong by however much the two differ - which
    /// on a drop from a ceiling tray to a socket is most of the number.
    /// </remarks>
    private static double Approach(Point3 from, Point3 to, RoutingOptions options) =>
        options.AxisAlignedApproach
            ? Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) + Math.Abs(from.Z - to.Z)
            : from.DistanceTo(to);

    /// <summary>
    /// What walking one carrier costs.
    /// </summary>
    /// <remarks>
    /// Its length, made more expensive when the preference says so. The preference is expressed on
    /// the carrier's class rather than on the route as a whole, which is the correction to the
    /// predecessor: it multiplied the finished route's length, so a route through both conduit and
    /// tray was penalised as though all of it were tray.
    /// </remarks>
    private static double Weight(CarrierNode node, RoutingOptions options) =>
        string.Equals(node.Class, "conduit", StringComparison.OrdinalIgnoreCase)
            ? node.Length
            : node.Length * (1.0 + options.PreferConduitUntil);

    private static IReadOnlyList<CarrierId> Unwind(Dictionary<CarrierId, CarrierId> came, CarrierId at)
    {
        var path = new List<CarrierId> { at };

        while (came.TryGetValue(at, out var previous))
        {
            at = previous;
            path.Add(at);
        }

        path.Reverse();
        return path;
    }

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
    private readonly List<(CarrierId Item, double Cost)> _heap = new();

    public void Push(CarrierId item, double cost)
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

    public bool TryPop(out CarrierId item, out double cost)
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
