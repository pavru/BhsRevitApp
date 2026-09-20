using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// Why circuits came back <see cref="RouteStatus.NoCarrierNear"/>, as notes: which end stopped them, how
/// far that end is from the structure, and where its point came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written for one measured result that nobody can explain.</b> The attended run of 2026-09-14 on the
/// linked set - Revit 2026, forty carriers, thirteen circuits - found no route at all with every circuit
/// cut in boxes: thirteen of thirteen NoCarrierNear, and the six cases that need a planned box stood down.
/// Three readings fit that, and they want different fixes: the model's ends are farther from the
/// structure than the reach; the options the cases pass are not the command's; or the reader put an end
/// or a linked carrier in the wrong place. Each note below is there to tell one of them from the others.
/// </para>
/// <para>
/// <b>Notes only, and nothing here may change what a case asserts.</b> The cases call this before they
/// stand down, which is where the answer is needed; an exception thrown from a diagnostic there would
/// turn a skip into "threw before it could assert" for a reason that has nothing to do with the case. So
/// any exception is caught, and only its type name is noted - never its message, which is Revit's and
/// may name things in the owner's model.
/// </para>
/// <para>
/// <b>Which end stopped a circuit is read from <see cref="RouteResult.BlockedAt"/>, and never written
/// down.</b> The router stops a leg at the first end that has nothing within reach, and records only that
/// end's label after the circuit's number; the status is the same whether it was the panel or a device.
/// A label is an element name with its id, so comparing it for equality against each end of the circuit
/// finds one - or one element at two ends, counted as such, where the end the copy of the rule stops at
/// is taken - and the id and the role are what is noted. The label, the number and
/// <c>BlockedAt</c> itself carry the names of panels, circuits and types, and never reach a note.
/// </para>
/// <para>
/// <b>The router's rule is written again here, and pinned before it is used.</b> The copy picks a point
/// on each candidate - the nearest along an open run, the straight-line nearest terminal of a conduit or
/// a fitting - and measures to it with the approach metric. Its first unreachable end is compared with
/// the end the router named, and its counts with <see cref="ApproachStudy"/>, which shares the router's
/// metric but not its rule for conduits. A disagreement is noted as one; a distance from a copy that
/// disagrees would be a distance about something the router does not do.
/// </para>
/// </remarks>
internal static class ReachNotes
{
    /// <summary>The command's default join tolerance, restated for a note and never for an assertion.</summary>
    /// <remarks>
    /// <c>CablingOptions</c> keeps it internal to the feature assembly, which this one does not reference.
    /// If the command's default moves, this note goes stale; no case asserts anything against it.
    /// </remarks>
    private const double CommandJoinToleranceMm = 50;

    /// <summary>The command's default reach, restated for a note and never for an assertion.</summary>
    private const double CommandMaxApproachMm = 3000;

    /// <summary>How many ends and links a single call notes one by one before folding the rest.</summary>
    private const int Rows = 12;

    /// <summary>Notes why the NoCarrierNear results among <paramref name="results"/> stopped.</summary>
    /// <param name="context">Where the notes go.</param>
    /// <param name="label">What every note written here is prefixed with.</param>
    /// <param name="document">The host the circuits were read from.</param>
    /// <param name="network">The network the results were routed over, built with <paramref name="options"/>.</param>
    /// <param name="circuits">The circuits that were routed, as they were routed.</param>
    /// <param name="results">What the router returned for them.</param>
    /// <param name="options">The options the network was built and the circuits were routed with.</param>
    /// <param name="catalogue">The catalogue the carriers were read with, to read a link again.</param>
    /// <param name="boxes">The indicator the carriers were read with, to read a link again.</param>
    public static void Explain(
        RevitTestContext context,
        string label,
        Document document,
        RouteNetwork network,
        IReadOnlyList<CircuitSnapshot> circuits,
        IReadOnlyList<RouteResult> results,
        RoutingOptions options,
        CarrierCatalogue catalogue,
        RecommendedBoxes? boxes)
    {
        context.Note(label + ": options passed, against the command's defaults", DescribeOptions(options));
        context.Note(label + ": routes by status", ByStatus(results));

        try
        {
            Detail(context, label, document, network, circuits, results, options, catalogue, boxes);
        }
        catch (Exception error) when (error is not RevitTestSkipped and not RevitTestFailure)
        {
            // See the remarks: a note must not become a failure, and the message stays out.
            context.Note(label + ": notes stopped by an exception", error.GetType().Name);
        }
    }

