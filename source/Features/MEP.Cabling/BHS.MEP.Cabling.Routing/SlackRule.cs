namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// How much cable is added beyond what the route measures: a fraction of it, and a length at every
/// place the cable is cut.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's model of 2026-09-21, and it replaces a fraction of the whole.</b> Until then slack
/// was one number, <c>LengthExtend</c>, multiplied into the measured length - which says that a
/// circuit with one device and a circuit with nine need slack in proportion to how far they run.
/// They do not: most of it is spent where the cable is cut and dressed, and that happens a fixed
/// number of times, at the panel, at each device and at each junction box.
/// </para>
/// <para>
/// So there are five numbers. The fraction stays, because sag and detours really are proportional to
/// the run. The other four are lengths, counted per <i>place</i> rather than per cable entry - the
/// owner's answer, asked as a question because an intermediate box takes three entries and the last
/// one takes two, and the two readings differ by half on every intermediate box.
/// </para>
/// <para>
/// <b>A splice made in the carrier has its own number, apart from a box.</b> The owner's decision:
/// the cable is cut there just the same, but there is nothing to coil it in, so the length is not
/// the same length.
/// </para>
/// <para>
/// <b>And all five are then scaled by one factor that belongs to the cable</b>, passed to
/// <see cref="For"/>. That is what keeps the norm and the cable apart: the numbers here are what a
/// project decided, and how much of it a particular cable needs is the cable's business. The factor
/// itself arrives with the cable, which is its own piece of work; until then it is one.
/// </para>
/// </remarks>
public sealed class SlackRule
{
    /// <summary>Nothing added anywhere - what a project that has said nothing gets.</summary>
    public static SlackRule None { get; } = new();

    /// <summary>A fraction of the measured length, drops included.</summary>
    /// <remarks>
    /// Of everything, the drops included - the owner's answer. It is a ratio between two lengths and
    /// is never converted from millimetres, unlike the four below; a unit error here would produce a
    /// plausible number nobody could trace.
    /// </remarks>
    public double Fraction { get; init; }

    /// <summary>Added once, where the cable enters the panel, in internal feet.</summary>
    public double AtPanel { get; init; }

    /// <summary>Added at each device, in internal feet.</summary>
    /// <remarks>
    /// At every device of the circuit, whichever way it is connected. The owner's rule from the
    /// junction-box discussion: a panel and a terminal always have at least one splice, so a device
    /// is a place the cable is cut even when the trunk runs past it in a box.
    /// </remarks>
    public double AtTerminal { get; init; }

    /// <summary>Added at each junction box the circuit's cable is cut in, in internal feet.</summary>
    public double AtBox { get; init; }

    /// <summary>Added at each splice made in the carrier itself, where no box stands, in internal feet.</summary>
    public double AtSplice { get; init; }

    /// <summary>Whether this rule adds anything at all.</summary>
    public bool IsNothing =>
        Fraction == 0 && AtPanel == 0 && AtTerminal == 0 && AtBox == 0 && AtSplice == 0;

    /// <summary>
    /// The slack for one circuit, in internal feet.
    /// </summary>
    /// <param name="measured">What the route measured: along the carriers and down to the ends.</param>
    /// <param name="terminals">How many devices the circuit has.</param>
    /// <param name="boxes">How many junction boxes its cable is cut in.</param>
    /// <param name="splices">How many splices are made in the carrier itself.</param>
    /// <param name="cableFactor">
    /// What the circuit's cable makes of the project's numbers; one when nothing is known about it.
    /// </param>
    /// <remarks>
    /// <b>The factor multiplies everything, the fraction included</b> - the owner's answer of
    /// 2026-09-21, asked because the fraction is about sag rather than about dressing and could have
    /// been left alone. It was not: a heavier cable sags more and turns wider, so the whole of the
    /// slack scales with it.
    /// </remarks>
    public double For(double measured, int terminals, int boxes, int splices, double cableFactor = 1)
    {
        var slack = (measured * Fraction)
            + AtPanel
            + (AtTerminal * terminals)
            + (AtBox * boxes)
            + (AtSplice * splices);

        return slack * cableFactor;
    }
}
