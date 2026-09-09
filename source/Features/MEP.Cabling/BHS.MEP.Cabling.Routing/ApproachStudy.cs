namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// How far a device is from the structure, measured the way it is today and the way it should be.
/// </summary>
/// <remarks>
/// <para>
/// <b>A measurement, not a feature, and it exists to decide one question before that question is
/// answered by writing code.</b> The router measures a device's approach to a carrier's
/// <i>terminals</i> - the points where carriers join each other. That is the wrong question: a cable
/// leaves a tray wherever it likes along its length, so a socket under the middle of a twenty-metre
/// run is measured to an end ten metres away, and at a three-metre reach that run does not exist for
/// it at all.
/// </para>
/// <para>
/// Fixing it properly means a route can enter a carrier part-way, which means Dijkstra is seeded
/// from a point rather than from a node, which rewrites the stretch the probe already guards. That
/// is real work. This says what the work would buy, on the model in front of us, before it is spent.
/// </para>
/// <para>
/// <b>It scans every carrier for every terminal, deliberately.</b> The spatial index files a carrier
/// under its terminals only, so a long tray whose middle is over the device is filed at cells far
/// away and would never be offered - which means using the index here would measure the index rather
/// than the distance. That is itself part of the answer: the real fix needs carriers filed along
/// their length, not only at their ends. Brute force is honest for a few hundred carriers and is not
/// the shape of the fix.
/// </para>
/// </remarks>
public static class ApproachStudy
{
    /// <summary>Measures both ways over every terminal of every circuit.</summary>
    public static ApproachComparison Compare(
        RouteNetwork network,
        IReadOnlyList<CircuitSnapshot> circuits,
        RoutingOptions options)
    {
        var terminals = 0;
        var byTerminals = 0.0;
        var byNearest = 0.0;
        var reachedByTerminals = 0;
        var reachedByNearest = 0;
        var worst = 0.0;

        foreach (var circuit in circuits)
        {
            foreach (var terminal in Ends(circuit))
            {
                terminals++;

                var toTerminals = double.MaxValue;
                var toNearest = double.MaxValue;

                foreach (var node in network.Nodes)
                {
                    foreach (var at in node.Terminals)
                        toTerminals = Math.Min(toTerminals, Approaches.Measure(terminal.At, at, options));

                    toNearest = Math.Min(
                        toNearest,
                        Approaches.Measure(terminal.At, node.NearestPointTo(terminal.At), options));
                }

                if (toTerminals <= options.MaxApproach)
                {
                    reachedByTerminals++;
                    byTerminals += toTerminals;
                }

                if (toNearest <= options.MaxApproach)
                {
                    reachedByNearest++;
                    byNearest += toNearest;
                }

                // Only over the ones both measures can see. A terminal reachable by one and not the
                // other has no difference to report - it has a different answer entirely, and that
                // is what the counts say.
                if (toTerminals <= options.MaxApproach && toNearest <= options.MaxApproach)
                    worst = Math.Max(worst, toTerminals - toNearest);
            }
        }

        return new ApproachComparison(terminals, reachedByTerminals, reachedByNearest, byTerminals, byNearest, worst);
    }

    private static IEnumerable<Terminal> Ends(CircuitSnapshot circuit)
    {
        yield return circuit.Source;

        foreach (var device in circuit.Devices)
            yield return device;
    }
}

/// <summary>What the two ways of measuring a drop came to.</summary>
public sealed class ApproachComparison
{
    public ApproachComparison(
        int terminals,
        int reachedByTerminals,
        int reachedByNearest,
        double byTerminals,
        double byNearest,
        double worst)
    {
        Terminals = terminals;
        ReachedByTerminals = reachedByTerminals;
        ReachedByNearest = reachedByNearest;
        ByTerminals = byTerminals;
        ByNearest = byNearest;
        Worst = worst;
    }

    /// <summary>Every panel and device end that was looked at.</summary>
    public int Terminals { get; }

    /// <summary>How many of them find a carrier when measured to its terminals - what happens today.</summary>
    public int ReachedByTerminals { get; }

    /// <summary>How many would, measured to the nearest point along a carrier.</summary>
    public int ReachedByNearest { get; }

    /// <summary>Summed drop, in internal feet, over what today's measure can reach.</summary>
    public double ByTerminals { get; }

    /// <summary>Summed drop over what the other measure can reach.</summary>
    public double ByNearest { get; }

    /// <summary>The largest single drop the change would shorten.</summary>
    public double Worst { get; }

    /// <summary>Terminals that have no carrier today and would have one.</summary>
    public int Gained => ReachedByNearest - ReachedByTerminals;

    /// <summary>True when the two measures agree closely enough that the work would buy nothing.</summary>
    /// <remarks>
    /// A tenth of a foot over a whole model, and no terminal changing from unreachable to reachable,
    /// is agreement: the model's segments are short and their ends are already where the cable
    /// leaves. Anything more is the case for doing the work.
    /// </remarks>
    public bool Agree => Gained == 0 && Math.Abs(ByTerminals - ByNearest) < 0.1;
}

/// <summary>How far a cable actually travels between a device and the structure.</summary>
/// <remarks>
/// One definition, used by the search and by the study that questions it. Two copies would let the
/// study measure something the router does not do, which is the one way a measurement can be worse
/// than none.
/// </remarks>
internal static class Approaches
{
    public static double Measure(Point3 from, Point3 to, RoutingOptions options) =>
        options.AxisAlignedApproach
            ? Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) + Math.Abs(from.Z - to.Z)
            : from.DistanceTo(to);
}
