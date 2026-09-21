using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>The cable of a circuit as a tree: what a terminal holds, and where the cable is cut.</summary>
public sealed partial class CablingApplyTests
{
    /// <summary>
    /// A device type told how many conductors its terminal block holds reads back saying so, and a
    /// type not told reads as saying nothing rather than as saying none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction is the case.</b> Revit reads an unset integer parameter as zero, so a reader
    /// written by arithmetic would report every unfilled type as a terminal holding no conductors -
    /// and a terminal that holds none may never be cut, which turns every tree in the model back into
    /// a chain. Nothing about that failure is visible in a route: the answer stays plausible.
    /// </para>
    /// <para>
    /// <b>Half the types, never all of them</b> - the same rule as the splicing case, for the same
    /// reason. A reader that ignored the parameter and returned a constant would pass a case where
    /// every type carries a value.
    /// </para>
    /// <para>
    /// <b>The categories are the model's own.</b> A circuit takes whatever a manufacturer's family
    /// calls itself, so the case binds the capacity to the categories the read found its devices in -
    /// which is what the apply does, by the same route, and is the only honest list either of us has.
    /// </para>
    /// </remarks>
    private static void ADeviceTypeThatStatesItsTerminalIsReadBackAsStatingIt(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        // Read first, because what the capacity has to be bound to is which categories this model
        // draws its devices in, and only the model knows that.
        var before = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        Note(context, "capacity: device categories the read found", before.Circuits.DeviceCategories.Count);

        Skip.When(
            before.Circuits.DeviceCategories.Count == 0,
            "the model this sweep opened describes no circuit with a device, so there is no type to state a terminal capacity on");

        Bind(watch, document, application, RuntimeFor(symbol, catalogue, before.Circuits.DeviceCategories));

        // Every device type the described circuits use, in a stable order: the case states a capacity
        // on the first half, and a choice that moved between runs would make its own notes unreadable.
        var types = before.Circuits.Described
            .SelectMany(circuit => circuit.Devices)
            .Select(device => document.GetElement(new ElementId(device.Owner.Value))?.GetTypeId().Value ?? 0)
            .Where(one => one != 0)
            .Distinct()
            .OrderBy(one => one)
            .ToList();

        Note(context, "capacity: device types in the described circuits", types.Count);

        Skip.When(
            types.Count < 2,
            "the described circuits of this model use fewer than two device types, so a type that states a capacity cannot be told from one that does not");

        const int Stated = 3;
        var told = types.Take(types.Count / 2).ToHashSet();
        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: some device types state their terminal capacity"))
        {
            transaction.Start();

            foreach (var type in told)
                SetInteger(document.GetElement(new ElementId(type)), CablingParameters.TerminalCapacity, Stated, "terminal capacity");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "stating a terminal capacity on some device types");

        var after = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var stated = 0;
        var silent = 0;

        foreach (var circuit in after.Circuits.Described)
        {
            foreach (var device in circuit.Devices)
            {
                var type = document.GetElement(new ElementId(device.Owner.Value))?.GetTypeId().Value ?? 0;

                if (type == 0)
                    continue;

                if (told.Contains(type))
                {
                    stated++;

                    Expect.Same(
                        Stated,
                        device.Capacity,
                        "device " + device.Owner.Value + ", whose type " + type + " states a terminal capacity");
                }
                else
                {
                    silent++;

                    // Zero, and it means the type said nothing - which is what sends the search to the
                    // project's default. A number here would be a type answering a question nobody asked it.
                    Expect.Same(
                        0,
                        device.Capacity,
                        "device " + device.Owner.Value + ", whose type " + type + " states no terminal capacity");
                }
            }
        }

        Note(context, "capacity: devices whose type states one", stated);
        Note(context, "capacity: devices whose type states none", silent);

        Skip.When(
            stated == 0 || silent == 0,
            "of the devices this model describes, every one is of a type the case told or every one is of a type it did not, so one half of the question cannot be asked");
    });

    /// <summary>
    /// What the search lays for a circuit is a tree: each device is served once, and every place the
    /// cable branches stands on a carrier that circuit's cable actually walks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariants, never numbers.</b> How many times a particular circuit of the owner's model
    /// branches is a fact about this week's model, not about the search; that the cable never branches
    /// somewhere it does not go is a fact about the search, and it is the one that would go wrong
    /// silently. A branch on a carrier outside the route is a cut made in a cable that is not there -
    /// and it reaches the apply as an indicator standing in the wrong room.
    /// </para>
    /// <para>
    /// <b>Served once is the other half.</b> The chain reached every device by construction, one after
    /// another; a tree reaches them through branches, and a device served twice or not at all is the
    /// failure that shape allows and the chain did not. A run that lost a device still routes, still
    /// reports a length and still looks finished - the same danger the reader counts separately.
    /// </para>
    /// <para>
    /// The connection mode is left as the model states it. Branching at a terminal is legal only where
    /// the circuit is cut at terminals, and forcing a mode here would test the case's own setting
    /// rather than the model's.
    /// </para>
    /// </remarks>
    private static void WhatTheSearchLaysIsATree(RevitTestContext context)
    {
        var document = context.Document!;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var found = snapshot.Circuits.Described
            .Select(circuit => Router.Route(snapshot.Network, circuit, Options))
            .Where(one => one.Status == RouteStatus.Found)
            .ToList();

        Note(context, "tree: routes found", found.Count);
        Note(context, "tree: routes that branch at all", found.Count(one => one.Branches.Count > 0));
        Note(context, "tree: branch points over all routes", found.Sum(one => one.Branches.Count));

        Skip.When(
            found.Count == 0,
            "no circuit of the model this sweep opened routed, so there is no cable whose shape could be a tree");

        foreach (var route in found)
        {
            var circuit = snapshot.Circuits.Described.Single(one => one.Id == route.Circuit);
            var walked = route.Path.ToHashSet();

            Expect.Same(
                circuit.Devices.Count,
                route.Taps.Count,
                "circuit " + route.Circuit.Value + ": the devices it holds against the places its cable leaves the structure for one");

            var served = route.Taps.Select(tap => tap.Device.Owner).ToList();

            Expect.Same(
                served.Count,
                served.Distinct().Count(),
                "circuit " + route.Circuit.Value + ": devices served once against devices served at all - a device served twice is a tree that folded back on itself");

            foreach (var branch in route.Branches)
            {
                Expect.That(
                    walked.Contains(branch.Carrier),
                    "circuit " + route.Circuit.Value + ": it branches on carrier " + branch.Carrier
                    + ", which its own cable never walks");

                // Two cables leaving is the least a branch can be: one arrives, and the place is of
                // no interest unless at least two go on from it. One leaving is an ordinary
                // pass-through, and stamping that as a branch would put an indicator where the cable
                // is not cut at all.
                Expect.That(
                    branch.Ways >= 2,
                    "circuit " + route.Circuit.Value + ": it calls a place on carrier " + branch.Carrier
                    + " a branch while only " + branch.Ways + " cable(s) leave it");
            }
        }
    }
}
