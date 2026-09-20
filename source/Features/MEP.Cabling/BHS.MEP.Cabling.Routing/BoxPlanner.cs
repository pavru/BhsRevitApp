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

/// <summary>A device served by a splice made in the carrier itself, with no junction box.</summary>
/// <remarks>
/// <para>
/// <b>The owner's rule of 2026-09-20: where the carrier allows splicing, that is where the cable
/// graph branches</b> - inside a trunking with a removable cover, inside a tee somebody declared a
/// splicing place. Nothing is recommended there and nothing is placed; the element is already in the
/// model and already on the route.
/// </para>
/// <para>
/// Reported rather than dropped, and that is the whole reason this type exists: the devices a plan
/// serves used to be the spurs of its boxes, and a device served by a splice would simply have gone
/// missing from that count. A screen that says "8 devices served" about a circuit with ten is worse
/// than one that says nothing.
/// </para>
/// </remarks>
public sealed class PlannedSplice
{
    internal PlannedSplice(CarrierId circuit, Tap tap)
    {
        Circuit = circuit;
        Tap = tap;
    }

    /// <summary>The circuit whose cable is cut here.</summary>
    public CarrierId Circuit { get; }

    /// <summary>The tap it serves.</summary>
    public Tap Tap { get; }

    /// <summary>The carrier the splice is made in.</summary>
    public CarrierId Carrier => Tap.Carrier;

    /// <summary>Where on it, in internal feet, host coordinates.</summary>
    public Point3 At => Tap.At;
}

/// <summary>Every place a circuit's cable is cut: the boxes it needs, and the splices it does not.</summary>
/// <remarks>
/// Two lists rather than one with a flag, because the two are used by different code for different
/// things and only one of them is ours to place: the apply phase puts an indicator at a recommended
/// box and writes parameters on it, and has nothing at all to do at a splice. A single list would
/// make every consumer filter, and the day one forgot, the tool would offer to put a box where the
/// project already said none is needed.
/// </remarks>
public sealed class BoxPlan
{
    internal BoxPlan(IReadOnlyList<PlannedBox> boxes, IReadOnlyList<PlannedSplice> splices)
    {
        Boxes = boxes;
        Splices = splices;
    }

    /// <summary>Nothing planned - what a run that routed no circuit cut in boxes carries.</summary>
    public static BoxPlan Empty { get; } =
        new(Array.Empty<PlannedBox>(), Array.Empty<PlannedSplice>());

    /// <summary>The boxes, existing ones used and places recommended.</summary>
    public IReadOnlyList<PlannedBox> Boxes { get; }

    /// <summary>The devices served by a splice in the carrier, in the order they were planned.</summary>
    public IReadOnlyList<PlannedSplice> Splices { get; }

    /// <summary>How many devices the plan serves, by a box or by a splice.</summary>
    public int Served
    {
        get
        {
            var served = Splices.Count;

            foreach (var box in Boxes)
                served += box.Spurs;

            return served;
        }
    }
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
    public static BoxPlan Plan(
        IEnumerable<RouteResult> routes,
        IEnumerable<ExistingBox> existing,
        double radius)
    {
        var boxes = new List<PlannedBox>();
        var splices = new List<PlannedSplice>();

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
                // A tap the router already gave a box goes to that box, however far it stands: routed
                // without additional boxes, the router chose it along the structure, and a radius on a
                // plan has no say in that. Everything else is decided by nearness, as it always was.
                var box = tap.Box is { } served
                    ? boxes.Find(one => one.Existing is { } existing && existing.Id == served)
                    : null;

                box ??= Nearest(boxes, tap.At, radius);

                // Nothing near, and the carrier allows a splice: the cable branches here and no box
                // is recommended. Asked after nearness on purpose - the owner's answer is that a box
                // already in the model wins, and a recommendation already made for a neighbouring tap
                // is the same kind of answer: one place instead of two, which is what the radius is
                // for.
                //
                // The trunk still leaves the box before this one, and that is why the previous box is
                // told so here rather than when the next box arrives: a splice that is the last stop
                // of a circuit would otherwise leave the box before it counting an entry it does not
                // have - the cable goes on, and there is no later box to notice. After it the trunk
                // stands at the splice, which counts nothing because there is nothing to count it on.
                if (box is null && tap.AllowsSplicing)
                {
                    splices.Add(new PlannedSplice(route.Circuit, tap));
                    previous?.Trunk();
                    previous = null;
                    continue;
                }

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
        return new BoxPlan(boxes.FindAll(box => box.Spurs > 0), splices);
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
