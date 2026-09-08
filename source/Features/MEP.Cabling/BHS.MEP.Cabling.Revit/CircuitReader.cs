using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>What one pass over the model's circuits produced.</summary>
/// <remarks>
/// The count of what was left out travels with what was taken, because a screen that reports on
/// three hundred circuits out of three hundred and fifty without saying so is worse than one that
/// reports on none. This is the same argument as <see cref="RouteStatus"/>, one step earlier: a
/// circuit that could not even be described has a reason, and the reason is worth a line.
/// </remarks>
public sealed class CircuitHarvest
{
    public CircuitHarvest(
        IReadOnlyList<CircuitSnapshot> circuits,
        int withoutPanel,
        int withoutDevices,
        int devicesSkipped)
    {
        Circuits = circuits;
        WithoutPanel = withoutPanel;
        WithoutDevices = withoutDevices;
        DevicesSkipped = devicesSkipped;
    }

    public IReadOnlyList<CircuitSnapshot> Circuits { get; }

    /// <summary>Circuits with no panel to start from, which cannot be described at all.</summary>
    public int WithoutPanel { get; }

    /// <summary>Circuits whose devices offered no point to reach.</summary>
    public int WithoutDevices { get; }

    /// <summary>Devices dropped from circuits that were otherwise described.</summary>
    /// <remarks>
    /// <b>The most dangerous of the three, and the reason it is counted separately.</b> A circuit
    /// that loses one of its five devices still routes, still reports a length, and still looks
    /// finished - it is simply short by however far that device was. The other two produce a circuit
    /// that is visibly missing; this one produces an answer that is quietly wrong.
    /// </remarks>
    public int DevicesSkipped { get; }
}

/// <summary>
/// Turns the electrical circuits of a document into snapshots the search can hold.
/// </summary>
/// <remarks>
/// Circuits are read separately from carriers and are not part of the network, because the two have
/// different lifetimes: the structure changes when somebody moves a tray, the circuits when
/// somebody rewires. An updater will invalidate them apart, and merging them now would make that a
/// rewrite later.
/// </remarks>
public sealed class CircuitReader
{
    /// <summary>Reads every circuit of the host document.</summary>
    /// <remarks>
    /// The host only: a circuit lives in the file that owns its panel, and the surveyed project had
    /// all 62 of them in the host while its carriers were spread across the links. Reading circuits
    /// out of a link would also be pointless in the other direction - nothing can be written back
    /// to one.
    /// </remarks>
    public CircuitHarvest Read(Document host)
    {
        var circuits = new List<CircuitSnapshot>();
        var withoutPanel = 0;
        var withoutDevices = 0;
        var devicesSkipped = 0;

        var found = new FilteredElementCollector(host)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>();

        foreach (var system in found)
        {
            var source = SourceTerminal(system);

            if (source is null)
            {
                withoutPanel++;
                continue;
            }

            var devices = Devices(system, ref devicesSkipped);

            if (devices.Count == 0)
            {
                withoutDevices++;
                continue;
            }

            circuits.Add(new CircuitSnapshot(new CarrierId(system.Id.Value), Number(system), source, devices)
            {
                BuiltInLength = system.Length,
                HasCustomPath = system.HasCustomCircuitPath,

                // OurRouteId stays empty until the parameter scheme exists. It is what tells our own
                // custom path from somebody else's, and reading it before we can write it would be a
                // field that is always empty pretending to be an answer.
            });
        }

        return new CircuitHarvest(circuits, withoutPanel, withoutDevices, devicesSkipped);
    }

    /// <summary>The panel end, taken from the connector the circuit is actually fed from.</summary>
    /// <remarks>
    /// <c>BaseEquipmentConnector</c> rather than <c>BaseEquipment</c>, and the difference is metres.
    /// A panel's location is its insertion point; the terminal a cable lands on is somewhere on its
    /// face. The predecessor measured to the element and carried a tolerance to absorb the error,
    /// which is why its routes looked wrong on a plan while its numbers looked plausible.
    /// </remarks>
    private static Terminal? SourceTerminal(ElectricalSystem system)
    {
        var panel = system.BaseEquipment;

        if (panel is null)
            return null;

        var at = system.BaseEquipmentConnector?.Origin
                 ?? (panel.Location as LocationPoint)?.Point;

        return at is null
            ? null
            : new Terminal(new CarrierId(panel.Id.Value), new Point3(at.X, at.Y, at.Z), panel.Name);
    }

    /// <summary>
    /// The devices, each at the connector this circuit lands on.
    /// </summary>
    /// <remarks>
    /// <b>The order is Revit's and is taken as given, not as meaningful.</b> <c>Elements</c> is an
    /// <c>ElementSet</c>, and nothing documents it as the order the circuit visits. The search does
    /// not depend on it today - it reaches every device from the structure - so this is recorded as
    /// a known unknown rather than relied upon; the day a mode routes device to device in sequence,
    /// the order has to be measured first.
    /// </remarks>
    private static IReadOnlyList<Terminal> Devices(ElectricalSystem system, ref int skipped)
    {
        var terminals = new List<Terminal>();

        foreach (Element element in system.Elements)
        {
            var at = DeviceOrigin(element, system);

            if (at is null)
            {
                skipped++;
                continue;
            }

            terminals.Add(new Terminal(new CarrierId(element.Id.Value), new Point3(at.X, at.Y, at.Z), element.Name));
        }

        return terminals;
    }

    private static XYZ? DeviceOrigin(Element element, ElectricalSystem system)
    {
        var manager = (element as FamilyInstance)?.MEPModel?.ConnectorManager;

        if (manager is not null)
        {
            XYZ? electrical = null;

            foreach (Connector connector in manager.Connectors)
            {
                if (connector.Domain != Domain.DomainElectrical)
                    continue;

                // The connector this circuit is on, when it can be told: a device with two
                // electrical connectors - a light with a switch leg, say - has two answers, and
                // picking the first would put the route on whichever one Revit happened to list.
                if (connector.MEPSystem?.Id == system.Id)
                    return connector.Origin;

                electrical ??= connector.Origin;
            }

            if (electrical is not null)
                return electrical;
        }

        return (element.Location as LocationPoint)?.Point;
    }

    private static string Number(ElectricalSystem system)
    {
        var panel = system.PanelName;
        var number = system.CircuitNumber;

        return panel.Length == 0 ? number : panel + ", " + number;
    }
}
