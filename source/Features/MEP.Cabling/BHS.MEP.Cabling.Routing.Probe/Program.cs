using System.Globalization;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Routing.Probe;

/// <summary>
/// Runs the routing core against networks small enough to reason about by hand.
/// </summary>
/// <remarks>
/// Every network here is drawn in the comment above it, because a graph written as coordinates is
/// unreadable and a wrong expectation in a check is worse than no check. Distances are in internal
/// feet, like everything on this side.
/// </remarks>
internal static class Program
{
    private const double Tolerance = 0.1;
    private const int Floor = 45;

    private static int _run;
    private static int _failed;

    private static int Main()
    {
        Console.WriteLine($"routing probe on {Environment.Version} / {RuntimeName()}");

        Grid();
        JoinsByProximity();
        PrefersConduit();
        ReportsWhyItFailed();
        ApproachIsAlongAxes();
        LengthIgnoresThePreference();
        TheRunGroupsItsFailures();
        TheStructureSaysHowManyPiecesItIsIn();
        AFittingJoinsOnEveryConnector();
        TheDropIsMeasuredToAnEndAndCouldBeMeasuredAlong();

        Console.WriteLine();

        if (_run < Floor)
        {
            Console.WriteLine($"  [FAIL] only {_run} checks ran, fewer than the {Floor} this probe is known to have");
            _failed++;
        }

        Console.WriteLine(_failed == 0
            ? $"== all checks passed ({_run})"
            : $"== {_failed} check(s) FAILED, out of {_run}");

        return _failed;
    }

    /// <summary>
    /// A straight run of three trays, a panel at one end and a socket at the other.
    /// <code>
    ///   P                                   S
    ///   |                                   |
    ///   +---[0]---+---[1]---+---[2]---+
    ///   0        10        20        30
    /// </code>
    /// </summary>
    private static void Grid()
    {
        Section("a straight run");

        var network = Trays(3, 10);

        var circuit = new CircuitSnapshot(
            new CarrierId(100),
            "P-1",
            Terminal(0, 0, 5, "panel"),
            new[] { Terminal(30, 0, 5, "socket") });

        var result = Router.Route(network, circuit, Options());

        Check("a route is found", result.Status == RouteStatus.Found);
        Check("it walks all three trays", result.Path.Count == 3);
        Check("its length is the run", Near(result.AlongCarriers, 30));
        Check("the drops are counted apart", Near(result.Approaches, 10));
        Check("and the total adds them", Near(result.TotalLength, 40));
    }

    /// <summary>
    /// Two runs with a gap between them: joined when the tolerance reaches, not otherwise.
    /// <code>
    ///   +---[0]---+   gap 0.5   +---[1]---+
    /// </code>
    /// </summary>
    private static void JoinsByProximity()
    {
        Section("a gap between runs");

        var carriers = new[]
        {
            Tray(1, 0, 10),
            Tray(2, 10.5, 20.5),
        };

        var circuit = new CircuitSnapshot(
            new CarrierId(100),
            "P-1",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(20.5, 0, 0, "socket") });

        var joined = Options(join: 1.0);
        var wide = Router.Route(NetworkBuilder.Build(1, carriers, joined), circuit, joined);
        Check("a tolerance wider than the gap joins them", wide.Status == RouteStatus.Found);

        var narrow = Router.Route(NetworkBuilder.Build(1, carriers, Options()), circuit, Options());
        Check("a tolerance narrower than the gap does not", narrow.Status == RouteStatus.NoConnectivity);

