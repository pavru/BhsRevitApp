namespace BHS.MEP.Cabling.Routing;

/// <summary>A junction box that already stands in the model, joined to the structure.</summary>
/// <remarks>
/// The owner's definition of a real box - a fitting that is a box by its role and is connected to
/// the carrier network. Which elements qualify is the Revit side's question; the planner only needs
/// to know where they are.
/// </remarks>
public sealed class ExistingBox
{
    public ExistingBox(CarrierId id, Point3 at)
    {
        Id = id;
        At = at;
    }

    public CarrierId Id { get; }

    public Point3 At { get; }
}

/// <summary>One box the calculation needs: an existing one it uses, or a place it recommends.</summary>
public sealed class PlannedBox
{
    internal PlannedBox(Point3 at, ExistingBox? existing)
    {
        At = at;
        Existing = existing;
    }

    /// <summary>Where it stands - the existing box's own point, or the first tap that asked for it.</summary>
    /// <remarks>
    /// A tap point rather than an average of the taps merged into it, and on purpose: every tap lies
    /// on the structure, and the mean of two points on two different carriers can lie on neither -
    /// an indicator floating beside the tray it is supposed to be on.
    /// </remarks>
    public Point3 At { get; }

    /// <summary>The box already in the model, or nothing when this is a recommendation.</summary>
    public ExistingBox? Existing { get; }

    /// <summary>Whether an indicator has to be placed for it.</summary>
    public bool IsRecommendation => Existing is null;

    /// <summary>Every circuit passing through, in the order they were first seen here.</summary>
    public IReadOnlyList<CarrierId> Circuits => _circuits;

    /// <summary>How many spurs leave it for devices.</summary>
    public int Spurs { get; private set; }

    /// <summary>
    /// Every cable entry: trunk in, trunk out, and each spur - the owner's answer of 2026-09-11.
    /// </summary>
    /// <remarks>
    /// What the designer picks a real box by, so it counts what the box has to take rather than what
    /// the device needs: an intermediate box with one device is three, the last box of a circuit is
    /// two, and a box shared by two circuits takes the sum.
    /// </remarks>
    public int Entries { get; private set; }

    /// <summary>The taps it serves, in the order they arrived.</summary>
    public IReadOnlyList<Tap> Taps => _taps;

    private readonly List<CarrierId> _circuits = new();
    private readonly List<Tap> _taps = new();

    internal void Serve(CarrierId circuit, Tap tap)
    {
        if (!_circuits.Contains(circuit))
            _circuits.Add(circuit);

        _taps.Add(tap);
        Spurs++;
        Entries++;
    }

    internal void Trunk() => Entries++;
}

/// <summary>
/// Turns the taps of circuits cut in boxes into the boxes they need.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule here is the owner's, answered on 2026-09-11 before a line of it was written.</b> A
/// box at every device, the last one included. One box per place, shared by every circuit passing
/// through it. Its count is every cable entry, not only the spurs. And one distance - the box radius,
/// a project setting - decides both questions of nearness: taps closer than it share a box, and an
/// existing box closer than it is used instead of recommending another.
/// </para>
/// <para>
/// <b>Deterministic, because it is run again and again.</b> The same model gives the same boxes in
/// the same places: existing boxes first, then the circuits in the order given and the taps in the
/// order each circuit visits them, each joining the nearest box within the radius or opening its own.
/// A planner that moved an indicator between two runs of an unchanged model would make the designer
/// chase something that is not there.
/// </para>
/// <para>
/// <b>The trunk length does not move when taps merge,</b> and that is a stated approximation rather
/// than an oversight: the search measured the trunk to each tap, and a merged box stands at most one
/// radius from a tap it serves. Recomputing the route to the merged point would make the length
/// depend on the planner, which is the wrong way round.
/// </para>
/// </remarks>
public static class BoxPlanner
{
    /// <param name="routes">Every route; only those cut in boxes and found are planned.</param>
    /// <param name="existing">Boxes already in the model and joined to the structure.</param>
    /// <param name="radius">The box radius, in internal feet.</param>
    public static IReadOnlyList<PlannedBox> Plan(
        IEnumerable<RouteResult> routes,
        IEnumerable<ExistingBox> existing,
        double radius)
    {
        var boxes = new List<PlannedBox>();

        foreach (var box in existing ?? Array.Empty<ExistingBox>())
        {
            if (box is not null)
                boxes.Add(new PlannedBox(box.At, box));
        }

        foreach (var route in routes ?? Array.Empty<RouteResult>())
        {
            if (route is null
                || route.Status != RouteStatus.Found
                || route.Connection != CircuitConnection.AtJunctionBox
                || route.Taps.Count == 0)
            {
                continue;
            }

            PlannedBox? previous = null;

            foreach (var tap in route.Taps)
            {
                var box = Nearest(boxes, tap.At, radius);

                if (box is null)
                {
                    box = new PlannedBox(tap.At, null);
                    boxes.Add(box);
                }

                // The trunk enters a box when it arrives from somewhere else - from the panel for the
                // first, from a different box after that - and leaves the one it came from. Two taps of
                // one circuit that share a box put no trunk between them: the cable never left.
                if (!ReferenceEquals(box, previous))
                {
                    box.Trunk();
                    previous?.Trunk();
                }

                box.Serve(route.Circuit, tap);
                previous = box;
            }
        }

        // Existing boxes nobody used are not part of the answer; they were only offered.
        return boxes.FindAll(box => box.Spurs > 0);
    }

    private static PlannedBox? Nearest(List<PlannedBox> boxes, Point3 at, double radius)
    {
        PlannedBox? best = null;
        var distance = double.MaxValue;

        foreach (var box in boxes)
        {
            var candidate = box.At.DistanceTo(at);

            if (candidate > radius || candidate >= distance)
                continue;

            distance = candidate;
            best = box;
        }

        return best;
    }
}
