namespace BHS.MEP.Cabling.Routing;

/// <summary>Where a circuit's cable is cut so that it can go more than one way.</summary>
/// <remarks>
/// <para>
/// <b>The node of the cable graph the owner named on 2026-09-20</b>: a circuit's cable is a tree
/// rooted at its panel, and its branch points are where conductors are spliced. A device alone on
/// its branch is not one of these - the cable simply ends in it.
/// </para>
/// <para>
/// A branch is not the same thing as a junction box, and keeping them apart is the point.
/// <see cref="BoxPlanner"/> turns branches into boxes: the ones that fall within the radius of each
/// other share one, an existing box takes the ones near it, and a carrier that allows splicing needs
/// none at all. A branch made in a device's terminals never becomes a box.
/// </para>
/// </remarks>
public sealed class Branch
{
    public Branch(CarrierId carrier, Point3 at, int ways)
    {
        Carrier = carrier;
        At = at;
        Ways = ways;
    }

    /// <summary>The carrier the branch is made on.</summary>
    public CarrierId Carrier { get; }

    /// <summary>Where on it, in internal feet, host coordinates.</summary>
    public Point3 At { get; }

    /// <summary>How many cables leave the branch. Always at least two.</summary>
    /// <remarks>
    /// One arrives and <see cref="Ways"/> leave, so the place takes <c>Ways + 1</c> cable entries -
    /// which is what <c>BHS_Cbl_ЧислоВводов</c> reports on the box that ends up here, and what
    /// <c>BHS_Cbl_ЁмкостьКлеммника</c> limits when the branch is made in a device.
    /// </remarks>
    public int Ways { get; }

    /// <summary>The device whose terminals the branch is made in, when it is made in one.</summary>
    /// <remarks>
    /// Null when the branch is made on the structure. The two are physically different - one needs a
    /// box or a carrier that allows splicing, the other needs only room in a terminal block - and the
    /// owner's answer of 2026-09-21 ties the second to the connection the circuit is routed with:
    /// a device is a branch point only where the cable is cut at the terminal.
    /// </remarks>
    public Terminal? Device { get; init; }

    /// <summary>Whether the carrier the branch is made on is one cable may be spliced in.</summary>
    /// <remarks>
    /// Carried here rather than looked up later, for the reason the tap carries it: the planner has no
    /// network, and handing it one would give it a second way to know the structure. False for a
    /// branch made in a device's terminals - that one is not on a carrier at all.
    /// </remarks>
    public bool AllowsSplicing { get; init; }
}

/// <summary>
/// Finds the tree a circuit's cable makes over the structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tree rather than a chain, and the difference is measured in cable.</b> Until 2026-09-21 a
/// circuit was routed as a path visiting its devices in the order the model listed them: panel to
/// the first, first to the second, and so on. That shape cannot split, so a circuit whose devices
/// sit on two branches of a tray has to travel out to one and come back - it pays the shorter
/// branch twice. The owner's model is a tree rooted at the panel, and the branch points are where
/// the cable is spliced.
/// </para>
/// <para>
/// <b>One cable line leaves the panel</b> - the owner's answer of 2026-09-21. Three sockets served
/// by one cable that splits, and one socket served by its own, are two Revit circuits, not one; so
/// the root of this tree has degree one, and everything else branches below it.
/// </para>
/// <para>
/// <b>The order the circuit lists its devices in means nothing</b> - the owner's answer of the same
/// day, and it replaces an assumption of mine that stood in the router as though it were a fact:
/// that the order was the electrician's and reordering it would produce a route nobody wired. Revit
/// gives the user no way to set that order, so there was never anything there to respect.
/// </para>
/// <para>
/// <b>The problem is a Steiner tree in a graph, which is NP-hard, and at thirty-five devices per
/// circuit - the owner's figure - an exact answer is out of reach.</b> This is the shortest-path
/// heuristic: start at the panel, and repeatedly join whichever unserved device is nearest to what
/// the tree already reaches. It is within a factor of two of the best possible tree and is usually
/// far closer, but it is a heuristic and this file says so rather than implying an optimum. It is
/// chosen over the other classical heuristic - a spanning tree over the pairwise distances - for a
/// reason that matters here more than the bound: it grows the tree one join at a time, so the places
/// a join is <i>allowed</i> can be restricted as it grows, which is exactly what splicing rules are.
/// </para>
/// </remarks>
internal static class CableTree
{
    /// <summary>What the search made of one circuit.</summary>
    internal sealed class Result
    {
        public RouteStatus Status { get; init; } = RouteStatus.Found;

        public string BlockedAt { get; init; } = string.Empty;

        /// <summary>The carriers the cable runs through, each once, in ascending order.</summary>
        /// <remarks>
        /// <b>A set, and since the tree replaced the chain it no longer pretends otherwise.</b> A path
        /// has an order and a tree does not, so anything that read this list as a traversal is reading
        /// something that no longer exists. Ascending rather than arbitrary so that two runs over an
        /// unchanged model produce the same list - <c>BHS_Cbl_ОтпечатокМаршрута</c> is compared against
        /// what is stored in the model, and a list whose order depended on the search's tie-breaking
        /// would report circuits stale for having been computed twice.
        /// </remarks>
        public IReadOnlyList<CarrierId> Carriers { get; init; } = Array.Empty<CarrierId>();

