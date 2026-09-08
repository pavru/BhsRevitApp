using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Turns the cable-bearing elements of one document into nodes the search can hold.
/// </summary>
/// <remarks>
/// <para>
/// One document, one transform, one source id - so the same code reads the host model and each
/// link, and the link's <c>GetTotalTransform()</c> is applied here rather than anywhere downstream.
/// This is the only place in the program that knows a link exists; past it there is one coordinate
/// system and <see cref="CarrierId.Source"/> to tell the elements apart.
/// </para>
/// <para>
/// <b>Measured, not assumed: carriers really do live in links.</b> A surveyed project held 21 trays
/// and 176 conduits in the host and 13 and 71 more in one of its three links, with every circuit in
/// the host. Code that read the host alone would have found a route for some circuits and reported
/// "no carrier near" for the rest, which is the worst of the failure modes because it looks like a
/// modelling problem.
/// </para>
/// </remarks>
public sealed class CarrierReader
{
    private readonly CarrierCatalogue _catalogue;

    public CarrierReader(CarrierCatalogue catalogue) => _catalogue = catalogue;

    /// <summary>Carriers that were collected and could not be placed, over every call so far.</summary>
    /// <remarks>
    /// <b>A dropped carrier does not fail loudly, which is why it is counted.</b> It leaves a hole
    /// in the structure, and the hole surfaces later as "no connectivity" for every circuit whose
    /// route ran through it - a report that sends somebody looking at their model for a fault that
    /// is in ours.
    ///
    /// Correct only once the sequence has been enumerated to the end: <see cref="Read"/> is lazy,
    /// and the counter fills as it is walked.
    /// </remarks>
    public int Skipped { get; private set; }

    /// <summary>Reads every carrier of every configured category, in host coordinates.</summary>
    /// <param name="document">The model to read - the host, or a link's own document.</param>
    /// <param name="source">Zero for the host; the link instance's id for a link.</param>
    /// <param name="transform">The link's total transform, or the identity for the host.</param>
    public IEnumerable<CarrierNode> Read(Document document, long source, Transform transform)
    {
        foreach (var category in _catalogue.Categories)
        {
            var carrierClass = _catalogue.ClassOf(category);

            var found = new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsNotElementType();

            foreach (var element in found)
            {
                var node = Read(element, source, transform, carrierClass);

                if (node is null)
                    Skipped++;
                else
                    yield return node;
            }
        }
    }

    private static CarrierNode? Read(Element element, long source, Transform transform, string carrierClass)
    {
        if (!TryExtent(element, out var start, out var end))
            return null;

        start = transform.OfPoint(start);
        end = transform.OfPoint(end);

        var from = new Point3(start.X, start.Y, start.Z);
        var to = new Point3(end.X, end.Y, end.Z);

        // A fitting is a joint rather than a run: it connects, and its own length is not walked.
        var kind = element is MEPCurve ? CarrierKind.Segment : CarrierKind.Fitting;

        // Label is left empty on purpose. It costs an API call per element - thousands of them on a
        // real model - and nothing reads it: a result names the circuit and the device it could not
        // reach, never the individual tray it ran along. The field stays because the day a screen
        // lists the run, filling it is one line here rather than a change of shape.
        return new CarrierNode(
            new CarrierId(source, element.Id.Value),
            kind,
            carrierClass,
            kind == CarrierKind.Segment ? from.DistanceTo(to) : 0,
            CrossSection(element, carrierClass),
            from,
            to);
    }

    /// <summary>
    /// Where a carrier begins and ends: its connectors, and its geometry only if it has none.
    /// </summary>
    /// <remarks>
    /// <b>Connectors first, because that is where the next run joins.</b> A fitting has no
    /// <c>LocationCurve</c> at all, so for it there is nothing else; and for a segment the connector
    /// is the honest end, since a curve can extend past the point another run meets it.
    ///
    /// The fallback matters more than it looks. A single element whose connectors cannot be read
    /// would otherwise drop out of the network, and a missing carrier does not fail loudly - it
    /// turns into "no connectivity" for every circuit whose route went through it.
    /// </remarks>
    private static bool TryExtent(Element element, out XYZ start, out XYZ end)
    {
        start = XYZ.Zero;
        end = XYZ.Zero;

        var manager = element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null,
        };

        if (manager is not null && TryExtremes(manager, out start, out end))
            return true;

        switch (element.Location)
        {
            case LocationCurve located when located.Curve is not null:
                start = located.Curve.GetEndPoint(0);
                end = located.Curve.GetEndPoint(1);
                return true;

            case LocationPoint located:
                start = located.Point;
                end = located.Point;
                return true;

            default:
                // Last rung, and the same reasoning as for a panel: a fitting with neither
                // connectors nor a location still occupies space, and the middle of that space is
                // a better place to put it than nowhere at all - a dropped carrier is a hole in the
                // network, and a hole reports itself later as somebody else's connectivity fault.
                if (element.CentreOrNull() is not { } centre)
                    return false;

                start = centre;
                end = centre;
                return true;
        }
    }

    /// <summary>The two connector origins that lie farthest apart.</summary>
    /// <remarks>
    /// A tee has three and a cross has four, and any two of them describe the fitting badly; the
    /// extremes describe its reach, which is what adjacency and the index are asking about.
    /// </remarks>
    private static bool TryExtremes(ConnectorManager manager, out XYZ start, out XYZ end)
    {
        start = XYZ.Zero;
        end = XYZ.Zero;

        var origins = new List<XYZ>();

        foreach (Connector connector in manager.Connectors)
        {
            // A connector without a place is skipped rather than asked: reading Origin on a logical
            // one throws. See Connectors.OriginOrNull - measured on a real model.
            if (connector.OriginOrNull() is { } origin)
                origins.Add(origin);
        }

        if (origins.Count == 0)
            return false;

        start = origins[0];
        end = origins[0];
        var widest = 0.0;

        for (var i = 0; i < origins.Count; i++)
        for (var j = i + 1; j < origins.Count; j++)
        {
            var span = origins[i].DistanceTo(origins[j]);

            if (span <= widest)
                continue;

            widest = span;
            start = origins[i];
            end = origins[j];
        }

        return true;
    }

    /// <summary>
    /// Free area, for the fill calculation that is not written yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Width</c>, <c>Height</c> and <c>Diameter</c> are declared on <c>MEPCurve</c> itself on all
    /// four releases, so this needs no <c>BuiltInParameter</c> lookup - and parameter identifiers
    /// are precisely the thing that moves between releases.
    /// </para>
    /// <para>
    /// <b>Which of the two diameters a conduit reports is not measured.</b> Fill wants the inner
    /// one. The number is recorded now because it is free to record while the element is in hand,
    /// and it is not used for anything until the fill calculation asks - at which point the question
    /// gets answered rather than guessed.
    /// </para>
    /// </remarks>
    private static double CrossSection(Element element, string carrierClass)
    {
        if (element is not MEPCurve curve)
            return 0;

        if (string.Equals(carrierClass, CarrierCatalogue.Conduit, StringComparison.Ordinal))
        {
            var radius = curve.Diameter / 2;
            return Math.PI * radius * radius;
        }

        return curve.Width * curve.Height;
    }
}
