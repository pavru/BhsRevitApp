using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>What a junction box holds: read back from a type, counted, reported, and nothing else.</summary>
public sealed partial class CablingApplyTests
{
    /// <summary>How many conductors the case states on the box types it tells - small, so that it bites.</summary>
    private const int StatedBoxCapacity = 1;

    /// <summary>
    /// A box type told how many conductors it holds reads back saying so, and one not told reads as
    /// saying nothing rather than as holding none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same distinction as a terminal's capacity, and the same reason it is a case.</b> Revit
    /// reads an unset integer parameter as zero, so a reader written by arithmetic alone would report
    /// every unfilled box type as holding no conductors at all - and then every box in every model
    /// that never took this up would be over its capacity. A report that warns about the ordinary
    /// stops being read, which is the failure this repository has named before.
    /// </para>
    /// <para>
    /// <b>Half the types, never all of them.</b> A reader that ignored the parameter and returned a
    /// constant would pass a case where every type carries a value.
    /// </para>
    /// </remarks>
    private static void ABoxTypeThatStatesItsCapacityIsReadBackAsStatingIt(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        Bind(watch, document, application, RuntimeFor(symbol, catalogue));
        MarkJoinedFittingTypesBoxes(context, watch, document, symbol, catalogue, "box capacity");

        var before = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        Note(context, "box capacity: boxes the read found", before.Boxes.Count);

        Skip.When(
            before.Boxes.Count == 0,
            "the model this sweep opened has no junction box joined to a carrier, so there is no type to state a capacity on");

        // The types of the boxes the read actually found, in a stable order: a choice that moved
        // between runs would make this case's own notes unreadable.
        var types = before.Boxes
            .Select(box => document.GetElement(new ElementId(box.Id.Value))?.GetTypeId().Value ?? 0)
            .Where(one => one != 0)
            .Distinct()
            .OrderBy(one => one)
            .ToList();

        Note(context, "box capacity: types among the boxes found", types.Count);

        Skip.When(
            types.Count < 2,
            "the boxes of the model this sweep opened are all of one type, so a type that states a capacity cannot be told from one that does not");

        var told = types.Take(types.Count / 2).ToHashSet();
        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: some box types state their capacity"))
        {
            transaction.Start();

            foreach (var type in told)
                SetInteger(document.GetElement(new ElementId(type)), CablingParameters.BoxCapacity, StatedBoxCapacity, "box capacity");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "stating a capacity on some box types");

        var after = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var stated = 0;
        var silent = 0;

        foreach (var box in after.Boxes)
        {
            var type = document.GetElement(new ElementId(box.Id.Value))?.GetTypeId().Value ?? 0;

            if (type == 0)
                continue;

            if (told.Contains(type))
            {
                stated++;

                Expect.Same(
                    StatedBoxCapacity,
                    box.Capacity,
                    "box " + box.Id.Value + ", whose type " + type + " states a capacity");
            }
            else
            {
                silent++;

                // Zero, and it means the type said nothing - which is what sends the answer to the
                // project, and with the project silent too, to no limit at all.
                Expect.Same(
                    0,
                    box.Capacity,
                    "box " + box.Id.Value + ", whose type " + type + " states no capacity");
            }
        }

        Note(context, "box capacity: boxes whose type states one", stated);
        Note(context, "box capacity: boxes whose type states none", silent);

        Skip.When(
            stated == 0 || silent == 0,
            "of the boxes this model holds, every one is of a type the case told or every one is of a type it did not, so one half of the question cannot be asked");
    });

    /// <summary>
    /// The conductors a circuit of this model reports, as the suite reads them off Revit itself.
    /// </summary>
    /// <remarks>
    /// <b>The suite's own arithmetic, not the reader's</b>, so that a note about the model and the
    /// code that consumes it do not agree by construction. The release split is the same one the
    /// reader carries and for the same measured reason: <c>OtherConductorsNumber</c> is declared only
    /// in the 2026 and 2027 assemblies.
    /// </remarks>
    private static int ConductorsOf(ElectricalSystem system)
    {
        var conductors =
            system.HotConductorsNumber + system.NeutralConductorsNumber + system.GroundConductorsNumber;

#if REVIT2026_OR_GREATER
        conductors += system.OtherConductorsNumber;
#endif

        return conductors <= 0 ? 0 : conductors * (system.RunsNumber > 0 ? system.RunsNumber : 1);
    }

    /// <summary>
    /// Gives every circuit a wire type when none of them reports a conductor, and says what changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case builds its own question rather than asking for the model to be edited</b> - this
    /// suite's standing rule, and here it is possible because of what the metadata says. The conductor
    /// counts are derived and carry no setter on any of the four releases: neither this suite nor a
    /// designer can write them. What does carry a setter on all four is
    /// <c>ElectricalSystem.WireType</c>; on 2026 and 2027 there is a second, <c>CableType</c>, which
    /// is not touched here because one mechanism present everywhere is the one worth measuring first.
    /// </para>
    /// <para>
    /// <b>That assigning a wire type makes Revit report conductors is a hypothesis, and it is written
    /// as one.</b> Everything here is a note; nothing is asserted. A check written before the answer
    /// agrees with whoever wrote it, and the answer to this one is not known to anybody in this
    /// repository. If it turns out to be false, the case that calls this stands down out loud and the
    /// notes say at which step it stopped - which is what tells the owner what to set, instead of
    /// leaving them to edit four models and find out.
    /// </para>
    /// <para>
    /// <b>Nothing is named.</b> The record is public: wire type names, panel names and circuit numbers
    /// belong to the owner's building and stay out of it. Counts and ids of our own making do not.
    /// </para>
    /// </remarks>
    private static void ArrangeConductors(RevitTestContext context, PostedWarnings watch, Document document, string label)
    {
        var circuits = new FilteredElementCollector(document)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>()
            .Where(one => one.CircuitType == CircuitType.Circuit)
            .ToList();

        Note(context, label + ": circuits in the host", circuits.Count);
        Note(context, label + ": of them reporting conductors before anything", circuits.Count(ReportsConductors));

        if (circuits.Count == 0 || circuits.Any(ReportsConductors))
            return;

        // What a circuit's cable is called differs by release, and the compiler is what said so rather
        // than the metadata strings: Revit 2026 deprecates ElectricalSystem.WireType in favour of
        // CableType - "CableType will be set on ElectricalSystem properly during document upgrade" -
        // and 2027 removes the property outright. A grep of the assembly still finds the name on 2027,
        // which is exactly the trap this repository has recorded twice before.
#if REVIT2026_OR_GREATER
        var kinds = new FilteredElementCollector(document)
            .OfClass(typeof(CableType))
            .OrderBy(one => one.Id.Value)
            .ToList();

        const string What = "cable type";
#else
        var kinds = new FilteredElementCollector(document)
            .OfClass(typeof(WireType))
            .OrderBy(one => one.Id.Value)
            .ToList();

        const string What = "wire type";
#endif

        Note(context, label + ": " + What + "s in the project", kinds.Count);

        // A project with none cannot be arranged at all, and that is itself the answer to what the
        // owner would have to add.
        if (kinds.Count == 0)
            return;

        var kind = kinds[0];
        var assigned = 0;
        var refused = 0;
        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: every circuit is given a cable"))
        {
            transaction.Start();

            foreach (var circuit in circuits)
            {
                try
                {
#if REVIT2026_OR_GREATER
                    // An id, not the element: the reference says so by what it refuses - "the id is
                    // not a Cable Type id nor invalidElementId".
                    circuit.CableType = kind.Id;
#else
                    circuit.WireType = (WireType)kind;
#endif
                    assigned++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Revit refusing one circuit is an answer, not a reason to lose the rest of the
                    // arrangement. How many it refused is the note that says so.
                    refused++;
                }
            }

            // The conductor counts are derived, and a derived value is what a regeneration is for.
            document.Regenerate();

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "giving every circuit a " + What);

        Note(context, label + ": circuits given a " + What, assigned);
        Note(context, label + ": circuits Revit refused one on", refused);
        Note(context, label + ": of them reporting conductors afterwards", circuits.Count(ReportsConductors));
        Note(context, label + ": conductors of the richest circuit afterwards",
            circuits.Count == 0 ? 0 : circuits.Max(ConductorsOfSafe));
    }

    /// <summary>Whether this circuit reports any conductor at all, asked safely.</summary>
    private static bool ReportsConductors(ElectricalSystem system) => ConductorsOfSafe(system) > 0;

    /// <summary>
    /// What this circuit reports, or zero when Revit refuses the question.
    /// </summary>
    /// <remarks>
    /// Caught rather than allowed through, and only here. This is a note about a model, and a model
    /// that refuses one of these is a model whose count is unknown rather than a sweep that stops; the
    /// production reader asks the same properties of a described circuit, where a refusal would be a
    /// finding. What Revit does with these on a spare or an unsized circuit is not measured.
    /// </remarks>
    private static int ConductorsOfSafe(ElectricalSystem system)
    {
        try
        {
            return ConductorsOf(system);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Stating a capacity changes nothing the calculation gives, and every box over it is reported -
    /// and, where the box is of the host, posted as a warning against that box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's answer of 2026-09-21, asserted where a model can show it.</b> A capacity warns
    /// and moves nothing: no tap changes box, no box is split, no route is diverted and no length
    /// differs. The case computes the plan twice over the same routes - once with capacities stated on
    /// the types and once with none - and requires the two to agree box for box.
    /// </para>
    /// <para>
    /// <b>Written as a comparison of two plans rather than as a list of expected numbers.</b> A list
    /// would be a second statement of what the planner does, and it would go on agreeing with a
    /// planner that had started diverting taps as long as somebody kept the list up to date. That is
    /// the failure this repository paid for when a probe check was rewritten to match a defect.
    /// </para>
    /// <para>
    /// <b>Invariants, never a count of conductors.</b> How many conductors a circuit of this model has
    /// is a fact about this week's model - and worse, about the release: <c>OtherConductorsNumber</c>
    /// exists only on Revit 2026 and later, so the same model gives a larger count on 2026 and 2027
    /// than on 2024 and 2025. Every conductor count here is a note; what is asserted is that the set
    /// reported over capacity is exactly the set that is over capacity, and that the plan did not move.
    /// </para>
    /// <para>
    /// <b>The warning half is asserted only for boxes of the host.</b> A warning raised in the host
    /// cannot address an element of a link, which is the rule every write of this feature follows; the
    /// screen names the rest. The case counts the boxes it expects itself, from the plan, rather than
    /// asking the apply how many it posted.
    /// </para>
    /// </remarks>
    private static void ACapacityChangesNothingAndEveryBoxOverItIsReported(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        new CablingParameters().Export(application);
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));
        MarkJoinedFittingTypesBoxes(context, watch, document, symbol, catalogue, "over capacity");

        // Before the read, because the read is what has to see the conductors. Notes only - whether
        // this achieves anything is the question the sweep answers.
        ArrangeConductors(context, watch, document, "over capacity");

        // Cut in boxes, because a circuit cut at terminals fills no box at all and the question would
        // have nowhere to be asked. The mode is the case's, exactly as the box cases beside it.
        var before = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        // Taken from the read itself, before anything of this case has touched the circuits. The two
        // notes are the difference between "this model states no conductors" and "something between
        // the read and here dropped them" - which is not a distinction worth guessing at, because on
        // 2026-09-21 it was the second, and the skip reason said the first on all four releases.
        Note(context, "over capacity: circuits the read describes",
            before.Circuits.Described.Count);
        Note(context, "over capacity: circuits the read gives conductors for",
            before.Circuits.Described.Count(circuit => circuit.Conductors > 0));
        Note(context, "over capacity: conductors of the richest circuit the read describes",
            before.Circuits.Described.Count == 0 ? 0 : before.Circuits.Described.Max(circuit => circuit.Conductors));

        var circuits = before.Circuits.Described
            .Select(circuit => InMode(circuit, CircuitConnection.AtJunctionBox))
            .ToList();

        var routes = circuits.Select(circuit => Router.Route(before.Network, circuit, Options)).ToList();
        var found = routes.Where(one => one.Status == RouteStatus.Found).ToList();

        Note(context, "over capacity: circuits routed", found.Count);
        Note(context, "over capacity: circuits Revit reports no conductors for",
            found.Count(one => one.Conductors <= 0));

        Skip.When(
            found.Count == 0,
            "no circuit of the model this sweep opened routed cut in boxes, so no cable is spliced in any box");

        var loose = BoxPlanner.Plan(found, before.Boxes, project.BoxRadius);

        Note(context, "over capacity: boxes in the plan", loose.Boxes.Count);
        Note(context, "over capacity: existing boxes the plan uses", loose.Boxes.Count(box => box.Existing is not null));

        Skip.When(
            loose.Boxes.All(box => box.Existing is null),
            "the plan over this model uses no box already in it, so no stated capacity could bear on anything");

        // Every box type of the model, so that every box the plan uses is bounded. Half the types
        // would leave which boxes are bounded to the model's own shape, and this case is about what a
        // capacity does rather than about where it is read from - that is the case above.
        var types = loose.Boxes
            .Where(box => box.Existing is not null)
            .Select(box => document.GetElement(new ElementId(box.Existing!.Id.Value))?.GetTypeId().Value ?? 0)
            .Where(one => one != 0)
            .Distinct()
            .ToList();

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: every box type states a capacity of one"))
        {
            transaction.Start();

            foreach (var type in types)
                SetInteger(document.GetElement(new ElementId(type)), CablingParameters.BoxCapacity, StatedBoxCapacity, "box capacity");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "stating a capacity of one on every box type the plan uses");

        var after = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var again = after.Circuits.Described
            .Select(circuit => InMode(circuit, CircuitConnection.AtJunctionBox))
            .Select(circuit => Router.Route(after.Network, circuit, Options))
            .Where(one => one.Status == RouteStatus.Found)
            .ToList();

        var bounded = BoxPlanner.Plan(again, after.Boxes, project.BoxRadius);

        // The two plans, box for box. Existing boxes are added first and in the order the read found
        // them, and recommendations in the order the circuits ask for them, so the two lists line up
        // when nothing has moved - which is the whole assertion.
        Expect.Same(
            loose.Boxes.Count,
            bounded.Boxes.Count,
            "boxes planned with no capacity stated, against boxes planned with one stated on every type");

        for (var i = 0; i < Math.Min(loose.Boxes.Count, bounded.Boxes.Count); i++)
        {
            var was = loose.Boxes[i];
            var now = bounded.Boxes[i];
            var where = "box " + i + " of the plan (" + (now.Existing is { } one ? one.Id.ToString() : "recommended") + ")";

            Expect.Same(was.Existing?.Id.Value ?? 0, now.Existing?.Id.Value ?? 0, where + ": which box it is");
            Expect.Same(was.Entries, now.Entries, where + ": cable entries");
            Expect.Same(was.Spurs, now.Spurs, where + ": drops served");
            Expect.Same(was.Circuits.Count, now.Circuits.Count, where + ": circuits through it");
        }

        Expect.Same(
            loose.Splices.Count,
            bounded.Splices.Count,
            "splices planned with no capacity stated, against splices planned with one stated on every type");

        // A millimetre, in internal feet: the two sums are computed by the same code over the same
        // structure, so they differ by nothing at all - but a length is a double, and an equality on
        // one is a check that fails for a reason nobody can act on.
        var measuredLoose = found.Sum(one => one.Measured);
        var measuredBounded = again.Sum(one => one.Measured);

        Expect.That(
            Math.Abs(measuredLoose - measuredBounded) < StoredRoutes.Tolerance,
            "what the routes measure with no capacity stated (" + measuredLoose
            + ") against the same with one stated on every type (" + measuredBounded + "), in internal feet");

        // What the run reports over capacity, against what the case reads as over capacity. Computed
        // from the plan's own two numbers rather than from a count of conductors, which differs by
        // release.
        var run = new RouteRun(again, after.Network.Version, TimeSpan.Zero, bounded);

        var mine = bounded.Boxes
            .Where(box => box.Capacity > 0 && box.Conductors > box.Capacity)
            .ToList();

        Note(context, "over capacity: boxes the case reads as over it", mine.Count);
        Note(context, "over capacity: conductors at the fullest box",
            bounded.Boxes.Count == 0 ? 0 : bounded.Boxes.Max(box => box.Conductors));

        Expect.Same(
            mine.Count,
            run.Overfull.Count,
            "boxes the run reports over capacity, against boxes this case reads as over it");

        foreach (var box in run.Overfull)
        {
            Expect.That(
                box.Capacity > 0 && box.Conductors > box.Capacity,
                "the run reports a box of " + box.Conductors + " conductor(s) against a capacity of "
                + box.Capacity + " as over it");

            Expect.That(
                box.Existing is not null,
                "the run reports a recommended box as over a capacity, which a recommendation does not have");
        }

        Expect.Same(
            found.Count(one => one.Conductors <= 0),
            run.WithoutConductors,
            "circuits the run counts as reporting no conductors, against circuits Revit reports none for");

        Skip.When(
            mine.Count == 0,
            "with a capacity of one on every box type, no box of this model holds more: the circuits spliced in them report no conductors, and the notes of this case say how far it got in arranging for them to");

        // The warning half. Only the boxes of the host can be addressed by one.
        var addressable = mine
            .Where(box => box.Existing is { } one && !one.Id.IsLinked)
            .Select(box => box.Existing!.Id.Value)
            .ToList();

        Note(context, "over capacity: overfull boxes of the host", addressable.Count);

        NeedsDefinitionsFor(document, symbol, run, after);

        var outcome = ApplyWatched(context, watch, "over capacity", run, after, project, catalogue, out var processed);
        var cabling = processed.Where(one => one.IsCabling).ToList();
        var posted = processed.Where(one => one.Is(CablingFeature.JunctionBoxOverCapacity)).ToList();

        Note(context, "over capacity: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, cabling);

        Expect.Same(
            addressable.Count,
            posted.Count,
            "JunctionBoxOverCapacity warnings Revit processed, against boxes of the host this case reads as over capacity");

        EachAgainstOneOf(posted, addressable, "JunctionBoxOverCapacity", "one box of the host this case reads as over capacity");
    });
}
