using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>A carrier whose type says cable may be spliced in it, through the reader and the plan.</summary>
public sealed partial class CablingApplyTests
{
    /// <summary>
    /// A fitting type told that cable may be spliced in it reads back that way through the production
    /// reader, and a type not told does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism, asserted where it does not depend on the model's geometry.</b> Whether any
    /// device of this model happens to hang under a fitting is the model's business, and the next case
    /// stands down when none does - but the parameter reaching the network is not: it holds for every
    /// carrier of a marked type, and fails loudly if the binding, the read or the catalogue drops it.
    /// </para>
    /// <para>
    /// <b>Half the types, never all of them.</b> Marking everything would make "and an unmarked type
    /// reads as no" unaskable, and that half is the one that catches a reader answering yes to
    /// everything - which a reader that ignored the parameter and returned a constant would do.
    /// </para>
    /// <para>
    /// The join is not asked here, unlike the junction-box cases: a splice is permitted by what the
    /// element is, not by what it is connected to. A fitting standing beside the run still says what
    /// its type says; whether a cable ever leaves the structure there is the router's question.
    /// </para>
    /// </remarks>
    private static void ASpliceableTypeIsReadBackAsOne(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        // Every fitting type of the host, the indicator's excluded, in a stable order: the case marks
        // the first half, and a marking that moved between runs would make its own notes unreadable.
        var types = HostFittings(document, symbol)
            .Select(one => one.GetTypeId().Value)
            .Distinct()
            .OrderBy(one => one)
            .ToList();

        Note(context, "splicing: fitting types in the host", types.Count);

        Skip.When(
            types.Count < 2,
            "the model this sweep opened has fewer than two fitting types outside the indicator's family, so a marked type cannot be told from an unmarked one");

        var marked = types.Take(types.Count / 2).ToHashSet();

        Note(context, "splicing: fitting types told cable may be spliced in them", marked.Count);

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: some fitting types allow splicing"))
        {
            transaction.Start();

            foreach (var type in marked)
                SetInteger(document.GetElement(new ElementId(type)), CablingParameters.Splicing, 1, "splicing");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "telling some fitting types that cable may be spliced in them");

        // Read through the production path, host only: a link's types are the link's, and this case
        // wrote into the host.
        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var byType = HostFittings(document, symbol).ToDictionary(one => one.Id.Value, one => one.GetTypeId().Value);
        var allowed = 0;
        var refused = 0;

        foreach (var pair in byType)
        {
            var node = snapshot.Network.Node(new CarrierId(0, pair.Key));

            // A fitting the read did not collect is not this case's business: it may be a category the
            // catalogue does not gather, or an element whose extent could not be read.
            if (node is null)
                continue;

            if (marked.Contains(pair.Value))
            {
                allowed++;

                Expect.That(
                    node.AllowsSplicing,
                    "carrier " + pair.Key + ", whose type " + pair.Value + " says cable may be spliced in it, read back as one where it may not");
            }
            else
            {
                refused++;

                Expect.That(
                    !node.AllowsSplicing,
                    "carrier " + pair.Key + ", whose type " + pair.Value + " says nothing about splicing, read back as one where cable may be spliced");
            }
        }

        Note(context, "splicing: carriers read back as allowing a splice", allowed);
        Note(context, "splicing: carriers read back as not allowing one", refused);

        Skip.When(
            allowed == 0 || refused == 0,
            "of the fittings this model has, the read collected only the marked ones or only the unmarked ones, so one half of the question cannot be asked");
    });

    /// <summary>
    /// Where a tap lands on a carrier cable may be spliced in, the plan branches there and recommends no
    /// box; the same circuits with the permission withdrawn ask for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rule of 2026-09-20, asserted against the same model twice.</b> Once with every
    /// fitting type told that cable may be spliced in it, once with none told - the difference between
    /// the two plans is the rule, and reading one plan alone would leave "no box here" indistinguishable
    /// from "no box was ever wanted here".
    /// </para>
    /// <para>
    /// <b>The unmarked plan is taken first and from an unmarked model</b>, not by asking the marked plan
    /// what it would have done: the two runs go through the whole production path, read included, so a
    /// parameter that never reached the network would show as two identical plans rather than as a
    /// passing comparison.
    /// </para>
    /// <para>
    /// Nothing is applied. A splice places nothing and writes nothing, so the assertion is about the plan;
    /// what the apply does with a plan has its own cases.
    /// </para>
    /// </remarks>
    private static void ATapOnASpliceableCarrierAsksForNoBox(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        Bind(watch, document, application, RuntimeFor(symbol, catalogue));
        CutEveryCircuitInBoxes(watch, document, application);

        var before = PlanFound(document, project, catalogue);

        Note(context, "splicing plan: routes found", before.Run.Found);
        Note(context, "splicing plan: boxes before anything is marked", before.Run.Boxes.Count);
        Note(context, "splicing plan: splices before anything is marked", before.Run.Splices.Count);

        Expect.Same(
            0,
            before.Run.Splices.Count,
            "splices planned in a model where no type says cable may be spliced anywhere");

        Skip.When(before.Run.Found == 0, "no circuit of this model routed, so no tap can land on anything");

        // The taps of the found routes that sit on a fitting of the host - the only ones the shipped
        // binding can be told about, since a straight tray is no place for a splice.
        var fittings = HostFittings(document, symbol).Select(one => one.Id.Value).ToHashSet();

        var onFittings = before.Run.Boxes
            .SelectMany(box => box.Taps)
            .Where(tap => !tap.Carrier.IsLinked && fittings.Contains(tap.Carrier.Value))
            .ToList();

        Note(context, "splicing plan: taps of found routes landing on a host fitting", onFittings.Count);

        Skip.When(
            onFittings.Count == 0,
            "no tap of the routes this model found leaves the structure at a fitting of the host, so no carrier this binding can reach is one a splice would be made in");

        var types = onFittings.Select(tap => document.GetElement(new ElementId(tap.Carrier.Value)).GetTypeId().Value)
            .Distinct()
            .ToList();

        Note(context, "splicing plan: types of the fittings those taps land on", types.Count);

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: the fittings taps land on allow splicing"))
        {
            transaction.Start();

            foreach (var type in types)
                SetInteger(document.GetElement(new ElementId(type)), CablingParameters.Splicing, 1, "splicing");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "telling the fittings the taps land on that cable may be spliced in them");

        var after = PlanFound(document, project, catalogue);

        Note(context, "splicing plan: boxes after marking", after.Run.Boxes.Count);
        Note(context, "splicing plan: splices after marking", after.Run.Splices.Count);

        Expect.That(
            after.Run.Splices.Count > 0,
            "the plan splices nowhere although " + onFittings.Count
            + " tap(s) of the found routes land on fittings whose types were told cable may be spliced in them");

        // Every splice sits on a carrier the read says allows one. Asked of the network rather than of
        // the marked set, so that a planner splicing on the wrong carrier is caught rather than assumed.
        foreach (var splice in after.Run.Splices)
        {
            Expect.That(
                after.Snapshot.Network.Node(splice.Carrier)?.AllowsSplicing == true,
                "the plan splices circuit " + splice.Circuit + " in carrier " + splice.Carrier
                + ", which the read does not say cable may be spliced in");
        }

        // Nothing is placed where the cable branches in the carrier: no box of the plan stands at a
        // splice. The box radius is the one distance that decides nearness, here as everywhere.
        foreach (var splice in after.Run.Splices)
        {
            var near = after.Run.Boxes.Count(box => box.At.DistanceTo(splice.At) <= project.BoxRadius);

            Expect.Same(
                0,
                near,
                "boxes the plan puts within the box radius of the splice it makes in carrier " + splice.Carrier);
        }

        // Every device is still served, by a box or by a splice: the permission moves work, it does not
        // lose it. Compared against the taps of the found routes, counted by this suite.
        var taps = after.Results.Where(one => one.Status == RouteStatus.Found).Sum(one => one.Taps.Count);

        Expect.Same(
            taps,
            after.Run.Plan.Served,
            "taps of the found routes, against the devices the plan reports serving by a box or by a splice");

        Expect.That(
            after.Run.Boxes.Count < before.Run.Boxes.Count
            || after.Run.Boxes.Sum(box => box.Spurs) < before.Run.Boxes.Sum(box => box.Spurs),
            "the same circuits ask for as many boxes serving as many devices after the permission as before: "
            + before.Run.Boxes.Count + " box(es) serving " + before.Run.Boxes.Sum(box => box.Spurs)
            + ", then " + after.Run.Boxes.Count + " serving " + after.Run.Boxes.Sum(box => box.Spurs));
    });
}
