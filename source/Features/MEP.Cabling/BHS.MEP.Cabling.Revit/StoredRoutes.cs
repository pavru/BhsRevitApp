using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// What the circuits of a model carry from an earlier apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reading half of the check for stale lengths</b> - the comparison itself is on the plain
/// axis, in <see cref="LengthReview"/>, because it is arithmetic over two records and is worth
/// proving on networks a person can check by hand. This side does the one thing that needs a
/// <c>Document</c>: read the three values the apply writes, exactly as they stand.
/// </para>
/// <para>
/// <b>Absent and zero are kept apart all the way through.</b> A circuit nobody ever applied carries
/// nothing, a length of zero written where no route was found is the defect this check exists to
/// find, and a parameter that is bound but unset is the first of the two. So every value is read
/// through <c>HasValue</c> rather than through a default.
/// </para>
/// </remarks>
public static class StoredRoutes
{
    /// <summary>One millimetre in internal feet: how far two lengths may differ and still agree.</summary>
    /// <remarks>
    /// <b>A tolerance rather than an exact comparison, and a small one.</b> The length is stored as a
    /// double and read back as the same double, so an unchanged model differs by zero; the tolerance is
    /// there for the value somebody rounded in a schedule or typed by hand, and a millimetre is below
    /// anything a cable schedule distinguishes.
    /// </remarks>
    public const double Tolerance = 1 / 304.8;

    /// <summary>
    /// Reads what each of these circuits carries. Circuits are host elements, so nothing here is linked.
    /// </summary>
    /// <remarks>
    /// An id that no longer resolves is stepped over rather than reported: the circuits come from a read
    /// of this same document, and one that disappeared between the read and this call is a model somebody
    /// is editing, not a finding about a stored length.
    /// </remarks>
    public static IReadOnlyDictionary<CarrierId, StoredRoute> Read(Document host, IEnumerable<CarrierId> circuits)
    {
        var stored = new Dictionary<CarrierId, StoredRoute>();

        if (host is null || circuits is null)
            return stored;

        foreach (var circuit in circuits)
        {
            if (stored.ContainsKey(circuit))
                continue;

            if (host.GetElement(new ElementId(circuit.Value)) is not { } element)
                continue;

            stored[circuit] = new StoredRoute(circuit, Length(element), Connection(element), Text(element));
        }

        return stored;
    }

    private static double? Length(Element element) =>
        element.get_Parameter(CablingParameters.CableLength) is { HasValue: true } found
            ? found.AsDouble()
            : null;

    /// <summary>
    /// The connection the stored length was computed with, or null when none is stored or it does not read.
    /// </summary>
    /// <remarks>
    /// A value nobody can read is treated as none rather than as a difference. It is our own output
    /// parameter, so a word we do not recognise means an older version of us or a hand edit - and
    /// reporting every such circuit as "routed in the other connection" would say something false about
    /// which of the two the length was computed with.
    /// </remarks>
    private static CircuitConnection? Connection(Element element)
    {
        var found = element.get_Parameter(CablingParameters.RouteConnection);

        if (found is not { HasValue: true })
            return null;

        return CircuitConnections.TryParse(found.AsString(), out var connection) ? connection : null;
    }

    private static string Text(Element element) =>
        element.get_Parameter(CablingParameters.RouteStamp) is { HasValue: true } found
            ? found.AsString() ?? string.Empty
            : string.Empty;
}
