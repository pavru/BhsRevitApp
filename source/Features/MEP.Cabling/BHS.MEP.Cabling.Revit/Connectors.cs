using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Where a connector is, when it is anywhere at all, and whether it is the sort that can be joined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not every connector has a point, and asking one that does not is an exception rather than a
/// null.</b> Measured on a real electrical model, at the first press of the first command:
/// </para>
/// <code>
/// Origin is available only for connectors of PhysicalConn type.
/// </code>
/// <para>
/// <c>ConnectorType</c> is a flag set - <c>End = 1</c>, <c>Curve = 2</c>, <c>Logical = 4</c>,
/// <c>Surface = 16</c>, and <c>Physical = 19</c>, which is <c>End | Curve | Surface</c>. A logical
/// connector is how Revit models a connection that has no place: a panel feeding a circuit is
/// connected to it in the electrical system without any point on the panel being the connection.
/// Identical on 2024, 2026 and 2027 - checked, because a flag value is exactly the kind of thing
/// that could differ and would fail silently by matching the wrong connector.
/// </para>
/// <para>
/// The type is tested rather than the exception caught. Catching would work and would also swallow
/// the day Revit throws for a different reason, which is how a hole in the network gets blamed on
/// somebody's model.
/// </para>
/// <para>
/// <b><c>IsConnected</c> refuses on the same connectors <c>Origin</c> refuses on - measured, and it
/// is why the type is asked before that too.</b> The connector census run on Revit 2026 against the
/// linked set found 75 non-physical connectors out of 212, and every one of them answered
/// <c>InvalidOperationException</c> to <c>Origin</c>, <c>CoordinateSystem</c>, <c>MEPSystem</c>
/// <b>and</b> <c>IsConnected</c> alike: 60 <c>Logical</c> (type 4, on circuits, on panels and on duct
/// systems) and 15 <c>MainSurface</c> (type 32, domain <c>DomainCableTrayConduit</c>, on panels).
/// <c>AllRefs</c> and <c>IsConnectedTo</c> do answer on a logical connector, which is what makes the
/// refusal easy to miss: the connector is not broken, it simply has no joint to report, because
/// membership of an electrical circuit is not a joint in a cable run.
/// </para>
/// <para>
/// <b>The owner's decision, 2026-09-16: only a physical connector counts as joined.</b> The two
/// places that ask - whether an indicator of ours has been drawn into the wiring, and whether a
/// fitting is a real junction box - both mean a joint somebody made between elements, and a logical
/// connector never describes one. Before the decision both asked <c>IsConnected</c> of every
/// connector without looking at the type, so a box or indicator family carrying a logical or surface
/// connector - an electrical-equipment family, say - would have thrown out of reading or applying.
/// Not on this model, where the indicator family has four physical connectors and nothing else: a
/// defect waiting for a family, not one anybody had hit.
/// </para>
/// </remarks>
internal static class Connectors
{
    /// <summary>Whether this connector is one of the sort that has a place and can be joined.</summary>
    public static bool IsPhysical(this Connector connector) =>
        connector is not null && (connector.ConnectorType & ConnectorType.Physical) != 0;

    /// <summary>
    /// Whether this connector is physical and joined to something - false for the sorts of connector
    /// that refuse the question rather than answering it.
    /// </summary>
    public static bool IsJoined(this Connector connector) =>
        connector.IsPhysical() && connector.IsConnected;

    /// <summary>The connector's point, or null when it is not the sort of connector that has one.</summary>
    public static XYZ? OriginOrNull(this Connector connector) =>
        connector.IsPhysical() ? connector.Origin : null;

    /// <summary>Whether this connector is on the electrical side of a family.</summary>
    public static bool IsElectrical(this Connector connector) =>
        connector is not null && connector.Domain == Domain.DomainElectrical;
}