        public double AlongCarriers { get; init; }

        public IReadOnlyDictionary<string, double> AlongByClass { get; init; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public double Approaches { get; init; }

        /// <summary>Where the cable leaves the structure, one per device.</summary>
        public IReadOnlyList<Tap> Taps { get; init; } = Array.Empty<Tap>();

        /// <summary>Where the cable is cut so that it can go more than one way.</summary>
        public IReadOnlyList<Branch> Branches { get; init; } = Array.Empty<Branch>();
    }

    /// <summary>What the search is allowed to do, beyond what the network says.</summary>
    /// <param name="Connection">Which places may be branched at: devices, or the structure.</param>
    /// <param name="SpliceCost">
    /// What one splice made in the structure costs, expressed in internal feet of cable. The owner's
    /// exchange rate: divide the installed price of a box by the price of a metre of the cable this
    /// project usually runs, and that is how many metres the search will run rather than cut. Zero
    /// makes the search split wherever it is even slightly shorter.
    /// </param>
    /// <param name="TerminalCapacity">
    /// How many cables a device's terminals hold, when the device's type does not say. Two is a
    /// device the cable passes through and no more.
    /// </param>
    /// <param name="ExistingBoxes">
    /// The boxes already in the model, when the project routes without additional boxes; null when it
    /// may recommend new ones. In the first case they and the carriers that allow splicing are the
    /// only places the structure may be cut at.
    /// </param>
    /// <param name="JoinTolerance">
    /// How near a place has to be to an existing box to be that box. The same distance the network
    /// joins carriers with, because it answers the same question: two things at one point.
    /// </param>
    internal readonly record struct Rules(
        CircuitConnection Connection,
        double SpliceCost,
        int TerminalCapacity,
        IReadOnlyCollection<ExistingBox>? ExistingBoxes,
        double JoinTolerance);

    /// <summary>One place a cable may pass through: a point on a carrier, or a circuit's own end.</summary>
    private readonly struct Place
    {
        public Place(CarrierId carrier, Point3 at, int terminal)
        {
            Carrier = carrier;
            At = at;
            Terminal = terminal;
        }

        /// <summary>The carrier this point lies on; default for a circuit's own ends.</summary>
        public CarrierId Carrier { get; }

        public Point3 At { get; }

        /// <summary>Which of the circuit's ends this is - zero for the panel - or -1 for a carrier point.</summary>
        public int Terminal { get; }

        public bool IsCircuitEnd => Terminal >= 0;
    }

    /// <summary>A step between two places, with what it costs the search and what it measures.</summary>
    /// <remarks>
    /// <b>Two numbers, because they are not the same number.</b> The search minimises a cost that
    /// carries the conduit preference - a thumb on the scale for choosing a route - while the length
    /// written into a parameter has to agree with a tape measure. The router has kept these apart
    /// since the preference was moved onto the class of the carrier; the tree keeps them apart the
    /// same way, on every edge rather than on the finished route.
    /// </remarks>
    private readonly struct Arc
    {
        public Arc(int to, double cost, double length, CarrierId along, string carrierClass)
            : this(to, cost, length, along, carrierClass, isApproach: false)
        {
        }

        private Arc(int to, double cost, double length, CarrierId along, string carrierClass, bool isApproach)
        {
            To = to;
            Cost = cost;
            Length = length;
            Along = along;
            Class = carrierClass;
            IsApproach = isApproach;
        }

        /// <summary>A drop between one of the circuit's ends and the structure.</summary>
        public static Arc Drop(int to, double cost) =>
            new(to, cost, cost, default, string.Empty, isApproach: true);

        public int To { get; }

        public double Cost { get; }

        public double Length { get; }

        /// <summary>The carrier this step runs along; default when it is a drop to a device or a panel.</summary>
        public CarrierId Along { get; }

        public string Class { get; }

        /// <summary>Whether this step is a drop between the structure and one of the circuit's ends.</summary>
        /// <remarks>
        /// <b>Its own flag, and not "the carrier is unset".</b> Nothing stops an element from having
        /// the identifier zero - the core's paper networks number carriers from it - so a default
        /// <see cref="CarrierId"/> means "carrier zero" as readily as it means "no carrier". Told
        /// apart by a sentinel, every foot of carrier zero was counted as a drop and the carrier
        /// itself was never indexed, so nothing could be joined to it at all.
        /// </remarks>
        public bool IsApproach { get; }
    }

    private const double Coincident = 1e-9;

