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
        CircuitHarvest circuits,
        int linksRead,
        int linksNotLoaded,
        int nestedLinksIgnored,
        int carriersSkipped)
    {
        Network = network;
        Circuits = circuits;
        LinksRead = linksRead;
        LinksNotLoaded = linksNotLoaded;
        NestedLinksIgnored = nestedLinksIgnored;
        CarriersSkipped = carriersSkipped;
    }

    public RouteNetwork Network { get; }

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

    /// <summary>Reads the host and its links, and builds the network and the circuits.</summary>
    public static CablingSnapshot Build(
        Document host,
        RoutingOptions options,
        CarrierCatalogue catalogue,
        long version)
    {
        var reader = new CarrierReader(catalogue);
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
            new CircuitReader().Read(host),
            read,
            notLoaded,
            nested,
            reader.Skipped);
    }
}
