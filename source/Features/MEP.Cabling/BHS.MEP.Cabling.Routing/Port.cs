namespace BHS.MEP.Cabling.Routing;

/// <summary>
/// One terminal of one carrier: where the search stands between two steps.
/// </summary>
/// <remarks>
/// <para>
/// <b>The vertex of the search, and the reason it is not the carrier.</b> A carrier as a vertex
/// carries one cost, so every route through it pays the same - its whole length. That was chosen
/// deliberately, because the alternative then on offer was paying nothing for a carrier entered and
/// left again. Neither is true of a cable that joins a run at its middle: it walks the part between
/// where it joined and where it leaves, and one number per carrier cannot hold two answers.
/// </para>
/// <para>
/// A struct, and a hand-written one. This is the key of three dictionaries in the hot loop of a
/// search that runs over thousands of carriers while somebody watches a progress bar; a class would
/// allocate one object per push, and the record forms that would write the equality members are
/// C# 10, which <c>net48</c> in this solution does not get.
/// </para>
/// </remarks>
internal readonly struct Port : IEquatable<Port>
{
    public Port(CarrierId carrier, int terminal)
    {
        Carrier = carrier;
        Terminal = terminal;
    }

    /// <summary>The carrier this terminal belongs to.</summary>
    public CarrierId Carrier { get; }

    /// <summary>Its index in <see cref="CarrierNode.Terminals"/> - not a point, so it stays comparable.</summary>
    public int Terminal { get; }

    public bool Equals(Port other) => Carrier.Equals(other.Carrier) && Terminal == other.Terminal;

    public override bool Equals(object? obj) => obj is Port other && Equals(other);

    public override int GetHashCode() => unchecked((Carrier.GetHashCode() * 397) ^ Terminal);
}