    /// <summary>A run's results counted by status, as one note value.</summary>
    public static string ByStatus(IReadOnlyList<RouteResult> results) =>
        "found " + Count(results, RouteStatus.Found)
        + ", no carrier near " + Count(results, RouteStatus.NoCarrierNear)
        + ", no connectivity " + Count(results, RouteStatus.NoConnectivity)
        + ", no box reachable " + Count(results, RouteStatus.NoBoxReachable)
        + ", nothing to route " + Count(results, RouteStatus.NothingToRoute);

    /// <summary>
    /// Whether two routings of the same circuits, in the same order, stopped at the same end where both
    /// stopped for want of a carrier.
    /// </summary>
    /// <remarks>
    /// Compared as text and never printed: <see cref="RouteResult.BlockedAt"/> is the circuit's number
    /// and the end's label, and both carry names from the model.
    /// </remarks>
    public static string SameEnd(IReadOnlyList<RouteResult> one, IReadOnlyList<RouteResult> other)
    {
        var both = 0;
        var same = 0;
        var onlyOne = 0;

        for (var index = 0; index < Math.Min(one.Count, other.Count); index++)
        {
            var first = one[index].Status == RouteStatus.NoCarrierNear;
            var second = other[index].Status == RouteStatus.NoCarrierNear;

            if (first && second)
            {
                both++;

                if (string.Equals(one[index].BlockedAt, other[index].BlockedAt, StringComparison.Ordinal))
                    same++;
            }
            else if (first || second)
            {
                onlyOne++;
            }
        }

        return same + " of " + both + " stopped at the same end; NoCarrierNear in one mode only: " + onlyOne;
    }

