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

    /// <summary>How many conductors it holds, or zero when its type did not say.</summary>
    /// <remarks>
    /// <para>
    /// <b>In conductors and not in cable entries - the owner's answer of 2026-09-21.</b> A box with
    /// four glands and a terminal block of six conductors is bounded by the six.
    /// </para>
    /// <para>
    /// <b>Zero is "nobody said", never "holds nothing",</b> for the reason a device's terminal
    /// capacity keeps the same distinction: a box that is in the wiring holds at least one conductor
    /// by being there, so zero is a blank or a typo rather than a property of a product. The project
    /// answers for a blank, and when the project is silent too there is no limit at all.
    /// </para>
    /// <para>
    /// <b>An <c>init</c> rather than a constructor argument, and the asymmetry with a carrier's
    /// openness along its length is deliberate.</b> That one is a required argument because a carrier
    /// wrongly called open cuts into the middle of a run and the length that comes out is
    /// indistinguishable from a right one, so every caller is made to answer. A capacity forgotten
    /// costs a warning that is not raised - visible as silence, not as a plausible wrong number.
    /// </para>
    /// </remarks>
    public int Capacity { get; init; }
}

/// <summary>One box the calculation needs: an existing one it uses, or a place it recommends.</summary>
public sealed class PlannedBox
{
    internal PlannedBox(Point3 at, ExistingBox? existing, int defaultCapacity = 0)
    {
        At = at;
        Existing = existing;

        // The owner's chain of 2026-09-21: the type, then the project, then no limit at all. And a
        // recommendation has no capacity by their answer of the same day - the indicator says how
        // many entries meet here and the designer picks a box that takes them, so a limit here would
        // be the tool arguing with the advice it is about to give.
        Capacity = existing is null
            ? 0
            : existing.Capacity > 0 ? existing.Capacity : defaultCapacity;
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

    /// <summary>How many drops leave it for devices.</summary>
    /// <remarks>
    /// <b>No longer the same thing as the devices the circuit has, and since the tree replaced the
    /// chain it never was.</b> A chain put a box at every device; a tree cuts the cable only where it
    /// goes more than one way, so a device at the end of a branch drops straight off the carrier and
    /// is counted by nothing here.
    /// </remarks>
    public int Spurs => _taps.Count;

    /// <summary>
    /// Every cable entry: the one that arrives and every one that leaves - the owner's answer of
    /// 2026-09-11, restated for a tree.
    /// </summary>
    /// <remarks>
    /// What the designer picks a real box by, so it counts what the box has to take rather than what
    /// the device needs. A place where one cable arrives and two leave is three, whether the two are
    /// a trunk going on and a drop going down or two trunks; a box shared by two circuits takes the
    /// sum of both.
    /// </remarks>
    public int Entries { get; private set; }

    /// <summary>The drops it serves, in the order they arrived.</summary>
    public IReadOnlyList<Tap> Taps => _taps;

    /// <summary>How many conductors are spliced here, over every circuit cut in this box.</summary>
    /// <remarks>
    /// <b>Only the cables actually cut here, which is the owner's answer and not a simplification.</b>
    /// A cable the run merely passes through the box with is never opened, so nothing of it lands on
    /// the block - and it is not counted, because the planner is only ever told about branches.
    /// Circuits whose conductor count Revit does not give contribute nothing; see
    /// <c>CircuitSnapshot.Conductors</c>.
    /// </remarks>
    public int Conductors { get; private set; }

    /// <summary>How many it may hold: its type's answer, the project's, or zero for no limit.</summary>
    public int Capacity { get; }

    /// <summary>Whether more conductors are spliced here than it holds.</summary>
    /// <remarks>
    /// <b>It changes nothing and is only reported</b> - the owner's answer of 2026-09-21. No tap is
    /// moved, no box is split, no route is diverted and no length differs by a millimetre from what
    /// the same model gave before anybody stated a capacity. What an overfull box buys is a line on
    /// the screen and a warning Revit shows against the box itself.
    /// </remarks>
    public bool IsOverfull => Capacity > 0 && Conductors > Capacity;

    private readonly List<CarrierId> _circuits = new();
    private readonly List<Tap> _taps = new();

    /// <summary>Records that a circuit's cable is cut here, and how many ends that leaves.</summary>
    /// <remarks>
    /// <para>
    /// <b>A second branch of the same circuit brings two fewer ends than it holds.</b> The run between
    /// the two branches is now inside the box, so what were a cable leaving one and a cable arriving
    /// at the other becomes no cable at all. Two branches of three ends each make a box of four - one
    /// trunk in, one trunk out, two drops - which is the answer the planner gave before there was a
    /// tree, about the same box.
    /// </para>
    /// <para>
    /// <b>A stated approximation:</b> it takes the two branches to be consecutive on the circuit, so
    /// that exactly one run is swallowed. Two branches with a third between them would swallow less
    /// and the box would be reported an entry short. Branches merge only within a radius of about the
    /// size of a box, so a third standing between them is not a shape this can meet unless the radius
    /// has been set to something its name no longer describes.
    /// </para>
    /// <para>
    /// Two <i>different</i> circuits cut in one box share nothing, so their ends simply add: the
    /// owner's rule of 2026-09-11, unchanged.
    /// </para>
    /// </remarks>
    /// <param name="conductors">
    /// How many conductors one cable of this circuit has, zero when Revit did not say. The conductors
    /// follow the entries exactly, including the two an adjoining second branch swallows: the run that
    /// disappears into the box takes its conductors with it.
    /// </param>
    internal void Hold(CarrierId circuit, int entries, int conductors = 0)
    {
        if (_circuits.Contains(circuit))
        {
            Entries += entries - 2;
            Conductors += (entries - 2) * conductors;
            return;
        }

        _circuits.Add(circuit);
        Entries += entries;
        Conductors += entries * conductors;
    }

    /// <summary>
    /// Records a drop that leaves from here, without counting a further entry.
    /// </summary>
    /// <remarks>
    /// The drop is one of the ends <see cref="Hold"/> already counted - a branch of two ways whose
    /// second way is the cable going down to a device is three entries, not four. Kept separately
    /// because the screen and the apply want to know which devices hang off which box, and the entry
    /// count cannot say.
    /// </remarks>
    internal void Serve(Tap tap) => _taps.Add(tap);
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
    internal PlannedSplice(CarrierId circuit, Branch branch)
    {
        Circuit = circuit;
        Branch = branch;
    }

    /// <summary>The circuit whose cable is cut here.</summary>
    public CarrierId Circuit { get; }

    /// <summary>The branch of the cable tree made here.</summary>
    public Branch Branch { get; }

    /// <summary>The carrier the splice is made in.</summary>
    public CarrierId Carrier => Branch.Carrier;

    /// <summary>Where on it, in internal feet, host coordinates.</summary>
    public Point3 At => Branch.At;
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

    /// <summary>How many drops leave a place the cable is cut - a box or a splice.</summary>
    /// <remarks>
    /// <b>Not "devices served", which is what this counted until the tree replaced the chain.</b> A
    /// chain cut the cable at every device, so the two numbers were the same; a tree cuts it only
    /// where it splits, and a device at the end of a branch is served without any of this. Ask the
    /// run for how many devices were served - see <c>RouteRun.Served</c>.
    /// </remarks>
    public int FromACut
    {
        get
        {
            var count = Splices.Count;

            foreach (var box in Boxes)
                count += box.Spurs;

            return count;
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
    /// <param name="defaultCapacity">
    /// How many conductors a box holds when its own type does not say, in the project's answer; zero
    /// when the project does not say either, and then those boxes have no limit. Recommended boxes
    /// never take it - the owner's answer of 2026-09-21 is that a recommendation has no capacity.
    /// </param>
    public static BoxPlan Plan(
        IEnumerable<RouteResult> routes,
        IEnumerable<ExistingBox> existing,
        double radius,
        int defaultCapacity = 0)
    {
        var boxes = new List<PlannedBox>();
        var splices = new List<PlannedSplice>();

        foreach (var box in existing ?? Array.Empty<ExistingBox>())
        {
            if (box is not null)
                boxes.Add(new PlannedBox(box.At, box, defaultCapacity));
        }

        foreach (var route in routes ?? Array.Empty<RouteResult>())
        {
            if (route is null
                || route.Status != RouteStatus.Found
                || route.Connection != CircuitConnection.AtJunctionBox)
            {
                continue;
            }

            foreach (var branch in route.Branches)
            {
                // A cut made in a device's terminals needs nothing placed and nothing recommended -
                // the terminal block holds it. Only the structure asks for a box.
                if (branch.Device is not null)
                    continue;

                // An existing box the search branched at is that box, wherever the radius would have
                // sent it: the search chose it along the structure, and a distance on a plan has no
                // say in a decision already made about the structure.
                var box = boxes.Find(one =>
                    one.Existing is { } stood && stood.Id == branch.Carrier);

                box ??= Nearest(boxes, branch.At, radius);

                // Nothing near, and the carrier itself allows a splice: the cable branches inside the
                // element and no box is recommended. Asked after nearness on purpose - the owner's
                // answer is that a box already in the model wins, and a recommendation already made
                // for a neighbouring branch is the same kind of answer: one place instead of two,
                // which is what the radius is for.
                if (box is null && branch.AllowsSplicing)
                {
                    splices.Add(new PlannedSplice(route.Circuit, branch));
                    continue;
                }

                if (box is null)
                {
                    // The project's value is handed over here too, although a recommendation takes
                    // none of it: the constructor is where that rule lives, and a second copy of it
                    // - an argument quietly left off - would be a rule no breakage could show red.
                    box = new PlannedBox(branch.At, null, defaultCapacity);
                    boxes.Add(box);
                }

                box.Hold(route.Circuit, branch.Ways + 1, route.Conductors);
            }

            // Which devices hang off which box, and the tap's own answer comes first.
            //
            // <b>The owner's rule of 2026-09-17: a tap carries its box and belongs to it however far
            // it stands.</b> Without additional boxes the search walks back from the device to the
            // box the run it is on began at, and that distance is routinely past the radius - eight
            // of twelve taps on the owner's linked set. The radius decides which taps share one box;
            // it was never meant to decide whether a tap has the box the search already gave it, and
            // for a while after the tree landed it did, so ten of those twelve devices were served
            // by nothing.
            //
            // A tap with no box of its own is the ordinary tree: the line out of the panel reaches
            // its device without being cut anywhere. Then nearness is the only question there is.
            foreach (var tap in route.Taps)
            {
                var serving = tap.Box is { } stood
                    ? boxes.Find(one => one.Existing is { } box && box.Id == stood)
                    : null;

                serving ??= Nearest(boxes, tap.At, radius);

                if (serving is not null && serving.Circuits.Contains(route.Circuit))
                    serving.Serve(tap);
            }
        }

        // Existing boxes nobody branched at are not part of the answer; they were only offered.
        return new BoxPlan(boxes.FindAll(box => box.Circuits.Count > 0), splices);
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
