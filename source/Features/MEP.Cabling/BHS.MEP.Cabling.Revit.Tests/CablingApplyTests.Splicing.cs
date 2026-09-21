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
    /// <b>The mechanism, asserted on the categories the parameter actually ships bound to.</b> The next
    /// case is about what the planner does with the answer and has to declare a category of its own to
    /// ask; this one asks nothing of the geometry - a marked type reads back marked for every carrier of
    /// it, and the assertion fails loudly if the binding, the read or the catalogue drops the parameter.
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
    /// box; the same circuits without the permission ask for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rule of 2026-09-20, asserted against the same model twice.</b> Once with the
    /// carriers the taps land on told that cable may be spliced in them, once with none told - the
    /// difference between the two plans is the rule, and reading one plan alone would leave "no box
    /// here" indistinguishable from "no box was ever wanted here".
    /// </para>
    /// <para>
    /// <b>The case declares the carrier categories itself, at run time, and that is the point rather
    /// than a convenience.</b> The shipped binding puts the parameter on fittings only, because a
    /// straight tray is no place for a splice - and a tap almost never lands on a fitting: a tap sits
    /// where the cable leaves the structure, which on an open run is the point nearest the device, and
    /// any tray touching a tee is at least as near as the tee itself. Measured: on the sweep's linked
    /// set, none of the ten taps of six found routes leaves at a fitting. A case that waited for one
    /// would be a case that never asserts.
    /// </para>
    /// <para>
    /// Declaring them is exactly the mechanism the owner described for the finished catalogue - the
    /// user picks the categories cable may be spliced in - and it goes through the production binding
    /// path, <c>RuntimeCategories</c>, the same one the indicator's own category travels by. What this
    /// case does not claim is that a project should say this of its trays; what it asserts is that when
    /// something says it, the planner branches there instead of asking for a box.
    /// </para>
    /// <para>
    /// Nothing is applied. A splice places nothing and writes nothing, so the assertion is about the
    /// plan; what the apply does with a plan has its own cases.
    /// </para>
    /// </remarks>
    private static void ATapOnASpliceableCarrierAsksForNoBox(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        var runtime = RuntimeFor(symbol, catalogue);

        foreach (var category in catalogue.Categories)
            runtime.Add(CablingParameters.Splicing, category);

        Bind(watch, document, application, runtime);
        CutEveryCircuitInBoxes(watch, document, application);

        var before = PlanFound(document, project, catalogue);

        Note(context, "splicing plan: routes found", before.Run.Found);
        Note(context, "splicing plan: boxes before anything is marked", before.Run.Boxes.Count);
        Note(context, "splicing plan: splices before anything is marked", before.Run.Splices.Count);

        Expect.Same(
            0,
            before.Run.Splices.Count,
            "splices planned in a model where no type says cable may be spliced anywhere");

        // The carriers of the host the taps of the found routes leave at. A link's types belong to the
        // link, and this case writes into the host.
        var carriers = before.Run.Boxes
            .SelectMany(box => box.Taps)
            .Select(tap => tap.Carrier)
            .Where(one => !one.IsLinked)
            .Select(one => one.Value)
            .Distinct()
            .ToList();

        Note(context, "splicing plan: taps of found routes", before.Run.Boxes.Sum(box => box.Spurs));
        Note(context, "splicing plan: carriers of the host those taps leave at", carriers.Count);

        Skip.When(
            carriers.Count == 0,
            "no tap of the routes this model found leaves the structure at a carrier of the host, so there is nothing this case can tell a splice may be made in");

        var types = carriers
            .Select(one => document.GetElement(new ElementId(one))?.GetTypeId().Value ?? 0)
            .Where(one => one != 0)
            .Distinct()
            .ToList();

        Note(context, "splicing plan: types of those carriers", types.Count);

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: the carriers the taps leave at allow splicing"))
        {
            transaction.Start();

            foreach (var type in types)
                SetInteger(document.GetElement(new ElementId(type)), CablingParameters.Splicing, 1, "splicing");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "telling the carriers the taps leave at that cable may be spliced in them");

        var after = PlanFound(document, project, catalogue);

        Note(context, "splicing plan: boxes after marking", after.Run.Boxes.Count);
        Note(context, "splicing plan: splices after marking", after.Run.Splices.Count);

        Expect.That(
            after.Run.Splices.Count > 0,
            "the plan splices nowhere although the " + types.Count
            + " carrier type(s) its taps leave at were told cable may be spliced in them");

        // Every splice sits on a carrier the read says allows one. Asked of the network rather than of
        // the marked set, so that a planner splicing on the wrong carrier is caught rather than assumed.
        foreach (var splice in after.Run.Splices)
        {
            Expect.That(
                after.Snapshot.Network.Node(splice.Carrier)?.AllowsSplicing == true,
                "the plan splices circuit " + splice.Circuit + " in carrier " + splice.Carrier
                + ", which the read does not say cable may be spliced in");
        }

        // Nothing stands where the cable branches in the carrier. The box radius is the one distance
        // that decides nearness, here as everywhere.
        foreach (var splice in after.Run.Splices)
        {
            var near = after.Run.Boxes.Count(box => box.At.DistanceTo(splice.At) <= project.BoxRadius);

            Expect.Same(
                0,
                near,
                "boxes the plan puts within the box radius of the splice it makes in carrier " + splice.Carrier);
        }

        // Every device is still served, by a box or by a splice: the permission moves work, it does not
        // lose it. Counted against the taps of the found routes, by this suite rather than by the plan.
        var taps = after.Results.Where(one => one.Status == RouteStatus.Found).Sum(one => one.Taps.Count);

        Expect.Same(
            taps,
            after.Run.Served,
            "taps of the found routes, against the devices the plan reports serving by a box or by a splice");

        Expect.That(
            after.Run.Boxes.Count < before.Run.Boxes.Count
            || after.Run.Boxes.Sum(box => box.Spurs) < before.Run.Boxes.Sum(box => box.Spurs),
            "the same circuits ask for as many boxes serving as many devices after the permission as before: "
            + before.Run.Boxes.Count + " box(es) serving " + before.Run.Boxes.Sum(box => box.Spurs)
            + ", then " + after.Run.Boxes.Count + " serving " + after.Run.Boxes.Sum(box => box.Spurs));
    });
}
