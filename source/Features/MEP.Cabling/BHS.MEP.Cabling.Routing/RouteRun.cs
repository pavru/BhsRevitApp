namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// What one pass of the search produced, with the failures grouped rather than counted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grouping is the whole point, and it is why <see cref="RouteStatus"/> exists.</b> The
/// predecessor returned an empty list for three unrelated reasons, so a report could say "twelve
/// circuits did not compute" and nothing more. Twelve failures are two or three causes; a person can
/// act on a cause and cannot act on a number.
/// </para>
/// <para>
/// It lives beside the search rather than in the view model, because it is about routes. A view
/// model that worked these out would be a second place where "what went wrong" is decided, and two
/// such places disagree within a month. The view model formats what is here; it does not compute it.
/// </para>
/// <para>
/// <b>The counts are walked once, in the constructor, rather than answered from properties.</b> The
/// screen asks for four of them and the failure list asks again, and on a run of several hundred
/// circuits that is six passes to answer questions whose answer cannot change: everything here is
/// finished by the time it is built.
/// </para>
/// </remarks>
public sealed class RouteRun
{
    private readonly int[] _byStatus;
    private readonly Dictionary<CarrierId, double> _slack = new();
    private readonly IReadOnlyList<PlannedBox> _overfull;

    /// <param name="plan">
    /// Where each circuit's cable is cut: the boxes it needs and the splices that need none. Slack is
    /// counted per place, so this is what decides it - see <see cref="RouteResult.Measured"/>.
    /// </param>
    /// <param name="slack">What the project adds, per place and as a fraction.</param>
    public RouteRun(
        IReadOnlyList<RouteResult> results,
        long networkVersion,
        TimeSpan took,
        BoxPlan? plan = null,
        SlackRule? slack = null)
    {
        Results = results;
        NetworkVersion = networkVersion;
        Took = took;
        Plan = plan ?? BoxPlan.Empty;
        Slack = slack ?? SlackRule.None;

        // How many places each circuit's cable is cut, from the plan: boxes it is served from, and
        // splices made in a carrier. A box shared by two circuits counts once for each of them - both
        // cables are cut in it - and two taps merged into one box count once, because the cable is cut
        // there once. That last part is the reason this is counted here and not in the router: it is
        // the planner's radius that merges them.
        var boxes = new Dictionary<CarrierId, int>();
        var splices = new Dictionary<CarrierId, int>();

        foreach (var box in Plan.Boxes)
        {
            foreach (var circuit in box.Circuits)
            {
                boxes.TryGetValue(circuit, out var seen);
                boxes[circuit] = seen + 1;
            }
        }

        foreach (var splice in Plan.Splices)
        {
            splices.TryGetValue(splice.Circuit, out var seen);
            splices[splice.Circuit] = seen + 1;
        }

        // Every box over its capacity, once, in the plan's order - so that the apply warns about the
        // same ones in the same order on every run of an unchanged model.
        var overfull = new List<PlannedBox>();

        foreach (var box in Plan.Boxes)
        {
            if (box.IsOverfull)
                overfull.Add(box);
        }

        _overfull = overfull;

        var failures = new List<RouteResult>();
        var statuses = new int[Enum.GetValues(typeof(RouteStatus)).Length];
        var length = 0.0;
        var builtIn = 0.0;
        var unsized = 0;

        foreach (var one in results)
        {
            var status = (int)one.Status;

            if (status >= 0 && status < statuses.Length)
                statuses[status]++;

            if (one.Status != RouteStatus.Found)
            {
                failures.Add(one);
                continue;
            }

            // Only the circuits cut in boxes: one cut at the terminal fills no box whatever its cable
            // has, so counting it among those nobody sized would overstate what is unknown.
            if (one.Connection == CircuitConnection.AtJunctionBox && one.Conductors <= 0)
                unsized++;

            boxes.TryGetValue(one.Circuit, out var cut);
            splices.TryGetValue(one.Circuit, out var spliced);

            // The cable factor is one until a cable says otherwise; reading it is its own piece of
            // work, and until it lands every circuit gets what the project decided.
            _slack[one.Circuit] = Slack.For(one.Measured, one.Taps.Count, cut, spliced);

            length += one.Measured + _slack[one.Circuit];
            builtIn += one.BuiltInLength;
        }

        _byStatus = statuses;
        Failures = failures;
        TotalLength = length;
        BuiltInLength = builtIn;
        WithoutConductors = unsized;
    }

    /// <summary>What the project adds beyond what the routes measure.</summary>
    public SlackRule Slack { get; }

