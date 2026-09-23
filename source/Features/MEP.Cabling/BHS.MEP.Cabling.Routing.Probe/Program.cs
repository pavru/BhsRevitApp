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
    private const int Floor = 193;

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
        ACableLeavesATrayWhereItLikesAndPaysForWhatItWalks();
        ACableCutInBoxesComesDownOnce();
        BoxesAreWhereTheTapsAreAndCountWhatTheyTake();
        WithoutAdditionalBoxesEveryDeviceIsServedFromOneThatStands();
        WhereTheCarrierAllowsASpliceNoBoxIsAskedFor();
        WhatASpliceCostsDecidesWhereTheCableIsCut();
        WhatATerminalHoldsDecidesWhetherItMayBranch();
        ABoxTheCableOnlyPassesThroughFeedsNothing();
        WhatABoxHoldsIsReportedAndChangesNothing();
        ACircuitLiesOnlyInCarriersThatAdmitIt();
        SlackIsCountedWhereTheCableIsCut();
        TheLengthIsToldByWhereItIsLaid();
        TheLengthIsToldByHowItIsLaid();
        AStoredLengthIsToldFromAStaleOne();

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
        Check("and the two together are what the route measures", Near(result.Measured, 40));
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
            new CarrierNode(new CarrierId(1), CarrierKind.Segment, "conduit", false, 24, 0, P(0, 0, 0), P(20, 0, 0)),
            new CarrierNode(new CarrierId(2), CarrierKind.Segment, "tray", true, 20, 0, P(0, 1, 0), P(20, 1, 0)),
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

        // Three across the run and four below it. The offset used to be along the run, which stopped
        // saying anything the day the drop began to be taken to the nearest point on the run rather
        // than to its end: the socket was then directly underneath, and both measures answered four.
        // The arithmetic is the same 3-4-5; only the direction of the offset had to move.
        var circuit = new CircuitSnapshot(
            new CarrierId(100),
            "P-1",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(3, 3, -4, "socket") });

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

        // The same network as conduit rather than tray: a cable leaves a pipe where it joins
        // something, so the tray-only figure falls back to the ends and the gain disappears.
        var pipe = new CarrierNode(
            new CarrierId(0), CarrierKind.Segment, "conduit", false, 20, 0.05, P(0, 0, 0), P(20, 0, 0));

        // The decomposition the screen states, and it needs a reach that both measures can see:
        // a terminal only one of them reaches is excluded from the sums by construction, because a
        // difference of sums over different sets is not a saving. Shown red exactly there.
        var wide = Options(reach: 12);
        var onTray = ApproachStudy.Compare(network, new[] { circuit }, wide);

        Check("both terminals are comparable when both measures reach them", onTray.Comparable == 2);
        Check("on a tray the saving costs nothing", Near(onTray.BoxSaving, 0) && onTray.FreeSaving > 0);

        var onPipe = ApproachStudy.Compare(
            NetworkBuilder.Build(3, new[] { pipe }, wide), new[] { circuit }, wide);

        Check("a conduit measured to its ends and along it come to the same",
            Near(onPipe.ByNearestOnTrays, onPipe.ByTerminals));

        Check("while a tray does not", !Near(onTray.ByNearestOnTrays, onTray.ByTerminals));
        Check("on a conduit it all waits on a box", Near(onPipe.FreeSaving, 0) && onPipe.BoxSaving > 0);
        Check("and the two halves add up to the whole",
            Near(onTray.FreeSaving + onTray.BoxSaving, onTray.ByTerminals - onTray.ByNearest));

        // The defect this measure had until the probe went red: a terminal out of reach one way and
        // in reach the other made the difference of two sums negative, i.e. a saving that unsaves.
        Check("a terminal only one measure reaches is left out of the sums", study.Comparable == 1);
        Check("so no saving can come out negative", study.FreeSaving >= 0 && study.BoxSaving >= 0);
    }

    /// <summary>
    /// A socket under the middle of one long tray: the search has to reach it, and to charge for
    /// the half of the tray the cable actually walks.
    /// <code>
    ///   P                    S
    ///   +---------[0]---------+
    ///   0         10         20
    ///             |
    ///             s  (1 ft below the tray, at x = 10)
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two defects of one origin, and both are measured elsewhere as sums rather than as
    /// behaviour.</b> <see cref="ApproachStudy"/> already says the drop to a run should be taken to
    /// the nearest point along it and quantifies what that is worth on a real model; what it cannot
    /// say is what the search does with that point, because the search does not use it. Here it
    /// costs a route outright: the socket is a foot below the tray and eleven feet from either end,
    /// so at any ordinary reach the circuit is reported <c>NoCarrierNear</c>.
    /// </para>
    /// <para>
    /// The second is the price of entering mid-run. Every carrier on a path contributes its whole
    /// length exactly once - deliberately, and the probe guards it, because without it a route that
    /// enters and leaves the same carrier pays nothing for it. But a cable that joins a
    /// twenty-foot tray at its middle and leaves at one end walks ten feet, not twenty, and the
    /// invariant as written cannot express the difference.
    /// </para>
    /// <para>
    /// <b>A tray only.</b> A conduit is a pipe: a cable comes out where the pipe ends or at a
    /// fitting, so measuring to a point along it would buy a saving that cannot be built. That
    /// distinction is the whole of the difference between the two figures the study reports.
    /// </para>
    /// </remarks>
    private static void ACableLeavesATrayWhereItLikesAndPaysForWhatItWalks()
    {
        Section("entering a tray in the middle");

        var network = NetworkBuilder.Build(1, new[] { Tray(0, 0, 20) }, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(10, 0, -1, "socket") });

        var result = Router.Route(network, circuit, Options());

        // Eleven feet to either end, one foot to the tray itself, and a reach of six.
        Check("a socket under the middle of a tray is reachable", result.Status == RouteStatus.Found);
        Check("the drop is measured to the tray, not to its end", Near(result.Approaches, 1));
        Check("and the cable pays for the half it walks", Near(result.AlongCarriers, 10));

        // The same geometry as a pipe, where the saving cannot be built.
        var pipe = new CarrierNode(
            new CarrierId(0), CarrierKind.Segment, "conduit", false, 20, 0.05, P(0, 0, 0), P(20, 0, 0));

        var inPipe = Router.Route(
            NetworkBuilder.Build(2, new[] { pipe }, Options(reach: 12)),
            circuit,
            Options(reach: 12));

        Check("a cable does not leave a conduit mid-run", Near(inPipe.Approaches, 11));

        // Both ends of the pipe are eleven feet from the socket, so the cable enters and leaves at
        // the same one - and a carrier entered and left at one point has not been walked. The old
        // answer here was twenty, because a carrier on a path used to cost its whole length however
        // little of it was used; twenty feet of pipe that no cable is inside is not a length anybody
        // would cut.
        Check("and a pipe entered and left at one end is not walked at all", Near(inPipe.AlongCarriers, 0));

        // The invariant that made the old rule worth having, stated where it can still be broken:
        // a route that goes in one end and out the other pays for all of it.
        var across = new CircuitSnapshot(
            new CarrierId(3), "P-2",
            Terminal(0, 0, 0, "panel"),
            new[] { Terminal(20, 0, -1, "socket") });

        var through = Router.Route(
            NetworkBuilder.Build(4, new[] { pipe }, Options(reach: 12)),
            across,
            Options(reach: 12));

        Check("while a pipe walked end to end costs all of it", Near(through.AlongCarriers, 20));
    }

    /// <summary>
    /// The two ways of connecting a device, on one tray and three sockets under it.
    /// <code>
    ///   +=========================+        tray, 0 to 30, at z = 0
    ///   P        S1       S2       S3      all one foot below it
    ///   0        10       20       30
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cut at each terminal</b>, every leg climbs up from where it starts and comes down where it
    /// ends: the panel's one foot, then two feet at each socket but the last, then the last - six.
    /// <b>Cut in boxes</b>, the trunk climbs once from the panel and stays up, and each socket gets
    /// one spur - one plus three, four. The carriers walked are the same thirty feet either way.
    /// </para>
    /// <para>
    /// This is the owner's difference, written as arithmetic. On the first real model the drops were
    /// 38 % of the headline, and the terminal mode counts every intermediate one twice.
    /// </para>
    /// </remarks>
    private static void ACableCutInBoxesComesDownOnce()
    {
        Section("a cable cut in boxes comes down once per device");

        var network = NetworkBuilder.Build(1, new[] { Tray(0, 0, 30) }, Options());
        var devices = new[] { Terminal(10, 0, -1, "S1"), Terminal(20, 0, -1, "S2"), Terminal(30, 0, -1, "S3") };

        var atTerminals = Router.Route(
            network,
            new CircuitSnapshot(new CarrierId(1), "P-1", Terminal(0, 0, -1, "panel"), devices),
            Options());

        var inBoxes = Router.Route(
            network,
            new CircuitSnapshot(new CarrierId(1), "P-1", Terminal(0, 0, -1, "panel"), devices)
            {
                Connection = CircuitConnection.AtJunctionBox,
            },
            Options());

        Check("cut at the terminals, each intermediate drop is walked down and back up", Near(atTerminals.Approaches, 6));
        Check("cut in boxes, the trunk climbs once and each device gets one spur", Near(inBoxes.Approaches, 4));
        Check("and the carriers walked are the same either way",
            Near(inBoxes.AlongCarriers, 30) && Near(atTerminals.AlongCarriers, 30));
        Check("the result says which way it was routed", inBoxes.Connection == CircuitConnection.AtJunctionBox);

        Check("there is a tap for every device, in the order they are visited",
            inBoxes.Taps.Count == 3
            && inBoxes.Taps[0].Device.Label == "S1"
            && inBoxes.Taps[2].Device.Label == "S3");

        Check("each tap is on the tray, above its device",
            Near(inBoxes.Taps[0].At.X, 10) && Near(inBoxes.Taps[1].At.X, 20)
            && Near(inBoxes.Taps[2].At.X, 30) && Near(inBoxes.Taps[1].At.Z, 0));

        Check("and each spur is the foot down to it", Near(inBoxes.Taps[1].Spur, 1));
        Check("taps are recorded in the terminal mode as well - it is still where the cable comes down",
            atTerminals.Taps.Count == 3);
    }

    /// <summary>
    /// The planner, over results written by hand - it is arithmetic over taps, and producing them by
    /// routing would test the search again and this not at all.
    /// </summary>
    /// <remarks>
    /// Every rule checked here is an answer the owner gave on 2026-09-11: a box at every device, the
    /// last one included; one box per place shared by every circuit; its count is every cable entry;
    /// and one radius decides both merging and whether an existing box is used.
    /// </remarks>
    private static void BoxesAreWhereTheTapsAreAndCountWhatTheyTake()
    {
        Section("boxes where the cable is cut");

        // One circuit, cut in three places far apart.
        var chain = InBoxes(1, 10, 20, 30);
        var three = BoxPlanner.Plan(new[] { chain }, Array.Empty<ExistingBox>(), radius: 1.5).Boxes;

        Check("a box wherever the cable is cut", three.Count == 3);
        Check("each takes the trunk in and out and one drop - three",
            three[0].Entries == 3 && three[1].Entries == 3 && three[2].Entries == 3);
        Check("all of them are recommendations, nothing stands there yet", three.All(box => box.IsRecommendation));

        // Two cuts of one circuit closer than the radius share a box, and the run between them is
        // inside it - so the box holds four ends, not six.
        var close = BoxPlanner.Plan(new[] { InBoxes(1, 10, 11, 20) }, Array.Empty<ExistingBox>(), radius: 1.5).Boxes;

        Check("cuts closer than the radius share one box", close.Count == 2);
        Check("which takes the trunk in, the trunk out and both drops - four", close[0].Entries == 4);
        Check("and it stands at the first of them, on the structure, not between them", Near(close[0].At.X, 10));
        Check("both drops are recorded against it", close[0].Spurs == 2);

        // Two circuits cut at the same place share one box, and it counts both - they share no run.
        var shared = BoxPlanner.Plan(new[] { InBoxes(1, 10), InBoxes(2, 10) }, Array.Empty<ExistingBox>(), radius: 1.5).Boxes;

        Check("two circuits cut in one place share one box", shared.Count == 1);
        Check("which names both circuits", shared[0].Circuits.Count == 2);
        Check("and takes what both bring - three and three", shared[0].Entries == 6);

        // An existing box within the radius is used; one nobody reaches is not part of the answer.
        var existing = new[] { new ExistingBox(new CarrierId(500), P(20.5, 0, 0)), new ExistingBox(new CarrierId(501), P(100, 0, 0)) };
        var withReal = BoxPlanner.Plan(new[] { chain }, existing, radius: 1.5).Boxes;

        Check("an existing box within the radius is used instead of recommending one",
            withReal.Count(box => !box.IsRecommendation) == 1
            && withReal.Single(box => !box.IsRecommendation).Existing!.Id == new CarrierId(500));
        Check("and an existing box nobody reaches is left out", withReal.Count == 3);

        // A device at the end of a branch is not a cut at all - the owner's rule, and the whole
        // difference between a tree and the chain that put a box at every device including the last.
        var leaf = new RouteResult(new CarrierId(4), RouteStatus.Found, 7)
        {
            Connection = CircuitConnection.AtJunctionBox,
            Taps = new[] { new Tap(Terminal(40, 0, -1, "S40"), new CarrierId(0), P(40, 0, 0), 1) },
        };

        Check("a device the cable simply ends in asks for no box",
            BoxPlanner.Plan(new[] { leaf }, Array.Empty<ExistingBox>(), 1.5).Boxes.Count == 0);

        // A circuit cut at its terminals asks for no boxes at all.
        var terminals = BoxPlanner.Plan(new[] { InBoxes(3, 10, 20).With(CircuitConnection.AtTerminal) }, Array.Empty<ExistingBox>(), 1.5).Boxes;

        Check("a circuit cut at its terminals asks for no boxes", terminals.Count == 0);
    }

    /// <summary>
    /// Routed without additional boxes: two boxes stand in the structure, three sockets hang under it.
    /// <code>
    ///   +====T0====X==========T1==========Y====T2====+     trays at z = 0, boxes X at 10, Y at 30
    ///   P                  S1          S3        S2        all one foot below
    ///   0                  18          24        38
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's rules of 2026-09-17, as arithmetic. S1 is 8 from X along the tray and 12 from Y, so
    /// X; S3 is 14 from X and 6 from Y, so Y; S2 is 8 from Y. None of them is within a radius of any box,
    /// so the ordinary mode would recommend three boxes and this one recommends none.
    /// </para>
    /// <para>
    /// The trunk climbs once at the panel and runs P - X - Y, thirty feet; S2 and S3 share Y's one visit.
    /// The spurs walk 8, 8 and 6 along the tray and come down a foot each. Along the structure 52, drops 4.
    /// The boxes are fittings of no length, so passing through one costs nothing to reason about.
    /// </para>
    /// </remarks>
    private static void WithoutAdditionalBoxesEveryDeviceIsServedFromOneThatStands()
    {
        Section("without additional boxes, every device is served from a box that stands");

        var x = Box(50, 10);
        var y = Box(51, 30);
        var network = NetworkBuilder.Build(1, new[] { Tray(0, 0, 10), x, Tray(1, 10, 30), y, Tray(2, 30, 40) }, Options());
        var standing = new[] { new ExistingBox(new CarrierId(50), P(10, 0, 0)), new ExistingBox(new CarrierId(51), P(30, 0, 0)) };

        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(18, 0, -1, "S1"), Terminal(38, 0, -1, "S2"), Terminal(24, 0, -1, "S3") })
        {
            Connection = CircuitConnection.AtJunctionBox,
        };

        var routed = Router.Route(network, circuit, Options(), standing);
        Check("a circuit whose every device reaches a box is routed", routed.Status == RouteStatus.Found);
        Check("each device is served from the box nearest it along the structure, however far",
            routed.Taps.Count == 3
            && routed.Taps[0].Box == new CarrierId(50)
            && routed.Taps[1].Box == new CarrierId(51)
            && routed.Taps[2].Box == new CarrierId(51));
        Check("each spur walks the tray from its box to above the device",
            Near(routed.Taps[0].SpurAlongCarriers, 8) && Near(routed.Taps[1].SpurAlongCarriers, 8)
            && Near(routed.Taps[2].SpurAlongCarriers, 6));
        Check("and comes down a foot", routed.Taps.All(tap => Near(tap.Spur, 1)));
        Check("along the structure: the trunk P - X - Y once, thirty, and the spurs, twenty-two",
            Near(routed.AlongCarriers, 52));
        Check("drops: the panel once and a foot per device", Near(routed.Approaches, 4));
        Check("trunk and spurs alike are counted under the class they were walked along",
            Near(routed.AlongClass("tray"), 52) && routed.AlongByClass.Count == 1);

        var planned = BoxPlanner.Plan(new[] { routed }, standing, radius: 1.5).Boxes;

        Check("the planner recommends nothing, although no tap is within a radius of a box",
            planned.Count == 2 && planned.All(box => !box.IsRecommendation));
        // Three ends at each: the cable arrives, goes on, and a second run leaves for another device.
        // The spurs are what hangs off each - and every one of the three is farther from its box than
        // the radius, which is the whole of this mode. The spur counts stood at zero here until
        // 2026-09-21, written to match a planner that had quietly stopped reading the box a tap
        // carries and was giving taps out by nearness alone; on the owner's linked set that left ten
        // devices of twelve served by nothing, and the probe agreed with it.
        Check("X holds three cable ends - the trunk in, the trunk on, the run that leaves it - and feeds one device",
            planned.SingleOrDefault(box => box.Existing?.Id == new CarrierId(50)) is { Entries: 3, Spurs: 1 });
        Check("and Y the same, feeding two: the device its own run leaves for, and the one the trunk goes on to",
            planned.SingleOrDefault(box => box.Existing?.Id == new CarrierId(51)) is { Entries: 3, Spurs: 2 });
        Check("so every device of the circuit is served, and none twice",
            planned.Sum(box => box.Spurs) == routed.Taps.Count);

        var ordinary = Router.Route(network, circuit, Options());

        Check("the ordinary mode on the same circuit gives no box to any tap", ordinary.Taps.All(tap => tap.Box is null));
        Check("and the same circuit, free to recommend, is cut in two places rather than three",
            BoxPlanner.Plan(new[] { Router.Route(network, circuit, Options()) }, Array.Empty<ExistingBox>(), 1.5).Boxes.Count == 2);

        var atTerminals = new CircuitSnapshot(circuit.Id, circuit.Number, circuit.Source, circuit.Devices);

        Check("a circuit cut at its terminals ignores the boxes",
            Router.Route(network, atTerminals, Options(), standing).Taps.All(tap => tap.Box is null));

        // A socket under a tray of its own, which no box reaches through the structure.
        var island = NetworkBuilder.Build(2, new[] { Tray(0, 0, 10), x, Tray(1, 10, 30), y, Tray(3, 100, 110) }, Options());
        var stranded = new CircuitSnapshot(
            new CarrierId(2), "P-2",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(18, 0, -1, "S1"), Terminal(105, 0, -1, "S9") })
        {
            Connection = CircuitConnection.AtJunctionBox,
        };

        var unserved = Router.Route(island, stranded, Options(), standing);

        Check("a device no box reaches fails the circuit, and says which", unserved.Status == RouteStatus.NoBoxReachable
            && unserved.BlockedAt == "P-2 - S9");
        Check("and so does a model with no boxes at all",
            Router.Route(network, circuit, Options(), Array.Empty<ExistingBox>()).Status == RouteStatus.NoBoxReachable);
    }

    /// <summary>
    /// A conduit into a tray into trunking the project named itself, a panel under the conduit's end and a
    /// socket under the trunking.
    /// <code>
    ///   P                                             S
    ///   |                                             |
    ///   +==[conduit]==+---[tray]---+~~[trunking]~~+~~~+
    ///   0            10           30             38  40
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cable walks the conduit whole, ten, the tray, twenty, and the trunking to above the socket,
    /// eight: thirty-eight along. It comes down a foot at each end: two. With a tenth for slack the slack
    /// is 4 of the 40, and the total 44.
    /// </para>
    /// <para>
    /// Until 2026-09-17 the slack was added into the length along carriers, 42 here, which no breakdown
    /// by carrier could add up to.
    /// </para>
    /// </remarks>
    private static void TheLengthIsToldByWhereItIsLaid()
    {
        Section("the length, told by where it is laid");

        var carriers = new[]
        {
            new CarrierNode(new CarrierId(1), CarrierKind.Segment, "conduit", false, 10, 0.05, P(0, 0, 0), P(10, 0, 0)),
            Tray(2, 10, 30),
            new CarrierNode(new CarrierId(3), CarrierKind.Segment, "trunking", true, 10, 0.05, P(30, 0, 0), P(40, 0, 0)),
        };

        var options = new RoutingOptions
        {
            JoinTolerance = Tolerance,
            MaxApproach = 6,
            AxisAlignedApproach = true,
        };

        // A tenth of what is measured and nothing per place: the old rule, expressed in the new one,
        // so that the numbers below stay the numbers this section has always asserted.
        var tenth = new SlackRule { Fraction = 0.1 };

        var network = NetworkBuilder.Build(1, carriers, options);
        var circuit = new CircuitSnapshot(
            new CarrierId(100), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(38, 0, -1, "socket") });

        var routed = Router.Route(network, circuit, options);

        Check("the route is found", routed.Status == RouteStatus.Found);
        Check("the conduit is walked whole", Near(routed.AlongClass("conduit"), 10));
        Check("the tray is walked whole", Near(routed.AlongClass("tray"), 20));
        Check("the trunking is walked to above the socket, under the class the project named",
            Near(routed.AlongClass("trunking"), 8));
        Check("a class asked for regardless of case is the same class", Near(routed.AlongClass("CONDUIT"), 10));
        Check("the parts add up to the length along carriers, and that length holds no slack",
            Near(routed.AlongByClass.Values.Sum(), routed.AlongCarriers) && Near(routed.AlongCarriers, 38));
        var run = new RouteRun(new[] { routed }, 1, TimeSpan.Zero, BoxPlan.Empty, tenth);

        Check("the slack is a tenth of what is laid, drops included, and stands apart",
            Near(routed.Approaches, 2) && Near(run.SlackOf(circuit.Id), 4));
        Check("and the total is what the route measured plus it", Near(run.TotalLengthOf(circuit.Id), 44));

        var none = Router.Route(network, circuit, Options());
        var bare = new RouteRun(new[] { none }, 1, TimeSpan.Zero);

        Check("with no slack asked for there is none, and a class the route never walked reads zero",
            Near(bare.SlackOf(circuit.Id), 0) && Near(bare.TotalLengthOf(circuit.Id), 40)
            && Near(none.AlongClass("busway"), 0));
    }

    /// <summary>
    /// Two trays of one class laid by one method typed two ways, a conduit whose type says nothing, and a
    /// third method on a tray the route never reaches.
    /// <code>
    ///   P                                             S
    ///   |                                             |
    ///   +==[conduit]==+---[tray A]---+---[tray B]---+~~~+
    ///   0            10             30             38  40
    ///      (none)        Лоток          "лоток "
    /// </code>
    /// </summary>
    /// <remarks>
    /// The same geometry as the section above, so the length along carriers is the same 38. Tray B is
    /// spelled with a different case and a trailing space and has to land with tray A: two people typed
    /// it. The conduit says nothing and lands under the empty key - which is what the screen names, so it
    /// must not quietly join a neighbour.
    /// </remarks>
    private static void TheLengthIsToldByHowItIsLaid()
    {
        Section("the length, told by how it is laid");

        var carriers = new[]
        {
            new CarrierNode(new CarrierId(1), CarrierKind.Segment, "conduit", false, 10, 0.05, P(0, 0, 0), P(10, 0, 0)),
            new CarrierNode(new CarrierId(2), CarrierKind.Segment, "tray", true, 20, 0.05, P(10, 0, 0), P(30, 0, 0))
            {
                Method = "Лоток",
            },
            new CarrierNode(new CarrierId(3), CarrierKind.Segment, "tray", true, 10, 0.05, P(30, 0, 0), P(40, 0, 0))
            {
                Method = "лоток ",
            },
            new CarrierNode(new CarrierId(4), CarrierKind.Segment, "tray", true, 10, 0.05, P(0, 50, 0), P(10, 50, 0))
            {
                Method = "Кабель-канал",
            },
        };

        var options = new RoutingOptions
        {
            JoinTolerance = Tolerance,
            MaxApproach = 6,
            AxisAlignedApproach = true,
        };

        var network = NetworkBuilder.Build(1, carriers, options);
        var circuit = new CircuitSnapshot(
            new CarrierId(100), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(38, 0, -1, "socket") });

        var routed = Router.Route(network, circuit, options);

        Check("the route is found", routed.Status == RouteStatus.Found);
        Check("both trays are one method, whatever case and spacing it was typed in",
            Near(routed.AlongMethod("Лоток"), 28) && Near(routed.AlongMethod("ЛОТОК"), 28));
        Check("the conduit whose type says nothing lands under the empty key, not with a neighbour",
            Near(routed.AlongMethod(string.Empty), 10));
        Check("a method the route never walked reads zero", Near(routed.AlongMethod("Кабель-канал"), 0));
        Check("the methods add up to the length along carriers, as the classes do",
            Near(routed.AlongByMethod.Values.Sum(), routed.AlongCarriers)
            && Near(routed.AlongByClass.Values.Sum(), routed.AlongCarriers)
            && Near(routed.AlongCarriers, 38));
        Check("and the class view is untouched by the method: both trays are still one class",
            Near(routed.AlongClass("tray"), 28) && Near(routed.AlongClass("conduit"), 10));
    }

    /// <summary>
    /// What a circuit carries from an earlier apply, against what the search finds today.
    /// <code>
    ///   P                       S
    ///   |                       |
    ///   +---[1]---+---[2]---+---+
    ///   0        10        20
    /// </code>
    /// </summary>
    /// <remarks>
    /// The route walks both trays and comes down a foot at each end: along 20, drops 2, total 22. Each
    /// case below stores something different against that and asks what the review makes of it.
    /// </remarks>
    private static void AStoredLengthIsToldFromAStaleOne()
    {
        Section("a stored length against the one found today");

        var network = NetworkBuilder.Build(1, new[] { Tray(1, 0, 10), Tray(2, 10, 20) }, Options());
        var circuit = new CircuitSnapshot(
            new CarrierId(100), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(20, 0, -1, "socket") });

        var routed = Router.Route(network, circuit, Options());
        var walked = RouteStamp.Of(routed.Path);

        Check("the route walks both trays", routed.Status == RouteStatus.Found && Near(routed.Measured, 22));
        Check("the stamp names them in order, each once, as ids", walked == "1; 2");
        Check("and a stamp reads back into the carriers it names",
            RouteStamp.Parse(walked).SequenceEqual(new[] { new CarrierId(1), new CarrierId(2) }));
        Check("a carrier in a link keeps its link in both directions",
            RouteStamp.Of(new[] { new CarrierId(7, 3) }) == "7:3"
            && RouteStamp.Parse("7:3").Single() == new CarrierId(7, 3));

        var results = new[] { routed };

        Check("what the last apply wrote, unchanged, is current",
            !Review(results, Stored(routed.Circuit, 22, CircuitConnection.AtTerminal, walked)).Any);

        var longer = Review(results, Stored(routed.Circuit, 23, CircuitConnection.AtTerminal, walked));

        Check("a length that no longer matches is stale, and both numbers are named",
            longer.Stale.Count == 1 && longer.Stale[0].Has(StaleReason.LengthDiffers)
            && Near(longer.Stale[0].StoredLength, 23) && Near(longer.Stale[0].ComputedLength, 22));
        Check("and only for that reason", longer.Stale[0].Reasons == StaleReason.LengthDiffers);

        Check("a difference inside the tolerance is not a change",
            !Review(results, Stored(routed.Circuit, 22.0005, CircuitConnection.AtTerminal, walked)).Any);

        // A tree has no single order to walk, so a stamp written by an earlier version of us - or by
        // a search that settled its branches in the other order - names the same measurement. Reading
        // that as a change would report every circuit in a model as stale, which is the failure that
        // looks exactly like a finding.
        Check("the same carriers in the other order are the same measurement",
            !Review(results, Stored(routed.Circuit, 22, CircuitConnection.AtTerminal, "2; 1")).Any);
        Check("and a carrier named twice is still named once",
            !Review(results, Stored(routed.Circuit, 22, CircuitConnection.AtTerminal, "2; 1; 2")).Any);

        var elsewhere = Review(results, Stored(routed.Circuit, 22, CircuitConnection.AtTerminal, "1; 9"));

        Check("a stamp naming other carriers is stale, and says which left and which arrived",
            elsewhere.Stale.Count == 1 && elsewhere.Stale[0].Has(StaleReason.CarriersDiffer)
            && elsewhere.Stale[0].Left.SequenceEqual(new[] { new CarrierId(9) })
            && elsewhere.Stale[0].Arrived.SequenceEqual(new[] { new CarrierId(2) }));

        var other = Review(results, Stored(routed.Circuit, 22, CircuitConnection.AtJunctionBox, walked));

        Check("a length computed in the other connection is stale, and both are named",
            other.Stale.Count == 1 && other.Stale[0].Has(StaleReason.ConnectionDiffers)
            && other.Stale[0].StoredConnection == CircuitConnection.AtJunctionBox
            && other.Stale[0].ComputedConnection == CircuitConnection.AtTerminal);

        var blocked = new[] { Failed(100, RouteStatus.NoCarrierNear, "P-1 - socket", 0) };
        var gone = Review(blocked, Stored(new CarrierId(100), 22, CircuitConnection.AtTerminal, walked));

        Check("a length stored where nothing routes any more is stale, and says where it stopped",
            gone.Stale.Count == 1 && gone.Stale[0].Reasons == StaleReason.NoRouteNow
            && gone.Stale[0].BlockedAt == "P-1 - socket");
        Check("and it still names the carriers the stored length was measured along",
            gone.Stale[0].Left.SequenceEqual(new[] { new CarrierId(1), new CarrierId(2) }));

        var nothing = LengthReview.Of(
            new RouteRun(results, 7, TimeSpan.Zero), new Dictionary<CarrierId, StoredRoute>(), Millimetre);

        Check("a circuit nobody ever wrote is counted apart, not reported as stale",
            !nothing.Any && nothing.NeverWritten == 1 && nothing.Current == 0 && nothing.Examined == 1);
        Check("and a circuit that neither routes nor carries anything is neither",
            LengthReview.Of(
                new RouteRun(blocked, 7, TimeSpan.Zero),
                new Dictionary<CarrierId, StoredRoute>(),
                Millimetre) is { Stale.Count: 0, NeverWritten: 0, Current: 0, Examined: 1 });
    }

    /// <summary>A millimetre in internal feet, the tolerance the command uses.</summary>
    private const double Millimetre = 1 / 304.8;

    private static LengthReview Review(IReadOnlyList<RouteResult> results, StoredRoute stored) =>
        LengthReview.Of(
            new RouteRun(results, 7, TimeSpan.Zero),
            new Dictionary<CarrierId, StoredRoute> { [stored.Circuit] = stored },
            Millimetre);

    private static StoredRoute Stored(CarrierId circuit, double length, CircuitConnection connection, string stamp) =>
        new(circuit, length, connection, stamp);

    /// <summary>A box: a fitting of no length with one connector, where the trays either side of it meet.</summary>
    private static CarrierNode Box(long id, double at) =>
        new(new CarrierId(id), CarrierKind.Fitting, "tray", true, 0, 0.05, P(at, 0, 0), P(at, 0, 0), new[] { P(at, 0, 0) });

    /// <summary>A found route cut in boxes, with a tap on the tray above each x given.</summary>
    /// <summary>
    /// A circuit whose cable is cut in a box at each of these places, each box feeding one device.
    /// </summary>
    /// <remarks>
    /// Three ends apiece - the trunk arriving, the trunk going on, and the drop to the device - which
    /// is the ordinary shape of a box on a run. Written by hand rather than routed, because the
    /// planner is arithmetic over branches and producing them by routing would test the search again
    /// and the planner not at all.
    /// </remarks>
    private static RouteResult InBoxes(long circuit, params double[] branches) =>
        new(new CarrierId(circuit), RouteStatus.Found, 7)
        {
            Connection = CircuitConnection.AtJunctionBox,
            Taps = branches.Select(x => new Tap(Terminal(x, 0, -1, "S" + x), new CarrierId(0), P(x, 0, 0), 1)).ToArray(),
            Branches = branches.Select(x => new Branch(new CarrierId(0), P(x, 0, 0), 2)).ToArray(),
        };

    private static RouteResult With(this RouteResult route, CircuitConnection connection) =>
        new(route.Circuit, route.Status, route.NetworkVersion)
        {
            Connection = connection,
            Taps = route.Taps,
            Branches = route.Branches,
        };

    private static void AFittingJoinsOnEveryConnector()
    {
        Section("a fitting joins on every connector");

        var tee = new CarrierNode(
            new CarrierId(1), CarrierKind.Fitting, "tray", true, 0, 0.05,
            P(10, 0, 0), P(14, 0, 0),
            new[] { P(10, 0, 0), P(14, 0, 0), P(12, 0, 0) });

        var branch = new CarrierNode(
            new CarrierId(3), CarrierKind.Segment, "tray", true, 10, 0.05,
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
            new CarrierId(4), CarrierKind.Fitting, "tray", true, 0, 0.05,
            P(0, 0, 0), P(4, 0, 0),
            new[] { P(0, 0, 0), P(4, 0, 0), P(1, 0, 0), P(2, 0, 0), P(3, 0, 0) });

        var onMiddle = new CarrierNode(
            new CarrierId(5), CarrierKind.Segment, "tray", true, 10, 0.05,
            P(2, 0, 0), P(2, 10, 0));

        var many = NetworkBuilder.Build(2, new[] { manifold, onMiddle }, Options());

        Check("a fitting with five connectors joins on the middle one",
            many.Neighbours(new CarrierId(4)).Contains(new CarrierId(5)));

        Check("and a route can be found across it", many.Shape().Groups == 1);
    }

    /// <summary>
    /// A carrier whose type allows splicing takes the branch itself, and no box is recommended there.
    /// <code>
    ///   +====T0====+====== trunking, splices allowed ======+     trays at z = 0
    ///   P      S1                      S2                        all one foot below
    ///   0       5                      20                 30
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rule of 2026-09-20.</b> A cable is not spliced inside a pipe and an open splice
    /// is not made in a tray - hence a box at S1. A trunking with a removable cover is a place cable
    /// is spliced, so at S2 the cable graph simply branches in the carrier and there is nothing to
    /// recommend and nothing to place.
    /// </para>
    /// <para>
    /// The trunk arithmetic is the part worth guarding. The cable arrives at B1, leaves it for the
    /// splice, and stops there - so B1 takes three: in, out and one spur. The exit is counted when the
    /// splice is planned rather than when the next box arrives, because here there is no next box; a
    /// planner that waited would leave B1 with two, and a designer would pick a box one entry too
    /// small.
    /// </para>
    /// </remarks>
    private static void WhereTheCarrierAllowsASpliceNoBoxIsAskedFor()
    {
        Section("where the carrier allows a splice, no box is asked for");

        var plain = Tray(0, 0, 10);
        var trunking = new CarrierNode(
            new CarrierId(1), CarrierKind.Segment, "tray", true, 20, 0.05, P(10, 0, 0), P(30, 0, 0))
        {
            AllowsSplicing = true,
        };

        Check("a carrier says nothing about splicing unless it is told", !plain.AllowsSplicing);

        var network = NetworkBuilder.Build(1, new[] { plain, trunking }, Options());

        // Three devices, so that the cable is cut twice: once over the plain tray and once inside the
        // trunking. Two would not ask the question - the second device is the end of its branch and
        // the cable is not cut there at all, which is the whole of the tree's difference from a chain.
        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(3, 0, -1, "S1"), Terminal(20, 0, -1, "S2"), Terminal(27, 0, -1, "S3") })
        {
            Connection = CircuitConnection.AtJunctionBox,
        };

        var routed = Router.Route(network, circuit, Options());

        Check("the circuit routes", routed.Status == RouteStatus.Found);
        Check("the tap on the plain tray does not allow a splice", !routed.Taps[0].AllowsSplicing);
        Check("the tap on the trunking does", routed.Taps[1].AllowsSplicing);
        Check("the cable is cut twice - over the plain tray, and in the trunking", routed.Branches.Count == 2);

        var plan = BoxPlanner.Plan(new[] { routed }, Array.Empty<ExistingBox>(), radius: 1.5);

        Check("one box, where the plain tray is cut", plan.Boxes.Count == 1);
        Check("and one splice, where the trunking is", plan.Splices.Count == 1);
        Check("the splice names the circuit and the carrier it is made in",
            plan.Splices[0].Circuit == circuit.Id && plan.Splices[0].Carrier == trunking.Id);
        Check("it stands where the cable branches", Near(plan.Splices[0].At.X, 20));
        Check("the box holds the trunk in, the trunk on and the run that leaves - three",
            plan.Boxes[0].Entries == 3);

        // The same geometry with the permission withdrawn: the trunking is an ordinary tray again.
        var ordinary = new CarrierNode(
            new CarrierId(1), CarrierKind.Segment, "tray", true, 20, 0.05, P(10, 0, 0), P(30, 0, 0));

        var without = BoxPlanner.Plan(
            new[] { Router.Route(NetworkBuilder.Build(2, new[] { plain, ordinary }, Options()), circuit, Options()) },
            Array.Empty<ExistingBox>(),
            radius: 1.5);

        Check("without the permission the same circuit asks for a box at both cuts", without.Boxes.Count == 2);
        Check("and splices nowhere", without.Splices.Count == 0);

        // A box already in the model wins, however permissive the carrier - the owner's answer.
        var standing = new[] { new ExistingBox(new CarrierId(9), P(20, 0, 0)) };
        var withBox = BoxPlanner.Plan(new[] { routed }, standing, radius: 1.5);

        Check("an existing box within the radius is used instead of splicing in the carrier",
            withBox.Splices.Count == 0 && withBox.Boxes.Count(box => !box.IsRecommendation) == 1);
    }

    /// <summary>
    /// Slack is a fraction of what is measured plus a length at every place the cable is cut, and the
    /// places are the plan's to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's model of 2026-09-21.</b> It replaces a single fraction of the whole, which said
    /// that a circuit with one device and a circuit with nine need slack in proportion to how far they
    /// run. Most of it is spent where the cable is cut and dressed, and that happens a fixed number of
    /// times.
    /// </para>
    /// <para>
    /// <b>And that is why it is counted on the run and not in the router.</b> Two taps closer than the
    /// box radius share a box - one place, cut once - and the router cannot know that, because the
    /// radius is the planner's and the planner runs after every circuit has been routed. The second
    /// half of this section is exactly that case: the same circuit, the same route, two taps that merge
    /// and two that do not, and a slack that differs by one box.
    /// </para>
    /// </remarks>
    private static void SlackIsCountedWhereTheCableIsCut()
    {
        Section("slack is counted where the cable is cut");

        var rule = new SlackRule
        {
            Fraction = 0.1,
            AtPanel = 1.5,
            AtTerminal = 0.25,
            AtBox = 0.75,
            AtSplice = 0.4,
        };

        Check("a rule that adds nothing says so", SlackRule.None.IsNothing && !rule.IsNothing);
        Check("and one that adds nothing adds nothing", Near(SlackRule.None.For(100, 9, 9, 9), 0));

        // Written out rather than called on the rule: 10 + 1.5 + 0.5 + 1.5 + 0.4.
        Check("every term is counted, each by its own count",
            Near(rule.For(100, terminals: 2, boxes: 2, splices: 1), 13.9));
        Check("the cable's factor multiplies all of it, the fraction included",
            Near(rule.For(100, 2, 2, 1, cableFactor: 2), 27.8));
        Check("and a factor of one changes nothing", Near(rule.For(100, 2, 2, 1, 1), 13.9));

        // A tray with three sockets under it, cut in boxes. The first two are a foot apart and merge
        // into one box; the third is far away and opens its own.
        var network = NetworkBuilder.Build(1, new[] { Tray(0, 0, 40) }, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(10, 0, -1, "S1"), Terminal(11, 0, -1, "S2"), Terminal(30, 0, -1, "S3") })
        {
            Connection = CircuitConnection.AtJunctionBox,
        };

        var routed = Router.Route(network, circuit, Options());

        Check("the circuit routes to three devices", routed.Status == RouteStatus.Found && routed.Taps.Count == 3);

        var merged = BoxPlanner.Plan(new[] { routed }, Array.Empty<ExistingBox>(), radius: 1.5);
        var apart = BoxPlanner.Plan(new[] { routed }, Array.Empty<ExistingBox>(), radius: 0.5);

        // The cable is cut twice, not three times: the last device is the end of its branch and the
        // cable simply ends in it. A radius that reaches merges the two cuts into one box.
        Check("a radius that reaches merges the two cuts into one box", merged.Boxes.Count == 1);
        Check("and one that does not leaves two", apart.Boxes.Count == 2);

        var withMerge = new RouteRun(new[] { routed }, 1, TimeSpan.Zero, merged, rule);
        var without = new RouteRun(new[] { routed }, 1, TimeSpan.Zero, apart, rule);

        var measured = routed.Measured;

        Check("slack is the fraction, the panel, a terminal each and a box per place",
            Near(withMerge.SlackOf(circuit.Id), (measured * 0.1) + 1.5 + (0.25 * 3) + (0.75 * 1)));
        Check("the same route with nothing merged pays for one box more",
            Near(without.SlackOf(circuit.Id) - withMerge.SlackOf(circuit.Id), 0.75));
        Check("the total is what the route measured plus its slack",
            Near(withMerge.TotalLengthOf(circuit.Id), measured + withMerge.SlackOf(circuit.Id)));
        Check("and the run sums that total, not the bare measurement",
            Near(withMerge.TotalLength, withMerge.TotalLengthOf(circuit.Id)));

        // A circuit that did not route has no length and no slack: there is nothing to add it to.
        var nowhere = Router.Route(NetworkBuilder.Build(2, Array.Empty<CarrierNode>(), Options()), circuit, Options());
        var barren = new RouteRun(new[] { nowhere }, 2, TimeSpan.Zero, BoxPlan.Empty, rule);

        Check("a circuit that did not route is given no slack",
            nowhere.Status != RouteStatus.Found
            && Near(barren.SlackOf(circuit.Id), 0) && Near(barren.TotalLengthOf(circuit.Id), 0));
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

    /// <summary>
    /// What a splice costs is what decides where a tree branches, and both answers are right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three devices strung under one tray, which is the plainest shape that offers a choice.</b>
    /// Cutting the cable under each device costs nothing in cable - the trunk was going past anyway -
    /// and costs two cuts. Cutting it once and running three separate lines from that one place costs
    /// ten feet of cable and one cut. Neither is wrong; which is cheaper is what the project states by
    /// saying what a splice costs, and a search that ignored the number would always give the first.
    /// </para>
    /// <para>
    /// <b>A place already cut takes another run for nothing</b> - that is what makes the second plan
    /// possible at all, and it is the part of the price easiest to write wrong: charged per leaving
    /// run instead of per place cut, the two plans would cost the same in cuts and the number would
    /// decide nothing.
    /// </para>
    /// </remarks>
    private static void WhatASpliceCostsDecidesWhereTheCableIsCut()
    {
        Section("what a splice costs decides where the cable is cut");

        var network = NetworkBuilder.Build(1, new[] { Tray(1, 0, 30) }, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(10, 0, -1, "S1"), Terminal(20, 0, -1, "S2"), Terminal(30, 0, -1, "S3") })
        {
            Connection = CircuitConnection.AtJunctionBox,
        };

        var cheap = Router.Route(network, circuit, Tree(0));

        Check("with splices free the cable runs the tray once and is cut under two of the devices",
            cheap.Status == RouteStatus.Found && cheap.Branches.Count == 2 && Near(cheap.AlongCarriers, 30));

        var dear = Router.Route(network, circuit, Tree(20));

        Check("with a splice worth twenty feet of cable it is cut once and three lines leave that place",
            dear.Status == RouteStatus.Found && dear.Branches.Count == 1 && Near(dear.AlongCarriers, 40));
        Check("and the one place it is cut holds four cable ends - the trunk in, and three lines out",
            dear.Branches[0].Ways == 3);

        // The drops are untouched by either answer: every device is still reached from the tray above
        // it, and the panel still leaves once. A plan that moved a drop would be measuring something
        // else, and the lengths above would still look plausible.
        Check("either way the panel and the three devices are approached once each",
            Near(cheap.Approaches, 4) && Near(dear.Approaches, 4));
    }

    /// <summary>
    /// A device may be branched at only while its terminal block has room, and the room is the type's
    /// answer where it gives one and the project's where it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A tee with a device at the junction and one at the end of each arm, cut at terminals.</b>
    /// Cutting in the tray is not allowed in this connection, so the only places the cable may be cut
    /// are the devices themselves - which is exactly where the capacity governs. With the ordinary
    /// terminal the cable arrives and goes on, no more: the far device is reached the long way round,
    /// back through the other arm. A block that holds three lets the second arm leave the junction
    /// device directly, and ten feet of cable are not laid.
    /// </para>
    /// <para>
    /// <b>The project's default and the type's answer are asked the same question twice</b>, because
    /// they are two ways to one number and only one of them is exercised by a model whose types are
    /// all blank - which is every model, until somebody fills one in.
    /// </para>
    /// </remarks>
    private static void WhatATerminalHoldsDecidesWhetherItMayBranch()
    {
        Section("what a terminal holds decides whether it may branch");

        var trunk = Tray(1, 0, 10);
        var north = new CarrierNode(
            new CarrierId(2), CarrierKind.Segment, "tray", true, 10, 0.05, P(10, 0, 0), P(10, 10, 0));
        var south = new CarrierNode(
            new CarrierId(3), CarrierKind.Segment, "tray", true, 10, 0.05, P(10, 0, 0), P(10, -10, 0));

        var network = NetworkBuilder.Build(1, new[] { trunk, north, south }, Options());

        Check("the tee is one piece", network.Shape().Groups == 1);

        // Spelled out rather than taken from the helper: it names a terminal after where it stands
        // along X, and all three of these stand at the same X.
        var junction = new Terminal(new CarrierId(801), P(10, 0, -1), "S1");
        var far = new Terminal(new CarrierId(802), P(10, 10, -1), "S2");
        var other = new Terminal(new CarrierId(803), P(10, -10, -1), "S3");

        var ordinary = new CircuitSnapshot(
            new CarrierId(1), "P-1", Terminal(0, 0, -1, "panel"), new[] { junction, far, other })
        {
            Connection = CircuitConnection.AtTerminal,
        };

        var chained = Router.Route(network, ordinary, Tree(0));

        Check("an ordinary terminal takes the cable in and out and no more, so the third device is reached the long way",
            chained.Status == RouteStatus.Found && chained.Branches.Count == 0 && Near(chained.AlongCarriers, 40));

        var roomy = new CircuitSnapshot(
            new CarrierId(1), "P-1", Terminal(0, 0, -1, "panel"),
            new[] { new Terminal(junction.Owner, junction.At, junction.Label) { Capacity = 3 }, far, other })
        {
            Connection = CircuitConnection.AtTerminal,
        };

        var branched = Router.Route(network, roomy, Tree(0));

        Check("a block that holds three lets the second arm leave the device itself, ten feet shorter",
            branched.Status == RouteStatus.Found && branched.Branches.Count == 1 && Near(branched.AlongCarriers, 30));
        Check("and the branch names the device it was made at",
            branched.Branches[0].Device is not null && branched.Branches[0].Device!.Owner == junction.Owner);

        // The same answer reached the other way: the type says nothing and the project says three.
        var byProject = Router.Route(network, ordinary, Tree(0, capacity: 3));

        Check("a type that says nothing gets the project's answer, which reaches the same tree",
            byProject.Status == RouteStatus.Found && byProject.Branches.Count == 1
            && Near(byProject.AlongCarriers, 30));

        // And the type overrules the project rather than the other way about: a project that says one
        // cannot make a block that holds three hold one.
        var byType = Router.Route(network, roomy, Tree(0, capacity: 1));

        Check("and a type that does say overrules the project",
            byType.Status == RouteStatus.Found && byType.Branches.Count == 1 && Near(byType.AlongCarriers, 30));
    }

    /// <summary>
    /// A box the cable goes through without being cut feeds nothing, and no device hangs off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One device, one box, and the shortest network that can tell the two rules apart.</b> The
    /// line out of the panel reaches the device without being cut anywhere, and it passes straight
    /// through a box on the way. The piece of cable feeding the device runs from the panel, not from
    /// that box, so the device hangs off nothing and the box serves nobody.
    /// </para>
    /// <para>
    /// <b>Written after the canonical sweep found it, because nothing on paper had asked.</b> The
    /// search named the first box standing anywhere along the run, cut or not - and on every paper
    /// network that box also happened to be one another run began at, so every check agreed. On the
    /// owner's model it named a box the apply would then have written this circuit onto, and the
    /// screen would have reported a device served from a place nothing is joined at.
    /// </para>
    /// </remarks>
    private static void ABoxTheCableOnlyPassesThroughFeedsNothing()
    {
        Section("a box the cable only passes through feeds nothing");

        var standing = new[] { new ExistingBox(new CarrierId(60), P(10, 0, 0)) };

        var network = NetworkBuilder.Build(
            1, new[] { Tray(0, 0, 10), Box(60, 10), Tray(1, 10, 20) }, Options());

        var circuit = new CircuitSnapshot(
            new CarrierId(1), "P-1", Terminal(0, 0, -1, "panel"), new[] { Terminal(18, 0, -1, "S1") })
        {
            Connection = CircuitConnection.AtJunctionBox,
        };

        var routed = Router.Route(network, circuit, Options(), standing);

        Check("a circuit of one device needs no box and is routed without one",
            routed.Status == RouteStatus.Found && routed.Branches.Count == 0);
        // The box stands where the two trays meet, and the cable walks both - so it goes past the box
        // whether or not the path names the box element itself. It usually does not: three carriers
        // meeting at one point touch each other, and the cheapest way across is tray to tray. That is
        // why a box is matched by where it stands rather than by which element a place belongs to.
        Check("the cable walks both trays, so it goes past the point the box stands at",
            routed.Path.Contains(new CarrierId(0)) && routed.Path.Contains(new CarrierId(1)));
        Check("and the device hangs off nothing, because nothing was cut",
            routed.Taps.Count == 1 && routed.Taps[0].Box is null && Near(routed.Taps[0].SpurAlongCarriers, 0));

        var plan = BoxPlanner.Plan(new[] { routed }, standing, radius: 1.5);

        Check("so the plan holds no box at all - the one that stands was only offered",
            plan.Boxes.Count == 0 && plan.Splices.Count == 0);
    }


    /// <summary>
    /// A circuit lies only in carriers that admit its cable group, and a box is a carrier too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rule of 2026-09-22, and the first one in this feature that forbids.</b> Fire
    /// alarm goes on its own, structured cabling shares a tray with power only behind a divider - so
    /// a shorter route through the wrong tray is not a better answer, it is an illegal one.
    /// </para>
    /// <para>
    /// <b>The strict reading, which was the owner's choice of two:</b> a carrier nobody marked admits
    /// only circuits nobody marked. So a model where nothing is filled in behaves exactly as it did
    /// before this existed - checked here - and a fire circuit cannot slip into a tray somebody
    /// forgot to mark.
    /// </para>
    /// <para>
    /// The last four are the ones worth having: a box is a carrier, so a tap must not merge into one
    /// that does not take it, and a box the calculation recommends belongs to the group that asked
    /// for it - otherwise the tool's own advice would be the one place two groups met.
    /// </para>
    /// </remarks>
    private static void ACircuitLiesOnlyInCarriersThatAdmitIt()
    {
        Section("a circuit lies only in carriers that admit its cable group");

        CarrierNode Run(long id, double y, string groups) =>
            new(new CarrierId(id), CarrierKind.Segment, "tray", true, 40, 0.05, P(0, y, 0), P(40, y, 0))
            {
                Groups = CableGroups.Parse(groups),
            };

        CircuitSnapshot Circuit(long id, string group, double deviceX, CircuitConnection how = CircuitConnection.AtTerminal) =>
            new(new CarrierId(id), "P-" + id, Terminal(0, 0, -1, "panel"),
                new[] { Terminal(deviceX, 0, -1, "S" + id) })
            {
                Connection = how,
                Conductors = 3,
                CableGroup = group,
            };

        // The rule itself, before anything is routed through it.
        Check("a carrier nobody marked admits a circuit nobody marked",
            CableGroups.Unmarked.Admits(string.Empty));
        Check("and turns away one that names a group - the strict reading",
            !CableGroups.Unmarked.Admits("fire"));
        Check("a carrier that names a group admits that group and nothing else",
            CableGroups.Parse("fire").Admits("fire")
            && !CableGroups.Parse("fire").Admits("power")
            && !CableGroups.Parse("fire").Admits(string.Empty));
        Check("case and surrounding space are not part of the answer",
            CableGroups.Parse(" Fire ").Admits("fire") && CableGroups.Parse("fire").Admits("FIRE"));
        Check("a carrier may name several, and admits each of them",
            CableGroups.Parse("power; data").Admits("power")
            && CableGroups.Parse("power; data").Admits("data")
            && !CableGroups.Parse("power; data").Admits("fire"));

        // A model where nobody marked anything is handed back unchanged - the promise that this
        // costs nothing to a project that ignores it, kept by construction rather than measured.
        var plain = NetworkBuilder.Build(1, new[] { Run(10, 0, string.Empty) }, Options());

        Check("a structure nobody marked is not filtered at all, it is the same network",
            ReferenceEquals(plain.Admitting(string.Empty), plain));

        var ungrouped = Router.Route(plain, Circuit(1, string.Empty, 20), Options());

        Check("and an ungrouped circuit routes there as it always did", ungrouped.Status == RouteStatus.Found);

        // The same tray, the same circuit, now in a group nobody gave that tray.
        var refused = Router.Route(plain, Circuit(2, "fire", 20), Options());

        Check("a circuit in a group the only tray does not admit is not routed",
            refused.Status != RouteStatus.Found);
        Check("and it is told apart from having nothing within reach at all",
            refused.Status == RouteStatus.NoCarrierAllowed);

        var nowhere = Router.Route(plain, Circuit(3, string.Empty, 20), Options(reach: 0.5));

        Check("while a circuit that really reaches nothing still says so",
            nowhere.Status == RouteStatus.NoCarrierNear);

        // Two trays in reach of both ends: the near one is not marked for fire, the far one is.
        var both = NetworkBuilder.Build(2, new[] { Run(10, 0, string.Empty), Run(11, 2, "fire") }, Options());

        var onFire = Router.Route(both, Circuit(4, "fire", 20), Options());
        var onPlain = Router.Route(both, Circuit(5, string.Empty, 20), Options());

        Check("a circuit takes the tray that admits it even though a nearer one is unmarked",
            onFire.Status == RouteStatus.Found && onFire.Path.Contains(new CarrierId(11)));
        Check("and the unmarked circuit takes the unmarked tray, which is nearer",
            onPlain.Status == RouteStatus.Found && onPlain.Path.Contains(new CarrierId(10)));
        Check("so the permitted route is the longer one, and that is the point of the rule",
            onFire.Measured > onPlain.Measured);

        // A box is a carrier. One tray both groups may use, and two circuits whose cable is cut in
        // boxes at very nearly the same places - so the radius would merge them, if it were allowed.
        var shared = NetworkBuilder.Build(3, new[] { Run(10, 0, "power; fire") }, Options());

        CircuitSnapshot Cut(long id, string group, double shift) =>
            new(new CarrierId(id), "P-" + id, Terminal(0, 0, -1, "panel"),
                new[]
                {
                    Terminal(10 + shift, 0, -1, "A" + id),
                    Terminal(20 + shift, 0, -1, "B" + id),
                    Terminal(30 + shift, 0, -1, "C" + id),
                })
            {
                Connection = CircuitConnection.AtJunctionBox,
                Conductors = 3,
                CableGroup = group,
            };

        var power = Router.Route(shared, Cut(6, "power", 0), Options());
        var fire = Router.Route(shared, Cut(7, "fire", 0.5), Options());
        var alsoPower = Router.Route(shared, Cut(8, "power", 0.5), Options());

        var same = BoxPlanner.Plan(new[] { power, alsoPower }, Array.Empty<ExistingBox>(), radius: 4);
        var mixed = BoxPlanner.Plan(new[] { power, fire }, Array.Empty<ExistingBox>(), radius: 4);

        Check("two circuits of one group, cut at the same places, share the boxes recommended there",
            power.Status == RouteStatus.Found
            && fire.Status == RouteStatus.Found
            && same.Boxes.Any(box => box.Circuits.Count == 2));
        Check("two circuits of different groups never share one, however near they are cut",
            mixed.Boxes.All(box => box.Circuits.Count == 1));
        Check("so the forbidden pair costs boxes rather than being quietly put in one",
            mixed.Boxes.Count > same.Boxes.Count);

        // And a box already standing takes only what it says it takes. Put where the fire circuit is
        // cut, so nearness is never the reason it is passed over.
        var where = fire.Branches[0].At;

        ExistingBox[] Standing(string groups) => new[]
        {
            new ExistingBox(new CarrierId(50), where) { Groups = CableGroups.Parse(groups) },
        };

        var intoIt = BoxPlanner.Plan(new[] { fire }, Standing("fire"), radius: 4);
        var pastIt = BoxPlanner.Plan(new[] { fire }, Standing("power"), radius: 4);

        Check("a box that admits the group is used where the cable is cut",
            intoIt.Boxes.Any(box => box.Existing?.Id == new CarrierId(50)));
        Check("a box that does not admit it is not used, however near it stands",
            pastIt.Boxes.All(box => box.Existing is null));

        // Without additional boxes there is nothing to fall back on, so the same rule has to show up
        // as a refusal rather than as a recommendation. Written because the path that filters the
        // boxes handed to the search is its own line of code: without a case that walks it, breaking
        // it would cost nothing here and a fire circuit would be served out of a power box.
        var standing = NetworkBuilder.Build(
            4,
            new[] { Run(10, 0, "power; fire"), Box(50, 10), Box(51, 20), Box(52, 30) },
            Options());

        ExistingBox[] Boxes(string groups) => new[]
        {
            new ExistingBox(new CarrierId(50), P(10, 0, 0)) { Groups = CableGroups.Parse(groups) },
            new ExistingBox(new CarrierId(51), P(20, 0, 0)) { Groups = CableGroups.Parse(groups) },
            new ExistingBox(new CarrierId(52), P(30, 0, 0)) { Groups = CableGroups.Parse(groups) },
        };

        var served = Router.Route(standing, Cut(9, "fire", 0), Options(), Boxes("fire"));
        var unserved = Router.Route(standing, Cut(10, "fire", 0), Options(), Boxes("power"));

        Check("without additional boxes, a circuit is served from boxes that admit it",
            served.Status == RouteStatus.Found);
        Check("and is not served from boxes that do not, even when they are the only ones there",
            unserved.Status != RouteStatus.Found);
    }
    /// <summary>
    /// What a box holds is measured in conductors, reported when exceeded, and changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's answers of 2026-09-21, as arithmetic.</b> A capacity is in conductors, not in
    /// cable entries, and counts only the cables actually spliced in the box. Exceeding it warns and
    /// moves nothing: no tap changes box, no box is split, no route is diverted. The type answers
    /// first, the project when the type is silent, and with neither there is no limit at all. A
    /// recommended box has no capacity whatever the project said - the indicator states the need and
    /// the designer picks a box that takes it.
    /// </para>
    /// <para>
    /// The network is the one the existing-box section uses: X at ten and Y at thirty, three devices,
    /// three ends at each box. With a three-conductor cable that is nine conductors apiece.
    /// </para>
    /// <para>
    /// <b>The check that the plan is unchanged is the one that carries the owner's answer</b>, and it
    /// is written as a comparison of two plans over the same routes rather than as a list of expected
    /// numbers. A list would be a second statement of what the planner does, and it would agree with
    /// a planner that had started diverting taps as long as somebody updated the list - which is the
    /// failure this file has already paid for once.
    /// </para>
    /// </remarks>
    private static void WhatABoxHoldsIsReportedAndChangesNothing()
    {
        Section("what a box holds is reported, and changes nothing");

        var x = Box(50, 10);
        var y = Box(51, 30);
        var network = NetworkBuilder.Build(1, new[] { Tray(0, 0, 10), x, Tray(1, 10, 30), y, Tray(2, 30, 40) }, Options());

        var devices = new[] { Terminal(18, 0, -1, "S1"), Terminal(38, 0, -1, "S2"), Terminal(24, 0, -1, "S3") };

        CircuitSnapshot Circuit(long id, int conductors) =>
            new(new CarrierId(id), "P-" + id, Terminal(0, 0, -1, "panel"), devices)
            {
                Connection = CircuitConnection.AtJunctionBox,
                Conductors = conductors,
            };

        ExistingBox[] Standing(int atX, int atY) => new[]
        {
            new ExistingBox(new CarrierId(50), P(10, 0, 0)) { Capacity = atX },
            new ExistingBox(new CarrierId(51), P(30, 0, 0)) { Capacity = atY },
        };

        // Three conductors a cable, three ends at each box: nine apiece. X takes twelve and fits;
        // Y takes six and does not.
        var routed = Router.Route(network, Circuit(1, 3), Options(), Standing(12, 6));
        var plan = BoxPlanner.Plan(new[] { routed }, Standing(12, 6), radius: 1.5);

        var atX = plan.Boxes.Single(box => box.Existing?.Id == new CarrierId(50));
        var atY = plan.Boxes.Single(box => box.Existing?.Id == new CarrierId(51));

        Check("a box counts the conductors of every cable spliced in it: three ends of a three-wire cable",
            atX.Conductors == 9 && atY.Conductors == 9);
        Check("the one that holds twelve is within its capacity", !atX.IsOverfull);
        Check("the one that holds six is over it", atY.IsOverfull);

        // Same routes, no capacity anywhere. If a capacity moved anything, these two would differ.
        var unbounded = BoxPlanner.Plan(new[] { routed }, Standing(0, 0), radius: 1.5);

        Check("stating a capacity changes nothing about the plan - same boxes, same entries, same spurs",
            unbounded.Boxes.Count == plan.Boxes.Count
            && unbounded.Boxes.Zip(plan.Boxes, (a, b) =>
                a.Existing?.Id == b.Existing?.Id && a.Entries == b.Entries && a.Spurs == b.Spurs).All(same => same)
            && unbounded.Splices.Count == plan.Splices.Count);
        Check("and with no capacity stated anywhere, no box is over one",
            unbounded.Boxes.All(box => !box.IsOverfull));

        // The type answers first: X states twelve and keeps it, Y states nothing and takes the six
        // the project names.
        var mixed = BoxPlanner.Plan(new[] { routed }, Standing(12, 0), radius: 1.5, defaultCapacity: 6);

        Check("a box whose type states a capacity keeps it, and one that does not takes the project's",
            mixed.Boxes.Single(box => box.Existing?.Id == new CarrierId(50)) is { Capacity: 12, IsOverfull: false }
            && mixed.Boxes.Single(box => box.Existing?.Id == new CarrierId(51)) is { Capacity: 6, IsOverfull: true });

        // A circuit Revit does not size fills nothing, however small the capacity.
        var unsized = Router.Route(network, Circuit(2, 0), Options(), Standing(1, 1));
        var barren = BoxPlanner.Plan(new[] { unsized }, Standing(1, 1), radius: 1.5);

        Check("a circuit that reports no conductors fills no box, whatever the capacity",
            barren.Boxes.All(box => box.Conductors == 0 && !box.IsOverfull));
        Check("and the run says how many circuits that was, so the silence is not read as a pass",
            new RouteRun(new[] { unsized }, 1, TimeSpan.Zero, barren).WithoutConductors == 1
            && new RouteRun(new[] { routed }, 1, TimeSpan.Zero, plan).WithoutConductors == 0);
        Check("the run names every box over its capacity, and only those",
            new RouteRun(new[] { routed }, 1, TimeSpan.Zero, plan).Overfull
                .Select(box => box.Existing?.Id).SequenceEqual(new CarrierId?[] { new CarrierId(51) }));

        // Two circuits spliced in one box add: the second one's conductors are on the same block.
        var second = Router.Route(network, Circuit(2, 3), Options(), Standing(12, 6));
        var shared = BoxPlanner.Plan(new[] { routed, second }, Standing(12, 6), radius: 1.5);

        Check("two circuits spliced in one box add their conductors, and share no capacity relief",
            shared.Boxes.Single(box => box.Existing?.Id == new CarrierId(50)) is { Conductors: 18, IsOverfull: true });

        // A recommended box, with a project capacity stated that it would be far over. Nothing in the
        // model stands near the taps here, so the ordinary mode recommends its own boxes.
        var recommended = BoxPlanner.Plan(
            new[] { Router.Route(network, Circuit(3, 3), Options(), null) },
            Array.Empty<ExistingBox>(),
            radius: 1.5,
            defaultCapacity: 1);

        Check("a recommended box has no capacity, so the project's value never makes one overfull",
            recommended.Boxes.Count > 0
            && recommended.Boxes.All(box => box.IsRecommendation && box.Capacity == 0 && !box.IsOverfull));

        // Two branches of one circuit merged into one box: a run that used to leave one and arrive at
        // the other is now inside the box and is no cable at all, so the second branch adds one entry
        // rather than three. The conductors have to follow that, and nothing above asks - the first
        // red run of this section found the arithmetic untested, because a circuit branching twice in
        // one box is a shape none of the networks above has.
        var oneTray = NetworkBuilder.Build(1, new[] { Tray(1, 0, 30) }, Options());
        var along = new CircuitSnapshot(
            new CarrierId(4), "P-4",
            Terminal(0, 0, -1, "panel"),
            new[] { Terminal(10, 0, -1, "S1"), Terminal(20, 0, -1, "S2"), Terminal(30, 0, -1, "S3") })
        {
            Connection = CircuitConnection.AtJunctionBox,
            Conductors = 3,
        };

        var twice = Router.Route(oneTray, along, Tree(0));

        Check("with splices free the cable is cut under two of the three devices",
            twice.Status == RouteStatus.Found && twice.Branches.Count == 2);

        var merged = BoxPlanner.Plan(new[] { twice }, Array.Empty<ExistingBox>(), radius: 12).Boxes.Single();
        var apart = BoxPlanner.Plan(new[] { twice }, Array.Empty<ExistingBox>(), radius: 1.5).Boxes;

        Check("merged into one box the second branch adds one entry, and its conductors with it",
            merged.Entries == 4 && merged.Conductors == 12);
        Check("kept apart they are two boxes of three, and the conductors are three apiece",
            apart.Count == 2 && apart.All(box => box is { Entries: 3, Conductors: 9 }));
    }

    private static CarrierNode Tray(long id, double from, double to) =>
        new(new CarrierId(id), CarrierKind.Segment, "tray", true, to - from, 0.05, P(from, 0, 0), P(to, 0, 0));

    /// <summary>The ordinary options with the two numbers that shape a tree stated.</summary>
    /// <remarks>
    /// A helper rather than a default on <c>Options</c>: every other section is about something else
    /// and would then be quietly routed with a price on splices it never asked for.
    /// </remarks>
    private static RoutingOptions Tree(double splice, int capacity = 2) =>
        new()
        {
            JoinTolerance = Tolerance,
            MaxApproach = 6,
            AxisAlignedApproach = true,
            SpliceCost = splice,
            TerminalCapacity = capacity,
        };

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
