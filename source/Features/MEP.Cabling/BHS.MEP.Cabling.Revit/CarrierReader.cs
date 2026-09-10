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

    private readonly RecommendedBoxes? _boxes;

    public CarrierReader(CarrierCatalogue catalogue, RecommendedBoxes? boxes = null)
    {
        _catalogue = catalogue;
        _boxes = boxes;
    }

    /// <summary>Markers of ours met while reading, which are not structure and were left out.</summary>
    /// <remarks>
    /// <b>Counted rather than dropped in silence, because the count is the proof the exclusion
    /// works.</b> Measured on the owner model: one free-standing marker of a cable-tray-fitting
    /// family took the carrier count from 358 to 359, so an exclusion that quietly did nothing would
    /// look exactly like an exclusion that worked.
    /// </remarks>
    public int Markers { get; private set; }

    /// <summary>Whether the document being read holds the configured marker type at all.</summary>
    /// <remarks>
    /// False is not automatically wrong - most models have never had a marker placed. It becomes
    /// news when a run has placed some before, and the way to notice is a project that renamed the
    /// type: the markers stay in the model and are read as structure from then on.
    /// </remarks>
    public bool MarkerTypeKnown { get; private set; }

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
        // Resolved once per document rather than per element: the lookup walks every family symbol,
        // and a survey crosses the host and every link.
        var markers = _boxes is null
            ? RecommendedBoxMarkers.None
            : RecommendedBoxMarkers.For(document, _boxes);

        MarkerTypeKnown |= markers.Known;

        foreach (var category in _catalogue.Categories)
        {
            var carrierClass = _catalogue.ClassOf(category);

            var found = new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsNotElementType();

            foreach (var element in found)
            {
                // Ours, and therefore not structure. A marker sits on the trace by construction and
                // its family is usually a fitting, so without this it is collected as a carrier and
                // the network grows a node the project does not have.
                if (markers.Marks(element))
                {
                    Markers++;
                    continue;
                }

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
        if (!TryExtent(element, out var start, out var end, out var joins))
            return null;

        start = transform.OfPoint(start);
        end = transform.OfPoint(end);

        var from = new Point3(start.X, start.Y, start.Z);
        var to = new Point3(end.X, end.Y, end.Z);

        // Every join point, through the same transform. A fitting may carry any number of
        // connectors - the owner's own correction, and the reason nothing here counts them - so this
        // is a list rather than a third and fourth field.
        var terminals = new Point3[joins.Count];

        for (var i = 0; i < joins.Count; i++)
        {
            var at = transform.OfPoint(joins[i]);
            terminals[i] = new Point3(at.X, at.Y, at.Z);
        }

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
            to,
            terminals);
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
    private static bool TryExtent(Element element, out XYZ start, out XYZ end, out IReadOnlyList<XYZ> joins)
    {
        start = XYZ.Zero;
        end = XYZ.Zero;
        joins = Array.Empty<XYZ>();

        var manager = element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null,
        };

        if (manager is not null && TryExtremes(manager, out start, out end, out joins))
            return true;

        // Every rung below describes the element by two points at most, and both are join points:
        // a run joins at the ends of its curve, and an element known only by one point joins there.
        switch (element.Location)
        {
            case LocationCurve located when located.Curve is not null:
                start = located.Curve.GetEndPoint(0);
                end = located.Curve.GetEndPoint(1);
                joins = new[] { start, end };
                return true;

            case LocationPoint located:
                start = located.Point;
                end = located.Point;
                joins = new[] { start };
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
                joins = new[] { centre };
                return true;
        }
    }

    /// <summary>
    /// The extremes of a carrier's reach, and every point at which something may join it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The old comment here named the defect and then committed it.</b> It said "a tee has three
    /// and a cross has four, and any two of them describe the fitting badly" - and returned two,
    /// because adjacency and the index only asked for two. The branch of every tee was therefore
    /// invisible, and a tray landing on it joined nothing.
    /// </para>
    /// <para>
    /// <b>The count is not three or four either.</b> The owner's correction: a fitting may have as
    /// many connectors as it likes. So nothing here counts them, nothing assumes a shape, and the
    /// origins travel as a list - see <see cref="BHS.MEP.Cabling.Routing.CarrierNode.Terminals"/>.
    /// </para>
    /// <para>
    /// The extremes are still computed, because a length and a drawing want the reach; they are now
    /// a summary of the terminals rather than a replacement for them. The pairwise walk that finds
    /// them is quadratic in the connector count, which is why it runs over the origins already
    /// gathered rather than asking Revit again.
    /// </para>
    /// </remarks>
    private static bool TryExtremes(ConnectorManager manager, out XYZ start, out XYZ end, out IReadOnlyList<XYZ> joins)
    {
        start = XYZ.Zero;
        end = XYZ.Zero;
        joins = Array.Empty<XYZ>();

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

        joins = origins;
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