    /// <summary>Finds the tree, or says which end of the circuit stopped it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The panel first, then the nearest device, then the next nearest to anything the tree has
    /// reached</b> - and "nearest" counts the splice as well as the cable, so a join that saves less
    /// cable than a splice costs is not made. That is the owner's objective of 2026-09-21 written as
    /// the thing the search actually minimises: total length plus one price per splice, in one unit.
    /// </para>
    /// <para>
    /// <b>Greedy, and the file says so rather than implying an optimum.</b> Each device is joined by
    /// the cheapest join available when its turn comes, which can be beaten by a set of joins chosen
    /// together. At the sizes the owner named it is the difference between an answer in milliseconds
    /// and no answer at all.
    /// </para>
    /// <para>
    /// A new join may not run through the tree it is joining: a path that re-entered the tree would
    /// close a loop, and a cable tree has none. Stated because it costs something - the join is the
    /// shortest one that stays clear of the tree, not the shortest one there is.
    /// </para>
    /// </remarks>
    internal static Result Search(
        RouteNetwork network,
        CircuitSnapshot circuit,
        RoutingOptions options,
        Rules rules)
    {
        var graph = Build(network, circuit, options, out var ends, out var blocked);

        if (graph is null)
            return new Result { Status = RouteStatus.NoCarrierNear, BlockedAt = blocked };

        var cable = new Laid(graph.Count);
        var served = new bool[ends.Length];
        var tapOf = new Tap?[ends.Length];
        var runs = new List<int>[ends.Length];
        var runEdges = new List<(int From, int To, Arc Step)>[ends.Length];

        cable.Occupy(ends[0]);

        for (var joined = 1; joined < ends.Length; joined++)
        {
            var reach = Reach(graph, rules, network, circuit, cable, ends);
            var pick = -1;
            var cheapest = double.MaxValue;

            for (var i = 1; i < ends.Length; i++)
            {
                if (served[i] || reach.Cost[ends[i]] >= cheapest)
                    continue;

                pick = i;
                cheapest = reach.Cost[ends[i]];
            }

            if (pick < 0)
            {
                // Nothing the cable reaches serves the rest. A device that reaches no carrier at all
                // was caught while the graph was built, so what is left here is a device the structure
                // does not join to the tree - or, when the project forbids new boxes, one that no
                // place the cable may be cut at can reach.
                return new Result
                {
                    Status = rules.ExistingBoxes is null
                        ? RouteStatus.NoConnectivity
                        : RouteStatus.NoBoxReachable,
                    BlockedAt = First(served, circuit),
                };
            }

            served[pick] = true;
            Attach(graph, network, circuit, rules, reach, ends, pick, cable, runs, runEdges, tapOf);
        }

        Improve(graph, network, circuit, rules, ends, cable, runs, runEdges, tapOf);
        HangOff(graph, rules, cable, runEdges, tapOf);

        var edges = new List<(int From, int To, Arc Step)>();

        for (var i = 1; i < ends.Length; i++)
        {
            if (runEdges[i] is { } laid)
                edges.AddRange(laid);
        }

        return Finish(graph, network, circuit, edges, cable, ends, tapOf);
    }

    /// <summary>Lays the run a search found for one device, and records what it means.</summary>
    private static void Attach(
        Graph graph,
        RouteNetwork network,
        CircuitSnapshot circuit,
        Rules rules,
        Reaching reach,
        int[] ends,
        int device,
        Laid cable,
        List<int>[] runs,
        List<(int From, int To, Arc Step)>[] runEdges,
        Tap?[] tapOf)
    {
        var run = new List<int> { ends[device] };
        var edges = new List<(int From, int To, Arc Step)>();
        var cursor = ends[device];
        var leaves = -1;
        var drop = 0.0;

        while (reach.From[cursor] >= 0)
        {
            var previous = reach.From[cursor];
            var step = reach.Step[cursor];

            edges.Add((previous, cursor, step));

            // Where this run leaves the structure for its device: the last place on a carrier before
            // the drop. That is what the apply phase calls a tap.
            if (cursor == ends[device] && step.IsApproach)
            {
                leaves = previous;
                drop = step.Length;
            }

            cursor = previous;
            run.Add(cursor);
        }

        cable.Lay(run);
        runs[device] = run;
        runEdges[device] = edges;

        if (leaves < 0)
            return;

        var at = graph.At(leaves);

        // Which box the device hangs off is not decided here - see HangOff. It cannot be: the cable
        // is still being laid, and whether a place is cut is a fact about the finished tree.
        tapOf[device] = new Tap(circuit.Devices[device - 1], at.Carrier, at.At, drop)
        {
            AllowsSplicing = Router.Splices(network, at.Carrier),
        };
    }

    /// <summary>
    /// Says which box already in the model each device hangs off, once the whole tree is laid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The last cut before the device, and not merely the nearest box its cable goes past.</b> The
    /// piece of cable that feeds a device runs from the place it was last cut; that is what a spur is
    /// and what the box at that place holds. A box the cable passes through uncut feeds nothing - the
    /// run simply goes on through it - and naming it would tell the apply to write this circuit on a
    /// box it does not branch at, and the screen that a device is served from somewhere it is not.
    /// </para>
    /// <para>
    /// <b>After the search, never during it, and that is the half that made the first version wrong
    /// in a way no paper network showed.</b> Whether a place is cut depends on runs that have not
    /// been laid yet: a box in the middle of this run becomes a cut the moment another device's run
    /// begins there, which may be three devices later, and the improvement pass re-lays runs after
    /// that again. Asked while the tree grows, the question has no stable answer.
    /// </para>
    /// <para>
    /// <b>Found by the canonical sweep, and only after the case stopped asking about a total.</b> On
    /// the owner's linked set ten of twelve taps name a box; asked as a sum, a tap naming the wrong
    /// one is invisible, because some other tap makes the count up.
    /// </para>
    /// </remarks>
    private static void HangOff(
        Graph graph,
        Rules rules,
        Laid cable,
        List<(int From, int To, Arc Step)>[] runEdges,
        Tap?[] tapOf)
    {
        if (rules.ExistingBoxes is null)
            return;

        for (var device = 1; device < tapOf.Length; device++)
        {
            if (tapOf[device] is not { } tap || runEdges[device] is not { } edges)
                continue;

            var walked = 0.0;

            foreach (var edge in edges)
            {
                if (!edge.Step.IsApproach)
                    walked += edge.Step.Length;

                // The run's own beginning is cut by being one - so a run that starts at a box stops
                // here, and one that starts at the panel walks out of the loop with nothing named.
                if (!cable.IsCut(edge.From))
                    continue;

                if (Standing(rules.ExistingBoxes, graph.At(edge.From), rules.JoinTolerance) is { } box)
                {
                    tapOf[device] = new Tap(tap.Device, tap.Carrier, tap.At, tap.Spur)
                    {
                        AllowsSplicing = tap.AllowsSplicing,
                        Box = box.Id,
                        SpurAlongCarriers = walked,
                    };
                }

                // Cut and no box standing there: the device hangs off that cut, which is a splice in
                // a carrier, and off no box at all. Either way the walk stops at the first cut.
                break;
            }
        }
    }