    private static void Detail(
        RevitTestContext context,
        string label,
        Document document,
        RouteNetwork network,
        IReadOnlyList<CircuitSnapshot> circuits,
        IReadOnlyList<RouteResult> results,
        RoutingOptions options,
        CarrierCatalogue catalogue,
        RecommendedBoxes? boxes)
    {
        var blocked = results.Where(one => one.Status == RouteStatus.NoCarrierNear).ToList();

        if (blocked.Count == 0)
            return;

        var byId = new Dictionary<CarrierId, CircuitSnapshot>();

        foreach (var circuit in circuits)
        {
            if (!byId.ContainsKey(circuit.Id))
                byId[circuit.Id] = circuit;
        }

        var nodes = network.Nodes.ToList();
        var groups = new List<BlockedEnd>();
        var atPanel = 0;
        var atDevice = 0;
        var unidentified = 0;
        var matchedTwice = 0;
        var copyAgrees = 0;

        foreach (var result in blocked)
        {
            if (!byId.TryGetValue(result.Circuit, out var circuit))
            {
                unidentified++;
                continue;
            }

            var ends = new List<Terminal> { circuit.Source };
            ends.AddRange(circuit.Devices);

            var matches = new List<int>();

            for (var index = 0; index < ends.Count; index++)
            {
                if (string.Equals(result.BlockedAt, circuit.Number + " - " + ends[index].Label, StringComparison.Ordinal))
                    matches.Add(index);
            }

            if (matches.Count == 0)
            {
                unidentified++;
                continue;
            }

            // Two matches mean one element at two ends, and possibly at two points: the panel's comes
            // from its feed or the panel ladder and the device's from the device ladder, so the panel
            // point can reach a carrier where the device point does not. The end the copy of the rule
            // stops at is taken when it is one of them; the copy then agrees by that choice, which is
            // why the matches are counted beside it.
            if (matches.Count > 1)
                matchedTwice++;

            var first = FirstUnreachable(network, ends, options);
            var stoppedAt = matches.Contains(first) ? first : matches[0];

            if (stoppedAt == 0)
                atPanel++;
            else
                atDevice++;

            if (first == stoppedAt)
                copyAgrees++;

            var end = ends[stoppedAt];
            var group = groups.FirstOrDefault(one => one.End.Owner == end.Owner && SamePoint(one.End.At, end.At));

            if (group is null)
            {
                group = new BlockedEnd(end, stoppedAt == 0);
                groups.Add(group);
            }

            group.Circuits.Add(circuit.Id.Value);
        }

        context.Note(
            label + ": circuits NoCarrierNear, by the end they stopped at",
            "at the panel " + atPanel + ", at a device " + atDevice + ", end not identified " + unidentified
            + "; ends matched more than once " + matchedTwice);

        context.Note(
            label + ": the case's copy of the router's rule stops at the same end",
            copyAgrees + " of " + (atPanel + atDevice));

        var indexMisses = 0;

        foreach (var group in groups)
        {
            group.Take(document, nodes, network, options);

            if (group.Nearest is not null && group.Distance <= options.MaxApproach && !group.OfferedByTheIndex)
                indexMisses++;
        }

        context.Note(
            label + ": distinct blocked ends",
            groups.Count + " (panels " + groups.Count(one => one.AsPanel) + ", devices " + groups.Count(one => !one.AsPanel) + ")");

        context.Note(
            label + ": blocked ends a carrier reaches by brute force, which the index did not offer",
            indexMisses.ToString(CultureInfo.InvariantCulture));

        context.Note(label + ": ends reached, by the approach study, its copy here, and the router's rule", Reached(network, nodes, circuits, options));
        context.Note(label + ": heights, mm", Heights(nodes, circuits));

        Links(context, label, document, nodes, groups, options, catalogue, boxes);

        var ordered = groups
            .OrderBy(one => one.AsPanel ? 0 : 1)
            .ThenBy(one => one.End.Owner.Value)
            .ToList();

        for (var index = 0; index < Math.Min(Rows, ordered.Count); index++)
        {
            var group = ordered[index];

            // One element at two points - a panel fed through two connectors, say - is two ends, and the
            // labels have to say which note is which without printing coordinates twice.
            var points = ordered.Count(one => one.End.Owner == group.End.Owner);
            var twin = points > 1
                ? " at point " + (ordered.Take(index + 1).Count(one => one.End.Owner == group.End.Owner)) + " of " + points
                : string.Empty;

            context.Note(
                label + ": blocked at " + (group.AsPanel ? "panel " : "device ") + group.End.Owner.Value.ToString(CultureInfo.InvariantCulture) + twin,
                group.Describe(options));
        }

        if (ordered.Count > Rows)
            context.Note(label + ": blocked ends folded", (ordered.Count - Rows).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The first end, in the order the router visits them, that the router's rule gives nothing within
    /// reach of - or -1.
    /// </summary>
    /// <remarks>
    /// Panel, then each device in turn, in both connection modes: in a box a later leg starts from the
    /// trunk rather than from the device before it, but that device was already asked as the end of the
    /// leg before, and a circuit that reached NoCarrierNear passed every leg before the one that stopped.
    /// </remarks>
    private static int FirstUnreachable(RouteNetwork network, IReadOnlyList<Terminal> ends, RoutingOptions options)
    {
        for (var index = 0; index < ends.Count; index++)
        {
            if (Indexed(network, ends[index].At, options) > options.MaxApproach)
                return index;
        }

        return -1;
    }

    /// <summary>The smallest approach, by the router's rule, over the carriers the index offers a point.</summary>
    private static double Indexed(RouteNetwork network, Point3 at, RoutingOptions options)
    {
        var best = double.MaxValue;

        foreach (var node in network.Near(at))
            best = Math.Min(best, Measure(at, Target(node, at), options));

        return best;
    }

    /// <summary>Where the router's rule meets a carrier from a point: along an open run, else at a terminal.</summary>
    private static Point3 Target(CarrierNode node, Point3 at) =>
        node.OpenAlongItsLength ? node.NearestPointTo(at) : NearestTerminal(node, at);

    /// <summary>The router's <c>NearestTerminal</c>, again: straight-line nearest, the first on a tie.</summary>
    private static Point3 NearestTerminal(CarrierNode node, Point3 at)
    {
        var best = node.Terminals.Count > 0 ? node.Terminals[0] : node.Start;
        var distance = at.DistanceTo(best);

        for (var index = 1; index < node.Terminals.Count; index++)
        {
            var candidate = at.DistanceTo(node.Terminals[index]);

            if (candidate >= distance)
                continue;

            distance = candidate;
            best = node.Terminals[index];
        }

        return best;
    }

    /// <summary>The approach metric, again: along the axes when the options say so, else straight.</summary>
    private static double Measure(Point3 from, Point3 to, RoutingOptions options) =>
        options.AxisAlignedApproach
            ? Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) + Math.Abs(from.Z - to.Z)
            : from.DistanceTo(to);

    /// <summary>
    /// Ends reached, counted three ways over every end of every circuit: the study's two, this copy of
    /// them, and the router's rule.
    /// </summary>
    /// <remarks>
    /// The study measures to the nearest terminal by the metric itself and projects onto every run, where
    /// the router takes a conduit's straight-line nearest terminal and never projects onto one - so neither
    /// of the study's counts is the router's, and the third number is noted beside them rather than
    /// compared with them. What the comparison pins is the metric.
    /// </remarks>
    private static string Reached(RouteNetwork network, IReadOnlyList<CarrierNode> nodes, IReadOnlyList<CircuitSnapshot> circuits, RoutingOptions options)
    {
        var study = ApproachStudy.Compare(network, circuits, options);
        var ends = 0;
        var toTerminals = 0;
        var alongCarriers = 0;
        var byTheRule = 0;

        foreach (var circuit in circuits)
        {
            foreach (var end in new[] { circuit.Source }.Concat(circuit.Devices))
            {
                ends++;

                var terminals = double.MaxValue;
                var along = double.MaxValue;

                foreach (var node in nodes)
                {
                    foreach (var terminal in node.Terminals)
                        terminals = Math.Min(terminals, Measure(end.At, terminal, options));

                    along = Math.Min(along, Measure(end.At, node.NearestPointTo(end.At), options));
                }

                if (terminals <= options.MaxApproach)
                    toTerminals++;

                if (along <= options.MaxApproach)
                    alongCarriers++;

                if (Indexed(network, end.At, options) <= options.MaxApproach)
                    byTheRule++;
            }
        }

        return "to terminals: study " + study.ReachedByTerminals + ", copy " + toTerminals
               + "; along carriers: study " + study.ReachedByNearest + ", copy " + alongCarriers
               + "; by the router's rule " + byTheRule
               + "; of " + ends + " ends (study " + study.Terminals + ")";
    }

    /// <summary>The height ranges of the circuit ends and of the carriers, host and linked apart.</summary>
    private static string Heights(IReadOnlyList<CarrierNode> nodes, IReadOnlyList<CircuitSnapshot> circuits)
    {
        var ends = circuits.SelectMany(one => new[] { one.Source }.Concat(one.Devices)).Select(one => one.At.Z).ToList();
        var host = nodes.Where(one => !one.Id.IsLinked).SelectMany(Points).Select(one => one.Z).ToList();
        var linked = nodes.Where(one => one.Id.IsLinked).SelectMany(Points).Select(one => one.Z).ToList();

        return "circuit ends " + Range(ends) + "; host carriers " + Range(host) + "; linked carriers " + Range(linked);
    }

    private static IEnumerable<Point3> Points(CarrierNode node) =>
        new[] { node.Start, node.End }.Concat(node.Terminals);

    private static string Range(IReadOnlyList<double> heights) =>
        heights.Count == 0 ? "none" : Mm(heights.Min()) + ".." + Mm(heights.Max());

    /// <summary>
    /// Each loaded link: whether its transform moves it, and how near its carriers come to the blocked
    /// ends with the transform and without it.
    /// </summary>
    /// <remarks>
    /// Read again through the production reader with <see cref="Transform.Identity"/>, which is the only
    /// thing that differs from the snapshot: a link whose carriers reach the blocked ends only without
    /// its transform is a transform applied wrongly, not a model built far from its devices.
    /// </remarks>
    private static void Links(
        RevitTestContext context,
        string label,
        Document document,
        IReadOnlyList<CarrierNode> nodes,
        IReadOnlyList<BlockedEnd> groups,
        RoutingOptions options,
        CarrierCatalogue catalogue,
        RecommendedBoxes? boxes)
    {
        var loaded = 0;
        var noted = 0;

        foreach (var link in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var linked = link.GetLinkDocument();

            if (linked is null)
                continue;

            loaded++;

            if (noted == Rows)
                continue;

            noted++;

            var source = link.Id.Value;
            var transform = link.GetTotalTransform();
            var placed = nodes.Where(one => one.Id.Source == source).ToList();
            var unmoved = new CarrierReader(catalogue, boxes).Read(linked, source, Transform.Identity).ToList();
            var rotated = !transform.BasisX.IsAlmostEqualTo(XYZ.BasisX) || !transform.BasisY.IsAlmostEqualTo(XYZ.BasisY);

            var with = Nearest(groups, placed, options);
            var without = Nearest(groups, unmoved, options);
            var reachedWithout = groups.Count(group => unmoved.Any(node => Measure(group.End.At, Target(node, group.End.At), options) <= options.MaxApproach));

            context.Note(
                label + ": link " + source.ToString(CultureInfo.InvariantCulture),
                "transform identity " + YesNo(transform.IsIdentity) + ", moved " + Mm(transform.Origin.GetLength()) + ", rotated " + YesNo(rotated)
                + "; carriers in the network " + placed.Count + ", read again without the transform " + unmoved.Count
                + "; nearest to a blocked end " + (with < double.MaxValue ? Mm(with) : "none") + " with the transform, "
                + (without < double.MaxValue ? Mm(without) : "none") + " without"
                + "; blocked ends its carriers would reach without the transform " + reachedWithout + " of " + groups.Count);
        }

        context.Note(label + ": links loaded", loaded.ToString(CultureInfo.InvariantCulture));

        if (loaded > noted)
            context.Note(label + ": links folded", (loaded - noted).ToString(CultureInfo.InvariantCulture));
    }

    private static double Nearest(IReadOnlyList<BlockedEnd> groups, IReadOnlyList<CarrierNode> carriers, RoutingOptions options)
    {
        var best = double.MaxValue;

        foreach (var group in groups)
        {
            foreach (var node in carriers)
                best = Math.Min(best, Measure(group.End.At, Target(node, group.End.At), options));
        }

        return best;
    }

    private static string DescribeOptions(RoutingOptions options)
    {
        var join = Millimetres(options.JoinTolerance);
        var reach = Millimetres(options.MaxApproach);

        return "join " + Decimal(join) + " mm, reach " + Decimal(reach) + " mm, axis-aligned " + YesNo(options.AxisAlignedApproach)
               + ", conduit preference " + Decimal(options.PreferConduitUntil)
               + ", single-device circuits " + (options.SkipSingleDeviceCircuits ? "skipped" : "routed")
               + "; the command's defaults: join " + Decimal(CommandJoinToleranceMm) + " mm, reach " + Decimal(CommandMaxApproachMm)
               + " mm, axis-aligned yes, conduit preference 0, length extension 0, single-device circuits routed"
               + " - so reach " + Signed(reach - CommandMaxApproachMm) + " mm and join " + Signed(join - CommandJoinToleranceMm) + " mm against them";
    }

    private static int Count(IReadOnlyList<RouteResult> results, RouteStatus status) =>
        results.Count(one => one.Status == status);

    private static bool SamePoint(Point3 one, Point3 other) =>
        Math.Abs(one.X - other.X) <= 1e-9 && Math.Abs(one.Y - other.Y) <= 1e-9 && Math.Abs(one.Z - other.Z) <= 1e-9;

    /// <summary>Internal feet to millimetres, asked of Revit as the command converts the other way.</summary>
    private static double Millimetres(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    private static string Mm(double feet) =>
        Millimetres(feet).ToString("F0", CultureInfo.InvariantCulture) + " mm";

    private static string SignedMm(double feet) =>
        Signed(Millimetres(feet)) + " mm";

    private static string Signed(double value) =>
        value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    private static string Decimal(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>One end that stopped at least one circuit, and what is measured about it.</summary>
    private sealed class BlockedEnd
    {
        public BlockedEnd(Terminal end, bool asPanel)
        {
            End = end;
            AsPanel = asPanel;
        }

        public Terminal End { get; }

        /// <summary>Whether it stopped its first circuit as that circuit's panel, rather than as a device.</summary>
        public bool AsPanel { get; }

        /// <summary>The circuits it stopped, by element id.</summary>
        public List<long> Circuits { get; } = new();

        /// <summary>The carrier nearest by the router's rule, over every carrier rather than the index's.</summary>
        public CarrierNode? Nearest { get; private set; }

        /// <summary>Where the router's rule meets that carrier.</summary>
        public Point3 Target { get; private set; }

        /// <summary>The approach to that point, by the metric.</summary>
        public double Distance { get; private set; } = double.MaxValue;

        /// <summary>Whether the index offers a carrier within reach, which it cannot for an end that stopped a route.</summary>
        public bool OfferedByTheIndex { get; private set; }

        /// <summary>The straight distance to the nearest point along any carrier, conduits included.</summary>
        public double Body { get; private set; } = double.MaxValue;

        /// <summary>The way the reader took, again, and whether its point is the end's.</summary>
        public ReaderLadder.Answer Way { get; private set; }

        public bool WayRefused { get; private set; }

        public bool WayIsTheReaders { get; private set; }

        /// <summary>For a guessed point: the height of the box and how far its centre is from the insertion point.</summary>
        public string Guess { get; private set; } = string.Empty;

        public void Take(Document document, IReadOnlyList<CarrierNode> nodes, RouteNetwork network, RoutingOptions options)
        {
            var at = End.At;

            foreach (var node in nodes)
            {
                var target = ReachNotes.Target(node, at);
                var distance = Measure(at, target, options);

                if (distance < Distance)
                {
                    Distance = distance;
                    Nearest = node;
                    Target = target;
                }

                Body = Math.Min(Body, at.DistanceTo(node.NearestPointTo(at)));
            }

            OfferedByTheIndex = Indexed(network, at, options) <= options.MaxApproach;

            var system = document.GetElement(new ElementId(Circuits[0])) as ElectricalSystem;
            var element = document.GetElement(new ElementId(End.Owner.Value));

            if (system is null || element is null)
                return;

            try
            {
                Way = AsPanel ? ReaderLadder.Panel(system) : ReaderLadder.Device(element, system);
                WayIsTheReaders = ReaderLadder.Same(Way.At, at);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException refused)
                when (refused.GetType() == typeof(Autodesk.Revit.Exceptions.InvalidOperationException))
            {
                // Not expected on an end the reader described: it asked the same members without
                // catching. Said as a refusal rather than allowed to end the notes for every other end.
                // The exact type only: a subclass - a stale element, a disabled discipline - is not a
                // refusal of this way, and ends the notes under its own type name.
                WayRefused = true;
                return;
            }

            if (!ReaderLadder.IsGuess(Way.Way))
                return;

            var box = ReaderLadder.Box(element);
            var inserted = (element.Location as LocationPoint)?.Point;

            Guess = "its bounding box " + (box is null ? "none" : Mm(box.Max.Z - box.Min.Z) + " tall")
                    + ", centre to insertion point " + (box is null || inserted is null ? "not known" : Mm(((box.Min + box.Max) / 2).DistanceTo(inserted)));
        }

        public string Describe(RoutingOptions options)
        {
            var parts = new List<string>
            {
                "circuits " + Circuits.Count + " [" + string.Join(", ", Circuits.Take(8).Select(one => one.ToString(CultureInfo.InvariantCulture)))
                + (Circuits.Count > 8 ? ", ..." : string.Empty) + "]",
            };

            var ways = AsPanel ? ReaderLadder.PanelWays : ReaderLadder.DeviceWays;

            parts.Add(
                WayRefused
                    ? "point from a way Revit refused to answer here"
                    : "point from " + ways[Way.Way] + (WayIsTheReaders ? string.Empty : ", which does not give the point the reader put it at"));

            if (Nearest is not { } node)
            {
                parts.Add("no carrier in the network");
            }
            else
            {
                var dx = Target.X - End.At.X;
                var dy = Target.Y - End.At.Y;

                parts.Add(
                    "nearest carrier " + node.Id + " " + node.Class + " " + node.Kind + (node.Id.IsLinked ? " in a link" : " in the host")
                    + ", " + Mm(Distance) + " by the approach metric, " + Mm(End.At.DistanceTo(Target)) + " straight to the same point");

                parts.Add("nearest along any carrier, conduits not held to their ends, " + Mm(Body) + " straight");

                parts.Add(
                    "to it: height " + SignedMm(Target.Z - End.At.Z) + ", across " + Mm(Math.Abs(dx) + Math.Abs(dy)) + " by the axes, "
                    + Mm(Math.Sqrt((dx * dx) + (dy * dy))) + " straight");

                parts.Add(
                    Distance > options.MaxApproach
                        ? "reach " + Mm(options.MaxApproach) + ", over it by " + Mm(Distance - options.MaxApproach)
                        : "reach " + Mm(options.MaxApproach) + ", within it by " + Mm(options.MaxApproach - Distance) + " by brute force");
            }

            if (Guess.Length > 0)
                parts.Add(Guess);

            return string.Join("; ", parts);
        }
    }
}
