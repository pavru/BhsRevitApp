namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// The cable-bearing structure of one model, as a snapshot that can be searched off the API thread.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and built entirely by the side that has a document. Nothing here may be lazy: a field
/// left to be fetched later is a Revit API call on a background thread, which is the failure this
/// whole assembly exists to make impossible.
/// </para>
/// <para>
/// <b><see cref="Version"/> is what makes an updater possible later rather than a rewrite.</b> Every
/// result carries the version it was computed on, so "this answer is older than the model" is a
/// comparison instead of a guess. Adding it afterwards would mean migrating models that already hold
/// results.
/// </para>
/// </remarks>
public sealed class RouteNetwork
{
    private readonly Dictionary<CarrierId, CarrierNode> _nodes;
    private readonly Dictionary<CarrierId, IReadOnlyList<CarrierId>> _adjacency;
    private readonly CarrierNode[] _byIndex;
    private readonly SpatialIndex _index;

    public RouteNetwork(
        long version,
        IReadOnlyCollection<CarrierNode> nodes,
        IReadOnlyDictionary<CarrierId, IReadOnlyList<CarrierId>> adjacency,
        double approachRadius)
    {
        Version = version;
        _nodes = nodes.ToDictionary(one => one.Id);
        _adjacency = adjacency.ToDictionary(one => one.Key, one => one.Value);
        _byIndex = nodes.ToArray();

        // Sized for the question the router asks - "what is within reach of this device" - which is
        // a different radius from the one that decided adjacency. Both are grids; keeping them
        // apart costs one array and stops each from answering the other's question badly.
        var cell = Math.Max(approachRadius, 1e-6);
        _index = new SpatialIndex(cell);

        for (var i = 0; i < _byIndex.Length; i++)
            _index.AddAll(i, Occupies(_byIndex[i], cell));
    }

    /// <summary>Monotonic, per document. Compared, never interpreted.</summary>
    public long Version { get; }

    public int Count => _nodes.Count;

    public IEnumerable<CarrierNode> Nodes => _nodes.Values;

    public CarrierNode? Node(CarrierId id) => _nodes.TryGetValue(id, out var node) ? node : null;

    public IReadOnlyList<CarrierId> Neighbours(CarrierId id) =>
        _adjacency.TryGetValue(id, out var next) ? next : Array.Empty<CarrierId>();

    /// <summary>
    /// How many separate pieces the structure falls into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written because a real model answered "no connectivity" for twenty-six circuits out of
    /// fifty-five, and the status alone cannot say whose fault that is.</b> Both ends reached the
    /// structure and nothing joined them - which is either a structure genuinely drawn in pieces, or
    /// a join tolerance too small to close gaps a person reads as joints. Those two want opposite
    /// actions, and guessing between them costs a press of the button each time.
    /// </para>
    /// <para>
    /// One walk over the graph answers it. A hundred groups is a tolerance; two is a model with a
    /// riser nobody drew.
    /// </para>
    /// </remarks>
    public NetworkShape Shape()
    {
        var seen = new HashSet<CarrierId>();
        var groups = 0;
        var largest = 0;
        var junctions = 0;
        var stack = new Stack<CarrierId>();

        foreach (var node in _nodes.Values)
        {
            if (node.Terminals.Count > 2)
                junctions++;
        }

        foreach (var start in _nodes.Keys)
        {
            if (!seen.Add(start))
                continue;

            groups++;
            var size = 1;
            stack.Push(start);

            while (stack.Count > 0)
            {
                foreach (var next in Neighbours(stack.Pop()))
                {
                    if (!seen.Add(next))
                        continue;

                    size++;
                    stack.Push(next);
                }
            }

            if (size > largest)
                largest = size;
        }

        return new NetworkShape(_nodes.Count, groups, largest, junctions);
    }

    /// <summary>The carriers that pass near a point.</summary>
    /// <remarks>
    /// <b>Not a convenience.</b> Without it the router asks every terminal about every carrier, and
    /// a run is circuits times devices times carriers - hundreds by tens by thousands. The index was
    /// built to keep adjacency out of quadratic time and would have left the router in it.
    /// </remarks>
    public IEnumerable<CarrierNode> Near(Point3 at)
    {
        foreach (var found in _index.Near(at))
            yield return _byIndex[found];
    }

    /// <summary>
    /// Where a carrier can be met: its terminals, and for an open run the length between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Filing a run under its ends alone hides its middle, and that was measured rather than
    /// suspected.</b> A twenty-metre tray whose middle is over a socket sits in cells ten metres
    /// away, so a query standing under it finds nothing - which is why <see cref="ApproachStudy"/>
    /// had to compare by brute force to say anything at all about the difference.
    /// </para>
    /// <para>
    /// <b>The extra cells are not overhead; they are the cells the tray is in.</b> A carrier that
    /// crosses ten cells is met in ten cells, and listing it in one of them was the defect. The
    /// count is proportional to length over cell size, which is the smallest a truthful answer can
    /// be.
    /// </para>
    /// <para>
    /// Only along an <see cref="CarrierNode.OpenAlongItsLength"/> run: a cable cannot leave a pipe
    /// mid-run, so filing a conduit's middle would offer the router an entry it must refuse - and an
    /// offer refused later is work done twice.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Point3> Occupies(CarrierNode node, double cell)
    {
        if (node.Kind != CarrierKind.Segment || !node.OpenAlongItsLength)
            return node.Terminals;

        var span = node.Start.DistanceTo(node.End);

        if (span <= cell)
            return node.Terminals;

        var points = new List<Point3>(node.Terminals);
        var steps = (int)Math.Ceiling(span / cell);

        for (var i = 1; i < steps; i++)
        {
            var t = i / (double)steps;

            points.Add(new Point3(
                node.Start.X + ((node.End.X - node.Start.X) * t),
                node.Start.Y + ((node.End.Y - node.Start.Y) * t),
                node.Start.Z + ((node.End.Z - node.Start.Z) * t)));
        }

        return points;
    }
}