        // The predecessor walked Revit's connectors, which would answer "not connected" for both -
        // and a person looking at the model sees one trace. This is the whole reason the owner asked
        // for a distance instead.
        Check("and the narrow case names where it stopped", narrow.BlockedAt.Length > 0);
    }

    /// <summary>
    /// Two ways from the panel to the socket, the conduit one longer.
    /// <code>
    ///        +------ conduit 24 ------+
    ///   P ---+                        +--- S
    ///        +------ tray    20 ------+
    /// </code>
    /// </summary>
    private static void PrefersConduit()
    {
        Section("conduit against tray");

        var carriers = new[]
        {
            new CarrierNode(new CarrierId(1), CarrierKind.Segment, "conduit", 24, 0, P(0, 0, 0), P(20, 0, 0)),
            new CarrierNode(new CarrierId(2), CarrierKind.Segment, "tray", 20, 0, P(0, 1, 0), P(20, 1, 0)),
        };

        var network = NetworkBuilder.Build(1, carriers, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(100),
            "P-1",
            Terminal(0, 0.5, 0, "panel"),
            new[] { Terminal(20, 0.5, 0, "socket") });

        var plain = Router.Route(network, circuit, Options());
        Check("with no preference the shorter tray wins", plain.Path[0] == new CarrierId(2));

        var prefers = Router.Route(network, circuit, Options(preferConduit: 0.5));
        Check("with a preference the longer conduit wins", prefers.Path[0] == new CarrierId(1));
    }

    /// <summary>The three ways a circuit fails, told apart.</summary>
    /// <remarks>
    /// The predecessor returned an empty list for all of them, which is why twelve failures in a
    /// report could not be grouped into the two or three causes they actually are.
    /// </remarks>
    private static void ReportsWhyItFailed()
    {
        Section("why it failed");

        var network = Trays(2, 10);

        var far = Router.Route(
            network,
            new CircuitSnapshot(new CarrierId(1), "P-1", Terminal(0, 0, 500, "panel"),
                new[] { Terminal(20, 0, 0, "socket") }),
            Options());

        Check("a device out of reach is NoCarrierNear", far.Status == RouteStatus.NoCarrierNear);
        // Both halves, and the check earns its keep by having caught the day the first half was
        // added: the screen groups by cause and says "26 circuits", so a line naming only the device
        // answers at a different level from the heading and leaves every circuit unnamed.
        Check("and it names the circuit as well as the end that was out of reach",
            far.BlockedAt == "P-1 - panel");

        var empty = Router.Route(
            network,
            new CircuitSnapshot(new CarrierId(2), "P-2", Terminal(0, 0, 0, "panel"), Array.Empty<Terminal>()),
            Options());

        Check("a circuit with no devices is NothingToRoute", empty.Status == RouteStatus.NothingToRoute);

        var islands = NetworkBuilder.Build(1, new[] { Tray(1, 0, 10), Tray(2, 100, 110) }, Options());

        var split = Router.Route(
            islands,
            new CircuitSnapshot(new CarrierId(3), "P-3", Terminal(0, 0, 0, "panel"),
                new[] { Terminal(110, 0, 0, "socket") }),
            Options());

        Check("two islands are NoConnectivity", split.Status == RouteStatus.NoConnectivity);
    }

    /// <summary>A socket three feet across and four feet below a tray.</summary>
    private static void ApproachIsAlongAxes()
    {
        Section("the drop to a device");

        // The reach admits both answers on purpose. Along the axes a drop is longer than the
        // straight line, so a device that is reachable one way can be out of reach the other - real
        // behaviour worth knowing, and not what this check is about.
        var axesOptions = Options(reach: 10);
        var straightOptions = Options(axisAligned: false, reach: 10);
        var network = Trays(1, 10, axesOptions);

        var circuit = new CircuitSnapshot(
            new CarrierId(100),
            "P-1",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(3, 0, -4, "socket") });

        var axes = Router.Route(network, circuit, axesOptions);
        var straight = Router.Route(network, circuit, straightOptions);

        // A cable runs across and then down: 3 + 4. The straight line is the hypotenuse, 5, and it
        // is shorter than anything anyone installs.
        Check("along the axes the drop is 3+4", Near(axes.Approaches, 7));
        Check("straight it would be the hypotenuse", Near(straight.Approaches, 5));
        Check("so the axis-aligned answer is the longer one", axes.Approaches > straight.Approaches);
    }

    /// <summary>The preference steers the choice and must not reach the reported length.</summary>
    private static void LengthIgnoresThePreference()
    {
        Section("the preference is not in the answer");

        var carriers = new[] { Tray(1, 0, 10) };
        var network = NetworkBuilder.Build(1, carriers, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(100),
            "P-1",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(10, 0, 0, "socket") });

        var plain = Router.Route(network, circuit, Options());
        var weighted = Router.Route(network, circuit, Options(preferConduit: 3.0));

        // A tray weighted four times over still measures ten feet. Reporting the search's cost here
        // would inflate the number written into a parameter and disagree with a tape measure.
        Check("a weighted tray still measures its length", Near(weighted.AlongCarriers, plain.AlongCarriers));
    }

    // ---- the small stuff ------------------------------------------------------------------------

    private static RouteNetwork Trays(int count, double each, RoutingOptions? options = null)
    {
        var carriers = new CarrierNode[count];

        for (var i = 0; i < count; i++)
            carriers[i] = Tray(i + 1, i * each, (i + 1) * each);

        return NetworkBuilder.Build(1, carriers, options ?? Options());
    }

    /// <summary>
    /// What a whole run says about itself, rather than what one circuit says.
    /// </summary>
    /// <remarks>
    /// Built from results directly rather than by routing: the grouping is arithmetic over statuses
    /// and lengths, and running a search to produce them would test the search again and this not at
    /// all. The failures here are the shapes a real run produces - some of each cause, and one cause
    /// that did not happen.
    /// </remarks>
    private static void TheRunGroupsItsFailures()
    {
        Section("what a run says about itself");

        var results = new List<RouteResult>
        {
            Routed(1, alongCarriers: 10, approaches: 2, builtIn: 11),
            Routed(2, alongCarriers: 20, approaches: 3, builtIn: 21),
            Failed(3, RouteStatus.NoCarrierNear, "Socket 3", builtIn: 99),
            Failed(4, RouteStatus.NoCarrierNear, "Socket 4", builtIn: 99),
            Failed(5, RouteStatus.NothingToRoute, "SP-1, way 12", builtIn: 0),
        };

        var run = new RouteRun(results, networkVersion: 7, took: TimeSpan.FromSeconds(1.5));

        Check("it counts what routed", run.Found == 2);
        Check("and counts each cause apart", run.Count(RouteStatus.NoCarrierNear) == 2);

        // The one that never happened. A report that prints it as zero buries the two that did.
        Check("a cause that did not happen is not a cause",
            !run.Causes.Contains(RouteStatus.NoConnectivity));

        Check("the causes that did happen are both there",
            run.Causes.SequenceEqual(new[] { RouteStatus.NoCarrierNear, RouteStatus.NothingToRoute }));

        Check("failures keep the order they were tried in",
            run.Failures.Select(one => one.Circuit.Value).SequenceEqual(new long[] { 3, 4, 5 }));

        Check("and can be asked for one cause at a time",
            run.Blocked(RouteStatus.NothingToRoute).Single().BlockedAt == "SP-1, way 12");

        Check("length is summed over what routed", Near(run.TotalLength, 35));

        // The pair that must come from one set. Revit's number for the three that did not route is
        // 198 ft here on purpose: if either total ever drifts onto all five results, this goes red.
        Check("and Revit's number over those same circuits, not all of them",
            Near(run.BuiltInLength, 32));

        Check("the version travels with the run", run.NetworkVersion == 7);
    }

    /// <summary>
    /// Whether the structure is one thing or many, which is the question "no connectivity" raises.
    /// </summary>
    /// <remarks>
    /// <code>
    ///   +--[0]--+--[1]--+        +--[2]--+        joined at 0.05, apart by 5
    ///   0      10      20       25      35
    /// </code>
    /// The two cases it has to tell apart are the two a real model produces: everything joined but
    /// for a stray, and nothing joined to anything. The second is a tolerance, the first is a model.
    /// </remarks>
    /// <summary>
    /// A branch joins, even though it is not one of the two points that lie farthest apart.
    /// </summary>
    /// <remarks>
    /// <code>
    ///                      +--[3]-- branch tray, from (5,0,0) to (5,10,0)
    ///                      |
    ///   --[0]--+--[1 tee]--+--[2]--
    ///   0     10          14      24
    ///           tee terminals: (10,0,0) (14,0,0) (12,0,0)
    /// </code>
    /// The tee's extremes are its two through connectors; the branch sits between them and is
    /// nearer to neither than two feet. This is the defect measured on a real model: the branch was
    /// discarded, so the tray on it joined nothing, and half the circuits reported "no connectivity"
    /// over a structure that was drawn correctly.
    /// </remarks>
    /// <summary>
    /// What measuring the drop along a carrier would change, against measuring it to the ends.
    /// </summary>
    /// <remarks>
    /// <code>
    ///   +--------------------[0]--------------------+     tray, 0 -> 20 along X
    ///   0                     |                    20
    ///                         | 1 down
    ///                         S   socket at (10, 0, -1)
    /// </code>
    /// Measured to the ends the socket is ten across and one down, i.e. eleven along the axes, and
    /// out of a three-foot reach. Measured along the tray it is one. This is the defect in the
    /// small, and the study exists to say how much of it a real model contains.
    /// </remarks>
    private static void TheDropIsMeasuredToAnEndAndCouldBeMeasuredAlong()
    {
        Section("the drop to a carrier, along it and to its ends");

        var tray = Tray(0, 0, 20);

        Check("the nearest point on a run is under the device",
            Near(P(10, 0, -1).NearestOn(tray.Start, tray.End).DistanceTo(P(10, 0, 0)), 0));

        Check("and beyond the run it is the end, not a point in the air",
            Near(P(30, 0, -1).NearestOn(tray.Start, tray.End).DistanceTo(P(20, 0, 0)), 0));

        Check("a carrier answers with that point", Near(tray.NearestPointTo(P(10, 0, -1)).X, 10));

        var network = NetworkBuilder.Build(1, new[] { tray }, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(10, 0, -1, "socket") });

        // Terminal() keys its CarrierId off x, so two ends at the same x would collide; the panel
        // sits on the tray's start and the socket under its middle.
        var study = ApproachStudy.Compare(network, new[] { circuit }, Options());

        Check("both ends are looked at", study.Terminals == 2);

        // The panel is on the end and reachable either way; the socket is eleven feet away by the
        // ends and one foot along the tray, and the reach is six.
        Check("the socket is out of reach measured to the ends", study.ReachedByTerminals == 1);
        Check("and in reach measured along the carrier", study.ReachedByNearest == 2);
        Check("which the study calls a gain", study.Gained == 1);
        Check("and it does not call that agreement", !study.Agree);
    }

    private static void AFittingJoinsOnEveryConnector()
    {
        Section("a fitting joins on every connector");

        var tee = new CarrierNode(
            new CarrierId(1), CarrierKind.Fitting, "tray", 0, 0.05,
            P(10, 0, 0), P(14, 0, 0),
            new[] { P(10, 0, 0), P(14, 0, 0), P(12, 0, 0) });

        var branch = new CarrierNode(
            new CarrierId(3), CarrierKind.Segment, "tray", 10, 0.05,
            P(12, 0, 0), P(12, 10, 0));

        var network = NetworkBuilder.Build(1, new[] { Tray(0, 0, 10), tee, Tray(2, 14, 24), branch }, Options());

        Check("the branch is not one of the extremes",
            !Near(tee.Start.DistanceTo(P(12, 0, 0)), 0) && !Near(tee.End.DistanceTo(P(12, 0, 0)), 0));

        Check("and the tee still reaches it", network.Neighbours(new CarrierId(1)).Contains(new CarrierId(3)));
        Check("both ways", network.Neighbours(new CarrierId(3)).Contains(new CarrierId(1)));
        Check("so the whole thing is one piece", network.Shape().Groups == 1);

        // The count is not three: a fitting may carry any number of connectors, so nothing anywhere
        // assumes a shape. Five here, four of them join points nothing else would have found.
        var manifold = new CarrierNode(
            new CarrierId(4), CarrierKind.Fitting, "tray", 0, 0.05,
            P(0, 0, 0), P(4, 0, 0),
            new[] { P(0, 0, 0), P(4, 0, 0), P(1, 0, 0), P(2, 0, 0), P(3, 0, 0) });

        var onMiddle = new CarrierNode(
            new CarrierId(5), CarrierKind.Segment, "tray", 10, 0.05,
            P(2, 0, 0), P(2, 10, 0));

        var many = NetworkBuilder.Build(2, new[] { manifold, onMiddle }, Options());

        Check("a fitting with five connectors joins on the middle one",
            many.Neighbours(new CarrierId(4)).Contains(new CarrierId(5)));

        Check("and a route can be found across it", many.Shape().Groups == 1);
    }

    private static void TheStructureSaysHowManyPiecesItIsIn()
    {
        Section("how many pieces the structure is in");

        var carriers = new[] { Tray(0, 0, 10), Tray(1, 10, 20), Tray(2, 25, 35) };

        var together = NetworkBuilder.Build(1, carriers, Options()).Shape();

        Check("it counts every carrier", together.Carriers == 3);
        Check("the gap of five splits them in two", together.Groups == 2);
        Check("and names the bigger piece", together.Largest == 2);

        // The same carriers, with a tolerance wide enough to close the gap a person reads as a
        // joint. One group is what a model that routes looks like.
        var reached = NetworkBuilder.Build(2, carriers, Options(join: 6)).Shape();

        Check("a wider tolerance makes it one piece", reached.Groups == 1);
        Check("holding all of them", reached.Largest == 3);
    }

    private static RouteResult Routed(long circuit, double alongCarriers, double approaches, double builtIn) =>
        new(new CarrierId(circuit), RouteStatus.Found, 7)
        {
            AlongCarriers = alongCarriers,
            Approaches = approaches,
            BuiltInLength = builtIn,
        };

    private static RouteResult Failed(long circuit, RouteStatus status, string blockedAt, double builtIn) =>
        new(new CarrierId(circuit), status, 7)
        {
            BlockedAt = blockedAt,
            BuiltInLength = builtIn,
        };

    private static CarrierNode Tray(long id, double from, double to) =>
        new(new CarrierId(id), CarrierKind.Segment, "tray", to - from, 0.05, P(from, 0, 0), P(to, 0, 0));

    private static Point3 P(double x, double y, double z) => new(x, y, z);

    private static Terminal Terminal(double x, double y, double z, string label) =>
        new(new CarrierId(900 + (long)x), P(x, y, z), label);

    private static RoutingOptions Options(double preferConduit = 0, bool axisAligned = true,
        double join = Tolerance, double reach = 6) =>
        new()
        {
            JoinTolerance = join,
            MaxApproach = reach,
            PreferConduitUntil = preferConduit,
            AxisAlignedApproach = axisAligned,
        };

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static void Section(string what)
    {
        Console.WriteLine();
        Console.WriteLine("== " + what);
    }

    private static void Check(string what, bool ok)
    {
        _run++;

        if (!ok)
            _failed++;

        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    }

    private static string RuntimeName() =>
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}