    /// <summary>
    /// Re-lays any run that has become cheaper now that the rest of the cable is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because joining the nearest device first can be beaten, and was, on three devices.</b> Two
    /// boxes on one tray and three sockets: joined nearest-first, the second socket is served from the
    /// first box and the third is served from that same box, all the way past the second - sixty feet
    /// of cable where fifty-two would do. Serving the far socket first puts the cable past the second
    /// box, and the near one is then seven feet from it instead of fifteen.
    /// </para>
    /// <para>
    /// So each run is lifted in turn and searched for again against everything else that is laid. A
    /// run is only lifted when nothing else depends on it - no other run begins at a place that would
    /// be left bare - which keeps every intermediate state a tree that could be built.
    /// </para>
    /// <para>
    /// <b>Still a heuristic, and bounded rather than run to exhaustion.</b> Each pass can only lower
    /// the total, so it terminates; the cap is there because the last passes buy very little and a
    /// circuit of thirty-five devices is searched once per device per pass.
    /// </para>
    /// </remarks>
    private static void Improve(
        Graph graph,
        RouteNetwork network,
        CircuitSnapshot circuit,
        Rules rules,
        int[] ends,
        Laid cable,
        List<int>[] runs,
        List<(int From, int To, Arc Step)>[] runEdges,
        Tap?[] tapOf)
    {
        const int passes = 3;

        for (var pass = 0; pass < passes; pass++)
        {
            var bettered = false;

            for (var device = 1; device < ends.Length; device++)
            {
                if (runs[device] is not { } laid || !CanLift(laid, runs, device, cable, ends[0]))
                    continue;

                var walked = Length(runEdges[device]);

                cable.Unlay(laid);

                var reach = Reach(graph, rules, network, circuit, cable, ends);
                var now = reach.Cost[ends[device]];

                // What the run as laid would cost if it were being chosen now - its own steps plus
                // whatever cutting the cable where it begins costs against the rest as it now stands.
                // Compared without that, a run that pays no splice would always look dearer than one
                // that does, and the pass would swap them back and forth.
                var was = walked + (Price(graph, rules, network, circuit, cable, laid[laid.Count - 1]) ?? 0);

                // Ties keep what is already laid: re-laying an equal run would churn the answer
                // between runs of an unchanged model, which is the one thing a plan must not do.
                if (now >= was - 1e-9 || reach.From[ends[device]] < 0)
                {
                    cable.Lay(laid);
                    continue;
                }

                Attach(graph, network, circuit, rules, reach, ends, device, cable, runs, runEdges, tapOf);
                bettered = true;
            }

            if (!bettered)
                return;
        }
    }

    /// <summary>Whether a run can be taken back without leaving another one beginning nowhere.</summary>
    /// <remarks>
    /// <b>The arithmetic is worth spelling out, because getting it wrong silently dropped a run.</b>
    /// A run begins on the cable somewhere; if the run being lifted is what put the cable there, that
    /// beginning is left in mid-air. The place's occupancy counts the run that begins there and the
    /// run being lifted, so two means those two and nothing else - and lifting would strand it. It is
    /// safe only when a third run lies on the same place, or when the lifted run never touches it.
    /// </remarks>
    private static bool CanLift(IReadOnlyList<int> run, List<int>[] runs, int device, Laid cable, int panel)
    {
        // The run that begins at the panel is the one line leaving it, and nothing else may take its
        // place: lifted, the search would happily serve its device from somewhere cheaper and leave
        // the panel connected to nothing. A stated limitation of the improvement rather than of the
        // model - that first run is never re-laid, so a better trunk out of the panel is not looked
        // for once one has been chosen.
        if (run.Count > 0 && run[run.Count - 1] == panel)
            return false;

        for (var other = 1; other < runs.Length; other++)
        {
            if (other == device || runs[other] is not { Count: > 0 } laid)
                continue;

            var begins = laid[laid.Count - 1];
            var touched = false;

            foreach (var vertex in run)
            {
                if (vertex != begins)
                    continue;

                touched = true;
                break;
            }

            if (touched && cable.Occupancy(begins) <= 2)
                return false;
        }

        return true;
    }

