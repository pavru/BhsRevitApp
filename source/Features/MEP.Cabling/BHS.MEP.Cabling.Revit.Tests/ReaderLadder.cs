using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// Which of the circuit reader's ways gave a circuit end its point, asked again of the document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reader does not say, and a test cannot ask it.</b> <c>CircuitReader</c> tries four ways for a
/// panel and four for a device and keeps the first that answers; <c>Terminal</c> carries the point and
/// not the way. The last two ways are guesses by the reader's own account - the centre of a tall panel's
/// box can sit a metre below where a cable leaves it - so "which way answered" is the first question to
/// put to an end that no carrier reaches. The reader's helpers are internal and nothing in this
/// repository grants the tests access to internals, so the ladder is written again here.
/// </para>
/// <para>
/// <b>Written again, and therefore pinned rather than trusted.</b> A copy that drifted from the reader
/// would name a way the reader never took. Every caller compares the point this gives with the point the
/// reader gave, and says so when the two differ; the census asserts it for every end it can see.
/// </para>
/// <para>
/// <b>The same calls in the same order, and nothing caught.</b> The reader asks these of the same
/// elements without catching anything, so on an end the reader described they cannot throw here either;
/// a caller asking about an element the reader never asked decides for itself what a refusal means.
/// </para>
/// </remarks>
internal static class ReaderLadder
{
    /// <summary>What a panel end's way is called in a note, by its number; zero is no way at all.</summary>
    public static readonly string[] PanelWays =
    {
        "no way",
        "the connector the circuit is fed from",
        "the panel's first physical electrical connector",
        "the centre of its bounding box",
        "its insertion point",
    };

    /// <summary>What a device end's way is called in a note, by its number; zero is no way at all.</summary>
    public static readonly string[] DeviceWays =
    {
        "no way",
        "its physical electrical connector on this circuit",
        "its first physical electrical connector",
        "the centre of its bounding box",
        "its insertion point",
    };

    /// <summary>The two ways that are guesses rather than connectors.</summary>
    public static bool IsGuess(int way) => way >= 3;

    /// <summary>
    /// The panel end of a circuit: <c>CircuitReader.SourceTerminal</c>, again.
    /// </summary>
    public static Answer Panel(ElectricalSystem system)
    {
        var panel = system.BaseEquipment;

        if (panel is null)
            return default;

        if (Origin(system.BaseEquipmentConnector) is { } fed)
            return new Answer(1, fed);

        var manager = panel.MEPModel?.ConnectorManager;

        if (manager is not null)
        {
            foreach (Connector connector in manager.Connectors)
            {
                if (IsElectrical(connector) && Origin(connector) is { } origin)
                    return new Answer(2, origin);
            }
        }

        return Guessed(panel);
    }

    /// <summary>
    /// A device end of a circuit: <c>CircuitReader.DeviceOrigin</c>, again.
    /// </summary>
    /// <remarks>
    /// A connector on this circuit wins wherever it stands in the list, and the first physical electrical
    /// connector answers only when none is - the reader's order, which is not the order the list is in.
    /// </remarks>
    public static Answer Device(Element element, ElectricalSystem system)
    {
        var manager = (element as FamilyInstance)?.MEPModel?.ConnectorManager;

        if (manager is not null)
        {
            XYZ? first = null;

            foreach (Connector connector in manager.Connectors)
            {
                if (!IsElectrical(connector))
                    continue;

                if (Origin(connector) is not { } origin)
                    continue;

                if (connector.MEPSystem is { } on && on.Id.Value == system.Id.Value)
                    return new Answer(1, origin);

                first ??= origin;
            }

            if (first is not null)
                return new Answer(2, first);
        }

        return Guessed(element);
    }

    /// <summary>Whether a way's point is the point the reader put an end at.</summary>
    /// <remarks>
    /// A billionth of a foot, not zero: both are the same doubles read through the same calls, and the
    /// tolerance only keeps a conversion through <c>Point3</c> from reading as a different way.
    /// </remarks>
    public static bool Same(XYZ? at, Point3 point) =>
        at is not null
        && Math.Abs(at.X - point.X) <= 1e-9
        && Math.Abs(at.Y - point.Y) <= 1e-9
        && Math.Abs(at.Z - point.Z) <= 1e-9;

    /// <summary>The model box of an element, as the reader's centre is taken from.</summary>
    public static BoundingBoxXYZ? Box(Element element) => element.get_BoundingBox(null);

    /// <summary>The reader's last two ways, in its order.</summary>
    private static Answer Guessed(Element element)
    {
        if (Box(element) is { } box)
            return new Answer(3, (box.Min + box.Max) / 2);

        if ((element.Location as LocationPoint)?.Point is { } inserted)
            return new Answer(4, inserted);

        return default;
    }

    /// <summary><c>Connectors.OriginOrNull</c>, again: the type is tested, the origin is not caught.</summary>
    private static XYZ? Origin(Connector? connector) =>
        connector is not null && (connector.ConnectorType & ConnectorType.Physical) != 0
            ? connector.Origin
            : null;

    /// <summary><c>Connectors.IsElectrical</c>, again.</summary>
    private static bool IsElectrical(Connector? connector) =>
        connector is not null && connector.Domain == Domain.DomainElectrical;

    /// <summary>A way, by number, and the point it gave; zero and nothing when no way answered.</summary>
    public readonly struct Answer
    {
        public Answer(int way, XYZ? at)
        {
            Way = way;
            At = at;
        }

        public int Way { get; }

        public XYZ? At { get; }
    }
}
