using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Everything the search needs, taken from the model in one pass on the API thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole point is that nothing here is lazy.</b> Past this type the routing runs on a
/// background thread, where the type <c>Document</c> does not exist at all - so a field left to be
/// fetched later is not a slow path, it is a Revit API call from the wrong thread, and those do not
/// fail politely.
/// </para>
/// <para>
/// The counts of what was skipped travel with what was taken. A snapshot that quietly read three of
/// four links produces routes that are wrong in a way nobody can see; the same snapshot that says
/// "one link was not loaded" produces a question somebody can answer.
/// </para>
/// </remarks>
public sealed class CablingSnapshot
{
    /// <remarks>
    /// The counts are constructor arguments rather than optional properties, and that is the point:
    /// a snapshot which does not say how many links it skipped would default them to zero, and zero
    /// is the answer that means "nothing was missed". Every place that builds one has to answer.
    /// </remarks>
    public CablingSnapshot(
        RouteNetwork network,
        IReadOnlyList<CarrierNode> carriers,
        CircuitHarvest circuits,
        int linksRead,
        int linksNotLoaded,
        int nestedLinksIgnored,
        int carriersSkipped,
        int markersExcluded = 0,
        bool markerTypeKnown = false,
        IReadOnlyList<ExistingBox>? boxes = null,
        int boxesUnconnected = 0,
        IReadOnlyList<long>? boxesUnconnectedIds = null)
    {
        Boxes = boxes ?? Array.Empty<ExistingBox>();
        BoxesUnconnected = boxesUnconnected;
        BoxesUnconnectedIds = boxesUnconnectedIds ?? Array.Empty<long>();
        Network = network;
        Carriers = carriers;
        Circuits = circuits;
        LinksRead = linksRead;
        LinksNotLoaded = linksNotLoaded;
        NestedLinksIgnored = nestedLinksIgnored;
        CarriersSkipped = carriersSkipped;
        MarkersExcluded = markersExcluded;
        MarkerTypeKnown = markerTypeKnown;
    }

    public RouteNetwork Network { get; }

    /// <summary>The carriers the network was built from.</summary>
    /// <remarks>
    /// Kept as well as the network, because the network is the answer to one join tolerance and the
    /// question that follows a failed run is what a different tolerance would have given. Rebuilding
    /// from these costs a pass over a few hundred elements; reading the model again costs the read.
    /// </remarks>
    public IReadOnlyList<CarrierNode> Carriers { get; }

    public CircuitHarvest Circuits { get; }

    /// <summary>Link instances whose carriers were read.</summary>
    public int LinksRead { get; }

    /// <summary>Link instances that are placed but not loaded, and therefore hold nothing.</summary>
    public int LinksNotLoaded { get; }

    /// <summary>Links inside links, which this pass does not follow.</summary>
    /// <remarks>
    /// Counted rather than followed, and counted rather than ignored. Following them means composing
    /// transforms and deciding what a nested element's <see cref="CarrierId.Source"/> should be,
    /// which is a shape decision worth making against a model that has them rather than in advance.
    /// Until then a number greater than zero here is the sign that this snapshot is incomplete, and
    /// the screen can say so instead of the routes being quietly short.
    /// </remarks>
    public int NestedLinksIgnored { get; }

    /// <summary>Carriers that were collected and had no readable extent.</summary>
    /// <remarks>See <see cref="CarrierReader.Skipped"/> for why a hole in the structure is worth a
    /// number: it surfaces later as somebody's circuit having no connectivity.</remarks>
    public int CarriersSkipped { get; }

    /// <summary>Markers of ours that were met and left out of the structure.</summary>
    /// <remarks>
    /// <b>Zero on a model nobody has run the calculation against, and that is the normal case.</b>
    /// It becomes interesting the second time: markers of the previous run are still there, and the
    /// number says the exclusion found them. Measured on the owner model - one free-standing marker
    /// takes the carrier count from 358 to 359 - so an exclusion that quietly matched nothing would
    /// be indistinguishable from one that worked.
    /// </remarks>
    public int MarkersExcluded { get; }

    /// <summary>Whether the model holds the marker type the project names.</summary>
    /// <remarks>
    /// False is ordinary on a model that has never been calculated. It is worth saying out loud
    /// anyway, because the way this goes wrong is a renamed type: the markers stay in the model,
    /// stop being recognised, and are read as structure from that day on.
    /// </remarks>
    public bool MarkerTypeKnown { get; }

    /// <summary>The junction boxes already in the model and joined to the structure.</summary>
    public IReadOnlyList<ExistingBox> Boxes { get; }

    /// <summary>How many elements call themselves boxes and are joined to nothing.</summary>
    public int BoxesUnconnected { get; }

    /// <summary>Which of them are in the host, so a warning can be posted against them.</summary>
    /// <remarks>Fewer than <see cref="BoxesUnconnected"/> when links hold some: a warning in the
    /// host cannot address an element of a link.</remarks>
    public IReadOnlyList<long> BoxesUnconnectedIds { get; }

    /// <summary>Reads the host and its links, and builds the network and the circuits.</summary>
    public static CablingSnapshot Build(
        Document host,
        RoutingOptions options,
        CarrierCatalogue catalogue,
        long version,
        RecommendedBoxes? boxes = null,
        CircuitConnection defaultConnection = CircuitConnection.AtTerminal)
    {
        var reader = new CarrierReader(catalogue, boxes);
        var carriers = new List<CarrierNode>(reader.Read(host, 0, Transform.Identity));

        var links = new FilteredElementCollector(host)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();

        var read = 0;
        var notLoaded = 0;
        var nested = 0;

        foreach (var link in links)
        {
            var linked = link.GetLinkDocument();

            if (linked is null)
            {
                notLoaded++;
                continue;
            }

            carriers.AddRange(reader.Read(linked, link.Id.Value, link.GetTotalTransform()));
            read++;

            nested += new FilteredElementCollector(linked)
                .OfClass(typeof(RevitLinkInstance))
                .GetElementCount();
        }

        return new CablingSnapshot(
            NetworkBuilder.Build(version, carriers, options),
            carriers,
            new CircuitReader(defaultConnection).Read(host),
            read,
            notLoaded,
            nested,
            reader.Skipped,
            reader.Markers,
            reader.MarkerTypeKnown,
            reader.Boxes,
            reader.BoxesUnconnected,
            reader.BoxesUnconnectedIds);
    }
}