    /// <summary>What a run costs the search, so that a replacement can be compared with it.</summary>
    /// <remarks>
    /// The search's cost, with the conduit preference in it, and not the length - the comparison has
    /// to be made in the same currency the search minimises or it would swap a route the search
    /// prefers for one it does not. The splice at the far end is the same either way: the run is being
    /// re-laid from somewhere, and where it starts is what the search is choosing.
    /// </remarks>
    private static double Length(IReadOnlyList<(int From, int To, Arc Step)>? edges)
    {
        if (edges is null)
            return double.MaxValue;

        var cost = 0.0;

        foreach (var edge in edges)
            cost += edge.Step.Cost;

        return cost;
    }

    /// <summary>
    /// What the cable occupies so far, counted as cable ends rather than as places.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction this type exists for, and getting it wrong cost a rewrite.</b> A cable tree
    /// is a tree of <i>runs</i>, not of points on the structure: where the cable is cut at a device's
    /// terminals, the run that arrives ends there and a new one leaves, and on the way out the two lie
    /// in the same tray side by side. Counted as places, that tray point looks like a loop and like a
    /// branch, and it is neither - it is two cables next to each other, which is what a tray is for.
    /// </para>
    /// <para>
    /// So a place is cut only when a run begins or ends at it. Then, and only then, any run merely
    /// passing through has to be cut too, and the place holds two ends of each - which is how a box on
    /// a trunk that also feeds a device comes to hold three.
    /// </para>
    /// </remarks>
    private sealed class Laid
    {
        private readonly int[] _terminating;
        private readonly int[] _passing;
        private readonly int[] _occupancy;

        public Laid(int places)
        {
            _terminating = new int[places];
            _passing = new int[places];
            _occupancy = new int[places];
        }

        /// <summary>Whether the cable is anywhere on this place.</summary>
        public bool Occupies(int vertex) => _occupancy[vertex] > 0;

        /// <summary>How many runs lie on this place.</summary>
        public int Occupancy(int vertex) => _occupancy[vertex];

        /// <summary>Marks a place the cable reaches before any run has been laid - the panel.</summary>
        public void Occupy(int vertex) => _occupancy[vertex]++;

        /// <summary>Whether the cable is cut here at all.</summary>
        public bool IsCut(int vertex) => _terminating[vertex] > 0;

        /// <summary>How many cable ends this place holds; zero where the cable is not cut.</summary>
        public int Ends(int vertex) =>
            _terminating[vertex] == 0 ? 0 : _terminating[vertex] + (2 * _passing[vertex]);

        /// <summary>Records one run, from where it begins to where it ends.</summary>
        public void Lay(IReadOnlyList<int> run) => Mark(run, 1);

        /// <summary>Takes one run back, so that a cheaper one can be tried in its place.</summary>
        public void Unlay(IReadOnlyList<int> run) => Mark(run, -1);

        private void Mark(IReadOnlyList<int> run, int by)
        {
            for (var i = 0; i < run.Count; i++)
            {
                _occupancy[run[i]] += by;

                if (i == 0 || i == run.Count - 1)
                    _terminating[run[i]] += by;
                else
                    _passing[run[i]] += by;
            }
        }
    }

    /// <summary>The first device the cable failed to reach, named for the report.</summary>
    private static string First(bool[] served, CircuitSnapshot circuit)
    {
        for (var i = 1; i < served.Length; i++)
        {
            if (!served[i])
                return circuit.Devices[i - 1].Label;
        }

        return circuit.Number;
    }

    /// <summary>What every place costs to start a new run from, and how the run gets there.</summary>
    private readonly struct Reaching
    {
        public Reaching(double[] cost, int[] from, Arc[] step)
        {
            Cost = cost;
            From = from;
            Step = step;
        }

        public double[] Cost { get; }

        public int[] From { get; }

        public Arc[] Step { get; }
    }

    /// <summary>
    /// The cheapest new run from the cable to every place, seeded at each place it may be cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The splice is paid at the seed rather than added afterwards, which is what makes the choice
    /// between "run further" and "cut here" a single comparison instead of two passes that have to
    /// agree. A place already cut is seeded at nothing: a second cable leaving a splice that exists
    /// costs no second splice.
    /// </para>
    /// <para>
    /// A new run may pass through places the cable already occupies, and pays the full length for
    /// doing so - that is two cables in one tray, which is ordinary. What it may not do is start
    /// somewhere the cable may not be cut.
    /// </para>
    /// </remarks>
    private static Reaching Reach(
        Graph graph,
        Rules rules,
        RouteNetwork network,
        CircuitSnapshot circuit,
        Laid cable,
        int[] ends)
    {
        var cost = new double[graph.Count];
        var from = new int[graph.Count];
        var step = new Arc[graph.Count];
        var queue = new Heap();

        for (var i = 0; i < cost.Length; i++)
        {
            cost[i] = double.MaxValue;
            from[i] = -1;
        }

        for (var vertex = 0; vertex < graph.Count; vertex++)
        {
            if (!cable.Occupies(vertex)
                || Price(graph, rules, network, circuit, cable, vertex) is not { } price)
            {
                continue;
            }

            cost[vertex] = price;
            queue.Push(vertex, price);
        }

        var settled = new bool[graph.Count];

        while (queue.TryPop(out var vertex, out var reached))
        {
            if (settled[vertex])
                continue;

            settled[vertex] = true;

            if (reached > cost[vertex])
                continue;

            // A run ends at the device it was laid for; it never continues through one. A cable that
            // passed through a socket on its way elsewhere is the cut at that socket's terminals,
            // which is a join of its own and priced as one.
            if (graph.At(vertex).IsCircuitEnd && from[vertex] >= 0)
                continue;

            foreach (var arc in graph.From(vertex))
            {
                var candidate = reached + arc.Cost;

                if (candidate >= cost[arc.To])
                    continue;

                cost[arc.To] = candidate;
                from[arc.To] = vertex;
                step[arc.To] = arc;
                queue.Push(arc.To, candidate);
            }
        }

        return new Reaching(cost, from, step);
    }

