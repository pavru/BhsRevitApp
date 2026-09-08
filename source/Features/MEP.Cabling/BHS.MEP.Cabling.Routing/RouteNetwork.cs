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

    public RouteNetwork(
        long version,
        IReadOnlyCollection<CarrierNode> nodes,
        IReadOnlyDictionary<CarrierId, IReadOnlyList<CarrierId>> adjacency)
    {
        Version = version;
        _nodes = nodes.ToDictionary(one => one.Id);
        _adjacency = adjacency.ToDictionary(one => one.Key, one => one.Value);
    }

    /// <summary>Monotonic, per document. Compared, never interpreted.</summary>
    public long Version { get; }

    public int Count => _nodes.Count;

    public IEnumerable<CarrierNode> Nodes => _nodes.Values;

    public CarrierNode? Node(CarrierId id) => _nodes.TryGetValue(id, out var node) ? node : null;

    public IReadOnlyList<CarrierId> Neighbours(CarrierId id) =>
        _adjacency.TryGetValue(id, out var next) ? next : Array.Empty<CarrierId>();
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
}
