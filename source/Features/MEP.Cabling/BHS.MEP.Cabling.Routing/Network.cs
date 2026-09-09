namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// An element of the model, named in a way this assembly can hold.
/// </summary>
/// <remarks>
/// <para>
/// Over <see cref="long"/>, and that is correctness rather than caution. Measured across all four
/// supported releases: Revit 2024 and 2025 carry both <c>ElementId(int)</c> and <c>IntegerValue</c>
/// alongside the 64-bit pair, while <b>Revit 2026 and 2027 have neither</b> - the 32-bit surface is
/// gone, not deprecated. Code written against <c>IntegerValue</c> does not compile on half the
/// supported range, and code that narrows to <see cref="int"/> would truncate on the other half.
/// </para>
/// <para>
/// <b>Two numbers, not one, because carriers live in links.</b> Cable trays are routinely modelled
/// in an MEP file and the circuits in the electrical one, so an element id alone is ambiguous:
/// the same number means different elements in the host and in each link. <see cref="Source"/> is
/// zero for the host and the link instance's own id otherwise, which is enough for the Revit side
/// to resolve the pair back and enough for this side to keep them apart.
/// </para>
/// <para>
/// It also answers a question the interface has to ask: <see cref="IsLinked"/> marks a carrier we
/// can route through but cannot write to. A linked document reports <c>IsLinked</c> and is not
/// modifiable from the host - so tray fill has nowhere to go for those, and the screen must say so
/// rather than silently write nothing. That last part is inferred from the shape of the API
/// (<c>Document.IsReadOnly</c>, <c>IsModifiable</c>) and is <b>a note, not a measurement</b>: it is
/// proven by attempting the write on a live model, and that has not been done.
/// </para>
/// <para>
/// The conversion to and from Revit's own identifier lives in exactly one place on the Revit side,
/// where the link's <c>GetTotalTransform()</c> also puts the geometry into host coordinates.
/// Here it is an opaque pair: this assembly must be able to say which element a route ran through
/// without being able to ask the model anything about it.
/// </para>
/// </remarks>
public readonly struct CarrierId : IEquatable<CarrierId>
{
    public CarrierId(long value) : this(0, value)
    {
    }

    public CarrierId(long source, long value)
    {
        Source = source;
        Value = value;
    }

    /// <summary>Zero for the host model; the link instance's own id for anything inside a link.</summary>
    public long Source { get; }

    public long Value { get; }

    /// <summary>Whether this element lives in a link, and therefore cannot be written to.</summary>
    public bool IsLinked => Source != 0;

    public bool Equals(CarrierId other) => Value == other.Value && Source == other.Source;

    public override bool Equals(object? other) => other is CarrierId id && Equals(id);

    public override int GetHashCode() => unchecked((Source.GetHashCode() * 397) ^ Value.GetHashCode());

    public override string ToString() =>
        Source == 0
            ? Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Source.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
              + Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(CarrierId left, CarrierId right) => left.Equals(right);

    public static bool operator !=(CarrierId left, CarrierId right) => !left.Equals(right);
}

