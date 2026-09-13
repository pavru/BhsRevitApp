using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Tells a junction box already in the model from a tee, and a real one from our own indicator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two signs, answering two different questions - the owner's distinction, and confusing them is
/// how this goes wrong.</b> Whether an element is a box at all is answered by
/// <c>BHS_Cbl_ElementRole</c> on its <b>type</b>: geometry cannot tell a box from a tee, and the
/// designer is the one who knows. Whether it is a <b>real</b> box is answered by connection: a box
/// that is part of the wiring is joined to the carrier network by its connectors, and our indicator
/// is joined to nothing at all.
/// </para>
/// <para>
/// <b>Connection is asked of Revit, never of our own network.</b> <c>Touches</c> is proximity between
/// terminals, and an indicator standing on the trace is proximate by construction - it is put there
/// on purpose. <c>Connector.IsConnected</c> and <c>AllRefs</c> describe a joint somebody made.
/// </para>
/// <para>
/// <b>Joined to a carrier, not merely joined.</b> A fitting whose only connection is to a device
/// answers "connected" and is not a box in a cable run. So the far end of the joint has to be an
/// element of a category the catalogue collects - which is the same list the network is built from,
/// and therefore the user's list rather than a constant here.
/// </para>
/// <para>
/// <b>The element stays a carrier.</b> A real box is a fitting, part of the structure, and the route
/// already passes through it; nothing here removes it from the network. That is the whole difference
/// from a marker of ours, which is excluded - see <see cref="RecommendedBoxMarkers"/>.
/// </para>
/// </remarks>
internal sealed class JunctionBoxReader
{
    private readonly Document _document;
    private readonly CarrierCatalogue _catalogue;

    /// <summary>Role by type id: a run has thousands of fittings and a handful of types.</summary>
    private readonly Dictionary<long, bool> _roles = new();

    public JunctionBoxReader(Document document, CarrierCatalogue catalogue)
    {
        _document = document;
        _catalogue = catalogue;
    }

    /// <summary>Elements whose type says they are boxes but which are joined to nothing.</summary>
    /// <remarks>
    /// Counted rather than silently dropped. A box drawn beside a run instead of in it is a modelling
    /// fault of exactly the kind this tool exists to surface - and one that looks, from the routing
    /// side, like a box the calculation ignored for no reason.
    /// </remarks>
    public IReadOnlyList<long> Unconnected => _unconnected;

    private readonly List<long> _unconnected = new();

    /// <summary>Whether this element is a real junction box, counting the ones that are not joined.</summary>
    public bool IsRealBox(Element element)
    {
        if (element is null || !HasBoxRole(element))
            return false;

        if (JoinsACarrier(element))
            return true;

        // The id and not only a tally: this ends up in Revit's own warning list, which addresses
        // an element. A count can be shown on a screen and cannot be pointed at.
        _unconnected.Add(element.Id.Value);
        return false;
    }

    private bool HasBoxRole(Element element)
    {
        var type = element.GetTypeId();

        if (type == ElementId.InvalidElementId)
            return false;

        if (_roles.TryGetValue(type.Value, out var known))
            return known;

        var role = _document.GetElement(type)?.get_Parameter(CablingParameters.ElementRole)?.AsString();

        // Ordinal, and not translated: the decision recorded with the parameter scheme is that names
        // are read by people and values are compared by code.
        known = string.Equals(role?.Trim(), CablingParameters.JunctionBoxRole, StringComparison.OrdinalIgnoreCase);
        _roles[type.Value] = known;

        return known;
    }

    private bool JoinsACarrier(Element element)
    {
        var manager = element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null,
        };

        if (manager is null)
            return false;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector is null || !connector.IsConnected)
                continue;

            foreach (Connector other in connector.AllRefs)
            {
                var owner = other?.Owner;

                if (owner is null || owner.Id == element.Id || owner.Category is null)
                    continue;

                if (_catalogue.ClassOf((BuiltInCategory)owner.Category.Id.Value).Length > 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Where the box stands: the middle of the element the network already described.</summary>
    /// <remarks>
    /// Taken from the node rather than measured again, and that is not only economy: the node's two
    /// points are its outermost connector origins, already carried through the link's transform into
    /// the host's coordinates, so the middle of them is the middle of the fitting in the one
    /// coordinate system every tap is expressed in. Measuring it here would be a second definition of
    /// the same place, and the two would disagree inside a link.
    /// </remarks>
    public static ExistingBox Where(CarrierNode node) =>
        new(node.Id, new Point3(
            (node.Start.X + node.End.X) / 2,
            (node.Start.Y + node.End.Y) / 2,
            (node.Start.Z + node.End.Z) / 2));
}