    /// <summary>What cutting the cable at this place costs, or null when it may not be cut there.</summary>
    /// <remarks>
    /// <para>
    /// Every rule here is the owner's. <b>One cable line leaves the panel</b>, so the panel may be
    /// started from exactly once. <b>A device is a branch point only where the cable is cut at the
    /// terminal</b>, and only while its terminals have room - the type says how much, and a type that
    /// does not say gets the project's answer. <b>The structure may be cut wherever a box may stand</b>,
    /// which is anywhere at all while the project lets boxes be recommended, and only at an existing
    /// box when it does not.
    /// </para>
    /// <para>
    /// <b>A carrier that allows splicing is cuttable in either connection</b>, because the permission
    /// is the carrier's and says nothing about cables: it is the type of the tray or trunking that
    /// decides, not how this circuit is connected.
    /// </para>
    /// <para>
    /// <b>One price for both kinds of cut in the structure, and that is an approximation this file
    /// names.</b> A splice in a trunking buys no box, so it should cost less than one; until the owner
    /// gives a second number, both are what a splice costs, which errs towards running cable rather
    /// than cutting it.
    /// </para>
    /// </remarks>
    private static double? Price(
        Graph graph,
        Rules rules,
        RouteNetwork network,
        CircuitSnapshot circuit,
        Laid cable,
        int vertex)
    {
        var place = graph.At(vertex);

        if (place.IsCircuitEnd)
        {
            if (place.Terminal == 0)
                return cable.IsCut(vertex) ? null : 0;

            if (rules.Connection != CircuitConnection.AtTerminal)
                return null;

            var device = circuit.Devices[place.Terminal - 1];
            var capacity = device.Capacity > 0 ? device.Capacity : rules.TerminalCapacity;

            return cable.Ends(vertex) + 1 <= capacity ? 0 : null;
        }

        if (!Router.Splices(network, place.Carrier))
        {
            if (rules.Connection != CircuitConnection.AtJunctionBox)
                return null;

            if (rules.ExistingBoxes is not null
                && Standing(rules.ExistingBoxes, place, rules.JoinTolerance) is null)
            {
                return null;
            }
        }

        // A place the cable is already cut at takes another leaving run for nothing; one it merely
        // passes through has to be cut to take one, and that is what a splice costs.
        return cable.IsCut(vertex) ? 0 : rules.SpliceCost;
    }

    /// <summary>The box already in the model standing at this place, or null.</summary>
    /// <remarks>
    /// <b>Matched by where it stands and not by which element the place belongs to, and that
    /// distinction was measured as a defect.</b> Carriers that touch each other touch the box between
    /// them as well, so one point in space is several places - the tray's end, the next tray's end,
    /// and the box's own - each with its own carrier. A search standing on the tray's end at the very
    /// point of a box would have been told there was no box there, and a circuit routed without
    /// additional boxes would report that none of its devices could reach one.
    /// </remarks>
    private static ExistingBox? Standing(
        IReadOnlyCollection<ExistingBox>? boxes,
        Place place,
        double tolerance)
    {
        if (boxes is null)
            return null;

        foreach (var box in boxes)
        {
            if (box.At.DistanceTo(place.At) <= tolerance)
                return box;
        }

        return null;
    }

    /// <summary>The graph one circuit is searched over: every place a cable may pass, and the steps between.</summary>
    /// <remarks>
    /// <para>
    /// <b>Built per circuit and thrown away, because half of it belongs to the circuit.</b> The
    /// carriers and their joins are the same for every circuit in a run, but where a cable may leave
    /// the structure for a device is not, and a tray that two devices tap has to be cut into three
    /// pieces at exactly those two points for the cable between them to be measured along it rather
    /// than out to the tray's end and back. Sharing the carrier half and rebuilding the rest is an
    /// optimisation with a way to be subtly wrong, and the measured cost of not having it is a
    /// fraction of what reading the model costs.
    /// </para>
    /// <para>
    /// <b>The whole network goes in, not only the carriers near the circuit's devices.</b> A cable
    /// tree may branch at a place that serves nobody - a tee where a tray splits towards two rooms is
    /// the ordinary case - so the places worth branching at cannot be gathered from the devices.
    /// </para>
    /// </remarks>
    private sealed class Graph
    {
        private readonly List<Place> _places = new();
        private readonly List<List<Arc>> _arcs = new();
        private readonly Dictionary<CarrierId, List<int>> _onCarrier = new();

        public int Count => _places.Count;