    /// <summary>The slack this circuit's cable gets, in internal feet; zero if it did not route.</summary>
    public double SlackOf(CarrierId circuit) => _slack.TryGetValue(circuit, out var slack) ? slack : 0;

    /// <summary>
    /// What this circuit's cable measures with its slack, in internal feet; zero if it did not route.
    /// </summary>
    /// <remarks>
    /// The one number a cable schedule wants, and the reason it is asked of the run rather than of the
    /// route: slack is counted per place the cable is cut, and how many boxes that is was decided by
    /// the plan, after every circuit had been routed.
    /// </remarks>
    public double TotalLengthOf(CarrierId circuit) =>
        _slack.ContainsKey(circuit) ? Measured(circuit) + _slack[circuit] : 0;

    private double Measured(CarrierId circuit)
    {
        foreach (var one in Results)
        {
            if (one.Circuit == circuit && one.Status == RouteStatus.Found)
                return one.Measured;
        }

        return 0;
    }

    public IReadOnlyList<RouteResult> Results { get; }

    /// <summary>The network these were computed on, so a later answer can say it is stale.</summary>
    public long NetworkVersion { get; }

    /// <summary>
    /// What the side that read the model wants said, in its own words.
    /// </summary>
    /// <remarks>
    /// <b>Strings, carried and never read.</b> What a link is, and what it means for one to be
    /// placed but not loaded, is knowledge the Revit side has and the search does not. Composing
    /// them here would need that knowledge; leaving them out would mean a run reports half its
    /// circuits unroutable while the reason sits in a count nobody was shown.
    /// </remarks>
    public IReadOnlyList<string> Reading { get; init; } = Array.Empty<string>();

    /// <summary>What other join tolerances would have made of the same carriers.</summary>
    /// <remarks>
    /// Empty unless something failed to cross the structure. It is evidence rather than advice: a
    /// wider tolerance joins runs a person reads as joined, and also joins two that merely pass
    /// near each other - so the number to choose is a judgement about a model, and this is the table
    /// it is made from.
    /// </remarks>
    public IReadOnlyList<ToleranceReading> Tolerances { get; init; } = Array.Empty<ToleranceReading>();

    /// <summary>
    /// How the drops would differ if measured along a carrier rather than to its ends.
    /// </summary>
    /// <remarks>
    /// Null until asked for. It is a measurement standing in for a decision - see
    /// <see cref="ApproachStudy"/> - and it goes when the decision is made, one way or the other.
    /// </remarks>
    public ApproachComparison? Approach { get; init; }

    /// <summary>What that network looked like as a graph.</summary>
    /// <remarks>
    /// Carried on the run rather than fetched from the network by whoever displays it, because the
    /// network is not kept once the run is over - and the one question this answers is asked exactly
    /// when the run reports that it could not cross the structure.
    /// </remarks>
    public NetworkShape Shape { get; init; }

    /// <summary>The boxes the circuits cut in boxes need: existing ones used, and places recommended.</summary>
    /// <remarks>
    /// Planned here, with the routes, and not in the apply phase: the screen owes the designer the
    /// count before anything is written, and a count computed twice - once to show, once to place -
    /// is two answers that will one day disagree.
    /// </remarks>
    /// <remarks>
    /// <b>The whole plan rather than its boxes, since 2026-09-20, and that is what makes the pair
    /// impossible to half-set.</b> A plan has two halves - the boxes it needs and the splices that
    /// need none - and a run that carried only the first would answer "no splices" about a model full
    /// of them, silently. One property, filled from one call, cannot disagree with itself.
    /// </remarks>
    public BoxPlan Plan { get; }

    /// <summary>The boxes the circuits cut in boxes need: existing ones used, and places recommended.</summary>
    public IReadOnlyList<PlannedBox> Boxes => Plan.Boxes;

    /// <summary>The places a cable is cut in a carrier itself, where no box is needed.</summary>
    public IReadOnlyList<PlannedSplice> Splices => Plan.Splices;

    /// <summary>How many devices the circuits cut in boxes actually reached.</summary>
    /// <remarks>
    /// <b>Asked of the run rather than of the plan, and that moved when the tree replaced the
    /// chain.</b> A chain cut the cable at every device, so counting the drops the plan's boxes served
    /// answered both questions at once; a tree cuts it only where it splits, and a device at the end
    /// of a branch is served by no box at all. Counting devices from the plan would have gone on
    /// looking right and reported a fraction of them.
    /// </remarks>
    public int Served
    {
        get
        {
            var served = 0;

            foreach (var one in Results)
            {
                if (one.Status == RouteStatus.Found && one.Connection == CircuitConnection.AtJunctionBox)
                    served += one.Taps.Count;
            }

            return served;
        }
    }