/// <summary>One end of a circuit: where a cable has to reach.</summary>
/// <remarks>
/// <b>The connector's point, not the element's location.</b> A panel's location is its insertion
/// point, which can be metres from the terminal a cable actually lands on, and the difference is the
/// whole reason a route looks wrong on a plan. The predecessor measured to the element and carried a
/// tolerance to absorb it.
/// </remarks>
public sealed class Terminal
{
    public Terminal(CarrierId owner, Point3 at, string label)
    {
        Owner = owner;
        At = at;
        Label = label;
    }

    /// <summary>The element the cable lands on - a panel or a device.</summary>
    public CarrierId Owner { get; }

    public Point3 At { get; }

    /// <summary>What to call it on screen, carried because asking the model is an API call.</summary>
    public string Label { get; }
}

/// <summary>How the devices of a circuit are connected to its cable.</summary>
/// <remarks>
/// <para>
/// <b>Told by the owner, not measured, and the difference is a fifth of the headline length.</b> A
/// device is either connected with the cable cut at its terminal - a doubled cable comes down to it
/// and a new run leaves - or with a single trunk cut in a junction box, from which only a spur
/// descends. On the first real model the drops were 38 % of the total, and the second way counts
/// each of them once where the first counts it twice.
/// </para>
/// <para>
/// A property of the circuit, inherited from its panel: a power panel does all of its circuits
/// through boxes, an RS485 panel through terminals. Where it is read from is the Revit side's
/// business; the search only needs the answer.
/// </para>
/// </remarks>
public enum CircuitConnection
{
    /// <summary>The cable is cut at each device: every intermediate drop is walked down and back up.</summary>
    AtTerminal,

    /// <summary>
    /// One trunk along the structure, a box at every device - the last one included, the owner's
    /// answer of 2026-09-11 - and a single spur from each box down to its device.
    /// </summary>
    AtJunctionBox,
}

/// <summary>A circuit to route, with everything the search needs and nothing it has to ask for.</summary>
/// <remarks>
/// Circuits are not part of <see cref="RouteNetwork"/> on purpose: the network is the structure of
/// the model and changes when somebody moves a tray, while circuits change when somebody rewires.
/// They have different lifetimes, and an updater will invalidate them separately.
/// </remarks>
public sealed class CircuitSnapshot
{
    public CircuitSnapshot(CarrierId id, string number, Terminal source, IReadOnlyList<Terminal> devices)
    {
        Id = id;
        Number = number;
        Source = source;
        Devices = devices;
    }

    public CarrierId Id { get; }

    /// <summary>The circuit number as a person reads it - "ЩО-1, гр. 7".</summary>
    public string Number { get; }

    /// <summary>The panel end.</summary>
    public Terminal Source { get; }

    /// <summary>The devices, in the order the circuit visits them.</summary>
    public IReadOnlyList<Terminal> Devices { get; }

    /// <summary>The length Revit itself reports today, for the comparison the result screen shows.</summary>
    public double BuiltInLength { get; init; }

    /// <summary>Whether Revit is currently walking a path somebody set by hand.</summary>
    /// <remarks>
    /// <b>Not on its own the sign of somebody else's work.</b> After our own first run our circuits
    /// are in the custom mode too, so "custom" alone would forbid us from recomputing what we
    /// ourselves wrote. The test is custom <i>and</i> no route id of ours - which is why
    /// <see cref="OurRouteId"/> travels beside it.
    /// </remarks>
    public bool HasCustomPath { get; init; }

    /// <summary>The route id we wrote last time, empty when we never did.</summary>
    public string OurRouteId { get; init; } = string.Empty;

    /// <summary>Cross-section of the cable, for the fill calculation. Zero when unknown.</summary>
    public double CableArea { get; init; }

    /// <summary>How its devices are connected. Terminal unless somebody said otherwise.</summary>
    /// <remarks>
    /// Terminal is the default because it is what the search did before the question was asked, and
    /// a default that changed the headline number of every existing model would be a change nobody
    /// decided. The owner's settings and parameters say otherwise per project and per panel.
    /// </remarks>
    public CircuitConnection Connection { get; init; } = CircuitConnection.AtTerminal;
}

/// <summary>What the structure looks like as a graph, for when a search says it could not cross it.</summary>
public readonly struct NetworkShape
{
    public NetworkShape(int carriers, int groups, int largest, int junctions = 0)
    {
        Carriers = carriers;
        Groups = groups;
        Largest = largest;
        Junctions = junctions;
    }

    /// <summary>Every carrier that was read.</summary>
    public int Carriers { get; }

    /// <summary>How many disconnected pieces they form.</summary>
    public int Groups { get; }

    /// <summary>How many carriers are in the biggest of those pieces.</summary>
    /// <remarks>
    /// The number that decides what to do. One group holding almost everything means the structure
    /// is continuous and a few strays are loose; groups all of a similar small size means nothing is
    /// joined to anything, which is a tolerance rather than a model.
    /// </remarks>
    public int Largest { get; }

    /// <summary>Carriers with more than two points at which something can join them.</summary>
    /// <remarks>
    /// <b>Evidence for one particular defect, kept because that defect was expensive.</b> A carrier
    /// was once described by two points only, so every tee lost its branch and every cross lost two.
    /// A model with many junctions and a badly fragmented structure is the signature; a model with
    /// none says the fragmentation is somewhere else entirely.
    /// </remarks>
    public int Junctions { get; }
}