        public Place At(int vertex) => _places[vertex];

        public IReadOnlyList<Arc> From(int vertex) => _arcs[vertex];

        public int Add(Place place)
        {
            _places.Add(place);
            _arcs.Add(new List<Arc>());

            if (!place.IsCircuitEnd)
            {
                if (!_onCarrier.TryGetValue(place.Carrier, out var list))
                    _onCarrier[place.Carrier] = list = new List<int>();

                list.Add(_places.Count - 1);
            }

            return _places.Count - 1;
        }

        public IReadOnlyList<int> On(CarrierId carrier) =>
            _onCarrier.TryGetValue(carrier, out var list) ? list : Array.Empty<int>();

        /// <summary>The vertex already standing at this point of this carrier, or -1.</summary>
        public int Find(CarrierId carrier, Point3 at)
        {
            foreach (var vertex in On(carrier))
            {
                if (_places[vertex].At.DistanceTo(at) <= Coincident)
                    return vertex;
            }

            return -1;
        }

        public void Join(int a, int b, double cost, double length, CarrierId along, string carrierClass)
        {
            if (a == b)
                return;

            _arcs[a].Add(new Arc(b, cost, length, along, carrierClass));
            _arcs[b].Add(new Arc(a, cost, length, along, carrierClass));
        }

        /// <summary>Joins one of the circuit's ends to the structure by a drop.</summary>
        public void Drop(int end, int onCarrier, double cost)
        {
            if (end == onCarrier)
                return;

            _arcs[end].Add(Arc.Drop(onCarrier, cost));
            _arcs[onCarrier].Add(Arc.Drop(end, cost));
        }
    }

    /// <summary>Lays the circuit and the structure out as one graph.</summary>
    private static Graph? Build(
        RouteNetwork network,
        CircuitSnapshot circuit,
        RoutingOptions options,
        out int[] ends,
        out string blockedAt)
    {
        var graph = new Graph();
        var terminals = new List<Terminal> { circuit.Source };
        terminals.AddRange(circuit.Devices);

        ends = new int[terminals.Count];
        blockedAt = string.Empty;

        for (var i = 0; i < terminals.Count; i++)
            ends[i] = graph.Add(new Place(default, terminals[i].At, i));

        // What each end of the circuit reaches, and where it meets it. An end that reaches nothing
        // stops the circuit here rather than later: the tree cannot be built without it, and the
        // report wants the element, not the shape of the failure.
        var reach = new Dictionary<CarrierId, (Point3 At, double Cost)>[terminals.Count];

        for (var i = 0; i < terminals.Count; i++)
        {
            reach[i] = Router.Approachable(network, terminals[i], options);

            if (reach[i].Count != 0)
                continue;

            blockedAt = terminals[i].Label;
            return null;
        }

        // Every carrier of the network becomes a little chain of places: its own terminals, plus
        // wherever a device or the panel meets it. Without the second kind, two sockets under one
        // tray would be measured to the tray's ends and back instead of along the ten feet between
        // them - the defect the port model fixed for a path, restated for a tree.
        foreach (var node in network.Nodes)
        {
            var points = new List<Point3>(node.Terminals);

            foreach (var one in reach)
            {
                if (one.TryGetValue(node.Id, out var met))
                    points.Add(met.At);
            }

            Lay(graph, node, points, options);
        }

        // Where two carriers touch, the cable crosses for nothing. The tolerance is the network's,
        // so what joins here is exactly what the network calls adjacent.
        foreach (var node in network.Nodes)
        {
            foreach (var next in network.Neighbours(node.Id))
            {
                if (network.Node(next) is not { } other)
                    continue;

                foreach (var reached in node.Terminals)
                {
                    var touch = Router.Touching(other, reached, options.JoinTolerance);

                    if (touch < 0)
                        continue;

                    var here = graph.Find(node.Id, reached);
                    var there = graph.Find(other.Id, other.Terminals[touch]);

                    if (here >= 0 && there >= 0)
                        graph.Join(here, there, 0, 0, node.Id, node.Class);
                }
            }
        }

        // And the drops: from each end of the circuit to every carrier it reaches. Which one the
        // cable actually uses is the search's to decide, so all of them are offered.
        for (var i = 0; i < terminals.Count; i++)
        {
            foreach (var met in reach[i])
            {
                var vertex = graph.Find(met.Key, met.Value.At);

                if (vertex >= 0)
                    graph.Drop(ends[i], vertex, met.Value.Cost);
            }
        }

        return graph;
    }