    /// <summary>The boxes holding more conductors than their capacity, in the plan's order.</summary>
    /// <remarks>
    /// <b>A list rather than a count, because every one of them is addressed.</b> Each gets a warning
    /// Revit posts against that element, so the apply needs to know which; a tally could be shown and
    /// could not be pointed at - the same reason <c>JunctionBoxReader.Unconnected</c> keeps ids.
    /// Recommended boxes are never here: by the owner's answer they have no capacity.
    /// </remarks>
    public IReadOnlyList<PlannedBox> Overfull => _overfull;

    /// <summary>
    /// How many routed circuits cut in boxes say nothing about their conductors, and so fill nothing.
    /// </summary>
    /// <remarks>
    /// <b>Said out loud because a silent zero reads exactly like a box that fits.</b> Revit's
    /// conductor counts belong to a power circuit; a data circuit, or one nobody has sized, reports
    /// none, and then no capacity in the model can ever be exceeded by it. A screen that shows "no box
    /// is over its capacity" without saying that half the circuits were not counted has told the
    /// designer something untrue about their model.
    /// </remarks>
    public int WithoutConductors { get; }

    /// <summary>Whether the circuits cut in boxes were routed without additional boxes.</summary>
    /// <remarks>
    /// Carried on the run, not read again at write time: the apply writes the mode the run was computed
    /// with back into the project, and a mode read a second time is the one somebody changed in between.
    /// </remarks>
    public bool ExistingBoxesOnly { get; init; }

    /// <summary>How long the search itself took, without the reading that preceded it.</summary>
    /// <remarks>
    /// Apart from the read on purpose. Reading a model is dominated by how big the model is and how
    /// tired the machine is - this repository has measured the same wait at 16 s and at 696 s - while
    /// the search is dominated by what we wrote. Folded together, a regression in ours would hide
    /// inside the variance of theirs.
    /// </remarks>
    public TimeSpan Took { get; }

    public int Count(RouteStatus status)
    {
        var index = (int)status;
        return index >= 0 && index < _byStatus.Length ? _byStatus[index] : 0;
    }

    public int Found => Count(RouteStatus.Found);

    /// <summary>Total computed length, in internal feet, over the circuits that routed.</summary>
    public double TotalLength { get; }

    /// <summary>What Revit reports today for those same circuits.</summary>
    /// <remarks>
    /// Summed over the ones that routed, and only those - see <see cref="RouteResult.BuiltInLength"/>
    /// for why the pair travels on the result rather than being gathered from two sets here.
    /// </remarks>
    public double BuiltInLength { get; }

    /// <summary>The circuits that did not route, in the order they were tried.</summary>
    /// <remarks>
    /// Kept whole rather than summarised: the screen groups them by status, and a person then wants
    /// to know <i>which</i> circuit and <i>where</i> it stopped. <see cref="RouteResult.BlockedAt"/>
    /// carries the address for exactly this.
    /// </remarks>
    public IReadOnlyList<RouteResult> Failures { get; }

    /// <summary>The failures of one kind, for a screen that lists causes before circuits.</summary>
    public IEnumerable<RouteResult> Blocked(RouteStatus status) =>
        Failures.Where(one => one.Status == status);

    /// <summary>Every status that actually occurred, worst first, so a report can walk them.</summary>
    /// <remarks>
    /// <b>Only the ones that occurred.</b> A report that prints "NoConnectivity: 0" alongside the
    /// two causes that did happen buries them, and this repository has already written down what a
    /// report that speaks about nothing costs: it stops being read, and then it cannot speak about
    /// something either.
    /// </remarks>
    public IEnumerable<RouteStatus> Causes
    {
        get
        {
            foreach (var status in new[] { RouteStatus.NoCarrierNear, RouteStatus.NoConnectivity, RouteStatus.NoBoxReachable, RouteStatus.NothingToRoute })
            {
                if (Count(status) > 0)
                    yield return status;
            }
        }
    }
}

/// <summary>What one join tolerance makes of a set of carriers.</summary>
public readonly struct ToleranceReading
{
    public ToleranceReading(double tolerance, NetworkShape shape)
    {
        Tolerance = tolerance;
        Shape = shape;
    }

    /// <summary>The tolerance tried, in internal feet - the screen formats it as a length.</summary>
    public double Tolerance { get; }

    public NetworkShape Shape { get; }
}