/// <summary>A point, in Revit's internal units.</summary>
/// <remarks>
/// <b>Internal feet, raw, and never formatted here.</b> Turning a length into something a person
/// reads needs the document's own unit settings, which live behind <c>UnitFormatUtils</c> and a
/// <c>Document</c> - neither of which exists on this axis. Formatting therefore belongs to the side
/// that has a document, and the temptation to do it "properly, by project settings" in a view model
/// is the quietest way to put a Revit API call on a background thread.
/// </remarks>
public readonly struct Point3
{
    public Point3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    public double DistanceTo(Point3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;

        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>The point on the segment from <paramref name="from"/> to <paramref name="to"/> nearest to this one.</summary>
    /// <remarks>
    /// Clamped to the segment, not to the infinite line: a device beyond the end of a tray taps the
    /// tray at its end, not at a point in the air past it.
    /// </remarks>
    public Point3 NearestOn(Point3 from, Point3 to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        var square = (dx * dx) + (dy * dy) + (dz * dz);

        // A degenerate segment is a point, and a fitting whose connectors coincide is exactly that.
        if (square <= 1e-12)
            return from;

        var along = (((X - from.X) * dx) + ((Y - from.Y) * dy) + ((Z - from.Z) * dz)) / square;

        if (along <= 0)
            return from;

        if (along >= 1)
            return to;

        return new Point3(from.X + (along * dx), from.Y + (along * dy), from.Z + (along * dz));
    }
}

/// <summary>What kind of thing a carrier is.</summary>
/// <remarks>
/// Kept apart from the class name because the two answer different questions. The kind decides
/// whether a node has length worth walking (a fitting is a joint, not a run); the class is what the
/// preference between conduit and tray is expressed in, and it is a string because the set of
/// carrier categories is the user's to configure - the predecessor hard-coded it and then grew nine
/// device categories and a generic-model escape hatch anyway.
/// </remarks>
public enum CarrierKind
{
    Segment,
    Fitting,
}

/// <summary>One piece of cable-bearing structure.</summary>
public sealed class CarrierNode
{
    public CarrierNode(
        CarrierId id,
        CarrierKind kind,
        string carrierClass,
        double length,
        double crossSectionArea,
        Point3 start,
        Point3 end,
        IReadOnlyList<Point3>? terminals = null)
    {
        Id = id;
        Kind = kind;
        Class = carrierClass;
        Length = length;
        CrossSectionArea = crossSectionArea;
        Start = start;
        End = end;
        Terminals = terminals ?? new[] { start, end };
    }

    public CarrierId Id { get; }

    public CarrierKind Kind { get; }

    /// <summary>What the user's configured category resolved to - "tray", "conduit", or their own.</summary>
    public string Class { get; }

    public double Length { get; }

    /// <summary>Free area, for the fill calculation. Zero when the model does not say.</summary>
    public double CrossSectionArea { get; }

    public Point3 Start { get; }

    public Point3 End { get; }

    /// <summary>
    /// Every point at which another carrier may join this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two is the common case and not the general one, and the difference was measured as a
    /// defect.</b> A carrier used to be described by <see cref="Start"/> and <see cref="End"/> alone,
    /// taken as the two connector origins that lie farthest apart. For a straight run that is exact.
    /// For a tee it silently discards the branch, and for a cross it discards two - so a tray landing
    /// on the branch of a tee is near no recorded point of it, and does not join.
    /// </para>
    /// <para>
    /// On a real model that turned twenty-six circuits of fifty-five into "both ends reachable, but
    /// nothing joins them", over a structure that read as forty-four separate groups. The signature
    /// was in the tolerance ladder: the groups fell away gradually - 44, 31, 14, 3, 1 across 50 mm to
    /// a metre - rather than at one step. Slop at a joint clusters at one size; half a fitting's
    /// length varies with the size of the tray, which is exactly a spread.
    /// </para>
    /// <para>
    /// <see cref="Start"/> and <see cref="End"/> stay, and stay the extremes: they are the carrier's
    /// reach, which is what a length and a drawing want. Joining is a different question and now has
    /// its own answer.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Point3> Terminals { get; }

    /// <summary>
    /// The point on this carrier nearest to <paramref name="at"/>, wherever along it that falls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A terminal is where a carrier joins another; this is where a cable can leave it.</b> The two
    /// are different questions and the code answered only the first, which is why a socket under the
    /// middle of a twenty-metre tray was measured to the tray's nearest end.
    /// </para>
    /// <para>
    /// A segment is a straight run between its extremes, so the answer is the projection onto it,
    /// clamped. A fitting is not a straight anything - its body turns - so the honest answer there is
    /// the nearest terminal, and no worse than what a fitting's short length allows.
    /// </para>
    /// </remarks>
    public Point3 NearestPointTo(Point3 at)
    {
        if (Kind == CarrierKind.Segment)
            return at.NearestOn(Start, End);

        var best = Terminals.Count > 0 ? Terminals[0] : Start;
        var distance = at.DistanceTo(best);

        for (var i = 1; i < Terminals.Count; i++)
        {
            var candidate = at.DistanceTo(Terminals[i]);

            if (candidate >= distance)
                continue;

            distance = candidate;
            best = Terminals[i];
        }

        return best;
    }

    /// <summary>The name a person would recognise, carried because <c>Element.Name</c> is an API call.</summary>
    public string Label { get; init; } = string.Empty;
}