    /// <summary>Puts the points of one carrier into the graph and joins them along it.</summary>
    /// <remarks>
    /// A segment is a straight run, so its points lie in one order along it and only neighbours need
    /// joining - which keeps the graph sparse and, more to the point, stops the cable from being
    /// measured as though it could jump along a tray. A fitting is not straight, so every way through
    /// it costs what the model says the fitting is, and entering and leaving by the same connector
    /// costs nothing; that is the same approximation the path search has always made.
    /// </remarks>
    private static void Lay(Graph graph, CarrierNode node, List<Point3> points, RoutingOptions options)
    {
        var vertices = new List<int>();

        foreach (var at in points)
        {
            var known = graph.Find(node.Id, at);

            vertices.Add(known >= 0 ? known : graph.Add(new Place(node.Id, at, -1)));
        }

        var factor = Router.Factor(node, options);

        if (node.Kind != CarrierKind.Segment)
        {
            for (var a = 0; a < vertices.Count; a++)
            {
                for (var b = a + 1; b < vertices.Count; b++)
                {
                    var length = Router.Along(node, graph.At(vertices[a]).At, graph.At(vertices[b]).At);
                    graph.Join(vertices[a], vertices[b], length * factor, length, node.Id, node.Class);
                }
            }

            return;
        }

        var span = node.Start.DistanceTo(node.End);
        var order = new List<(double Along, int Vertex)>();

        foreach (var vertex in vertices)
        {
            var at = graph.At(vertex).At;
            var along = span <= Coincident ? 0 : Project(node.Start, node.End, at);

            order.Add((along, vertex));
        }

        order.Sort((left, right) => left.Along.CompareTo(right.Along));

        for (var i = 1; i < order.Count; i++)
        {
            var length = Router.Along(node, graph.At(order[i - 1].Vertex).At, graph.At(order[i].Vertex).At);
            graph.Join(order[i - 1].Vertex, order[i].Vertex, length * factor, length, node.Id, node.Class);
        }
    }

    /// <summary>Adds the finished tree up: what it measures, what it runs through, where it is cut.</summary>
    /// <remarks>
    /// <para>
    /// <b>A place the cable goes more than one way is a branch; a place it merely passes through is
    /// not.</b> A device the cable enters and leaves is cut at its terminals and needs no box, and the
    /// slack already counts every device once, so it is not reported here - what is reported is the
    /// places something has to hold three cable ends or more.
    /// </para>
    /// <para>
    /// The carriers come out sorted rather than in the order the search happened to lay them, because
    /// they are compared against what is stored in a model. See <see cref="Result.Carriers"/>.
    /// </para>
    /// </remarks>
    private static Result Finish(
        Graph graph,
        RouteNetwork network,
        CircuitSnapshot circuit,
        List<(int From, int To, Arc Step)> edges,
        Laid cable,
        int[] ends,
        Tap?[] tapOf)
    {
        var along = 0.0;
        var approaches = 0.0;
        var byClass = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var carriers = new List<CarrierId>();
        var seen = new HashSet<CarrierId>();

        foreach (var edge in edges)
        {
            if (edge.Step.IsApproach)
            {
                approaches += edge.Step.Length;
                continue;
            }

            along += edge.Step.Length;

            if (byClass.TryGetValue(edge.Step.Class, out var known))
                byClass[edge.Step.Class] = known + edge.Step.Length;
            else
                byClass[edge.Step.Class] = edge.Step.Length;

            if (seen.Add(edge.Step.Along))
                carriers.Add(edge.Step.Along);
        }

        carriers.Sort(static (left, right) =>
        {
            var source = left.Source.CompareTo(right.Source);
            return source != 0 ? source : left.Value.CompareTo(right.Value);
        });

        var branches = new List<Branch>();

        for (var vertex = 0; vertex < graph.Count; vertex++)
        {
            // One end is the cable arriving and the rest are cables leaving, so a place holding two
            // is not a branch: it is a cable cut and carried on, which is what a device's terminals
            // do and what every device already accounts for in the slack.
            var held = cable.Ends(vertex);

            if (held < 3)
                continue;

            var place = graph.At(vertex);

            if (place.IsCircuitEnd)
            {
                // The panel cannot be one of these - one cable line leaves it - so this is a device
                // whose terminals hold the split.
                if (place.Terminal == 0)
                    continue;

                branches.Add(new Branch(default, place.At, held - 1)
                {
                    Device = circuit.Devices[place.Terminal - 1],
                });

                continue;
            }

            branches.Add(new Branch(place.Carrier, place.At, held - 1)
            {
                AllowsSplicing = Router.Splices(network, place.Carrier),
            });
        }

        var taps = new List<Tap>();

        for (var i = 1; i < ends.Length; i++)
        {
            if (tapOf[i] is { } tap)
                taps.Add(tap);
        }

        return new Result
        {
            Carriers = carriers,
            AlongCarriers = along,
            AlongByClass = byClass,
            Approaches = approaches,
            Taps = taps,
            Branches = branches,
        };
    }

    /// <summary>A binary heap over vertex numbers, for the same reason the router has one over ports.</summary>
    /// <remarks>
    /// <c>System.Collections.Generic.PriorityQueue</c> arrived in .NET 6 and this assembly also targets
    /// net48. Thirty lines rather than a package: on net48 the metric that matters is assemblies
    /// somebody else may also ship, and this one ships none.
    /// </remarks>
    private sealed class Heap
    {
        private readonly List<(int Item, double Cost)> _heap = new();

        public void Push(int item, double cost)
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

        public bool TryPop(out int item, out double cost)
        {
            if (_heap.Count == 0)
            {
                item = -1;
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

    /// <summary>How far along the segment from <paramref name="from"/> to <paramref name="to"/> a point falls.</summary>
    private static double Project(Point3 from, Point3 to, Point3 at)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        var square = (dx * dx) + (dy * dy) + (dz * dz);

        if (square <= Coincident)
            return 0;

        return (((at.X - from.X) * dx) + ((at.Y - from.Y) * dy) + ((at.Z - from.Z) * dz)) / square;
    }
}
