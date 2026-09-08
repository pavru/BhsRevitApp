using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Where a connector is, when it is anywhere at all.
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
/// </remarks>
internal static class Connectors
{
    /// <summary>The connector's point, or null when it is not the sort of connector that has one.</summary>
    public static XYZ? OriginOrNull(this Connector connector) =>
        connector is not null && (connector.ConnectorType & ConnectorType.Physical) != 0
            ? connector.Origin
            : null;

    /// <summary>Whether this connector is on the electrical side of a family.</summary>
    public static bool IsElectrical(this Connector connector) =>
        connector is not null && connector.Domain == Domain.DomainElectrical;
}
