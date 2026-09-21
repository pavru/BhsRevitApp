using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>Which circuits a carrier admits, through the reader and through the search.</summary>
public sealed partial class CablingApplyTests
{
    /// <summary>The group this case writes; unmistakably ours, so nothing in a model can collide with it.</summary>
    private const string TestGroup = "BHS-TEST-GROUP";

    /// <summary>
    /// A carrier told which cable groups it admits reads back admitting them, and one told nothing
    /// reads as admitting only circuits in no group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism, on the instance rather than the type</b> - the owner's answer of 2026-09-22,
    /// and the one place this feature departs from its neighbours. Role, splicing and capacity are
    /// properties of a product; a divider belongs to the length of tray that was installed. So this
    /// case writes on carriers, not on their types, and a reader that went to the type would find
    /// nothing and read every carrier as unmarked - which is the half the second assertion catches.
    /// </para>
    /// <para>
    /// <b>Half the carriers, never all of them.</b> Marking everything would make "and one told
    /// nothing reads as unmarked" unaskable, and that half is what catches a reader answering the
    /// same thing to everything.
    /// </para>
    /// <para>
    /// The host's carriers only: a link is never written to, so a case that marked one would be
    /// asserting about a write that cannot happen.
    /// </para>
    /// </remarks>
    private static void ACarrierThatStatesWhichGroupsItAdmitsIsReadBackStatingThem(RevitTestContext context) =>
        Watched(context, watch =>
        {
            var document = context.Document!;
            var application = context.Application.Application;
            var catalogue = new CarrierCatalogue();
            var project = CablingProjectSettings.Read(new FixedSettings());
            var symbol = NeedsIndicatorFamily(document, project);

            Bind(watch, document, application, RuntimeFor(symbol, catalogue));

            var before = CablingSnapshot.Build(
                document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

            // In a stable order: the case marks the first half, and a marking that moved between runs
            // would make its own notes unreadable.
            var carriers = before.Carriers
                .Where(one => !one.Id.IsLinked)
                .Select(one => one.Id.Value)
                .Distinct()
                .OrderBy(one => one)
                .ToList();

            Note(context, "grouping: carriers of the host", carriers.Count);

            Skip.When(
                carriers.Count < 2,
                "the model this sweep opened has fewer than two carriers in the host, so a marked one cannot be told from an unmarked one");

            var marked = carriers.Take(carriers.Count / 2).ToHashSet();

            Note(context, "grouping: carriers told which groups they admit", marked.Count);

            var mark = watch.Mark;
            TransactionStatus status;

            using (var transaction = new Transaction(document, "BHS test: some carriers admit a cable group"))
            {
                transaction.Start();

                foreach (var carrier in marked)
                    SetText(document.GetElement(new ElementId(carrier)), CablingParameters.AllowedGroups, TestGroup, "allowed groups");

                status = transaction.Commit();
            }

            Committed(watch, mark, status, "telling some carriers which cable groups they admit");

            var after = CablingSnapshot.Build(
                document, Options, catalogue, version: 2, project.Boxes, project.DefaultConnection);

            var admitting = 0;
            var unmarked = 0;

            foreach (var carrier in carriers)
            {
                if (after.Network.Node(new CarrierId(0, carrier)) is not { } node)
                    continue;

                if (marked.Contains(carrier))
                {
                    admitting++;

                    Expect.That(
                        node.Groups.Admits(TestGroup) && !node.Groups.Admits(string.Empty),
                        "carrier " + carrier + ", told it admits one named group, read back admitting " + node.Groups.Named.Count + " group(s)");
                }
                else
                {
                    unmarked++;

                    Expect.That(
                        node.Groups.IsUnmarked && !node.Groups.Admits(TestGroup),
                        "carrier " + carrier + ", told nothing, read back admitting a group it was never given");
                }
            }

            Note(context, "grouping: carriers read back admitting the group", admitting);
            Note(context, "grouping: carriers read back unmarked", unmarked);

            Skip.When(
                admitting == 0 || unmarked == 0,
                "of the carriers this model has, the read collected only the marked ones or only the unmarked ones, so one half of the question cannot be asked");
        });

    /// <summary>
    /// A circuit is laid only in carriers that admit its cable group: one in a group nobody admits is
    /// turned away and told apart from a circuit with nothing within reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rule of 2026-09-22, asserted against the same model three times</b> - nobody
    /// grouped, every circuit grouped and no carrier admitting it, and then the host's carriers told
    /// to admit it. One reading alone would leave "this circuit has no route" indistinguishable from
    /// "this circuit never had one".
    /// </para>
    /// <para>
    /// <b>What it asserts is an invariant, never a number.</b> How many circuits of the owner's model
    /// route is a fact about this week's model; that a route never runs along a carrier which does not
    /// admit it is a fact about the search, and that is the one which would go quietly wrong.
    /// </para>
    /// <para>
    /// <b>And the first reading is the control that keeps the rest honest:</b> on a model where nobody
    /// grouped anything, not one circuit may be reported as turned away. A rule that fired on an
    /// ungrouped project would change every existing answer, and it would do so silently - the failure
    /// this whole design is shaped to avoid.
    /// </para>
    /// <para>
    /// Nothing is applied. What the apply writes has its own cases; a permission decides where a route
    /// may run, and that is settled before anything is placed.
    /// </para>
    /// </remarks>
    private static void ACircuitIsLaidOnlyInCarriersThatAdmitItsGroup(RevitTestContext context) =>
        Watched(context, watch =>
        {
            var document = context.Document!;
            var application = context.Application.Application;
            var catalogue = new CarrierCatalogue();
            var project = CablingProjectSettings.Read(new FixedSettings());
            var symbol = NeedsIndicatorFamily(document, project);

            Bind(watch, document, application, RuntimeFor(symbol, catalogue));

            var plain = PlanFound(document, project, catalogue);

            var routed = plain.Results
                .Where(one => one.Status == RouteStatus.Found)
                .Select(one => one.Circuit.Value)
                .ToList();

            Note(context, "grouping: circuits described", plain.Snapshot.Circuits.Described.Count);
            Note(context, "grouping: routes found before anybody is grouped", routed.Count);

            Expect.Same(
                0,
                plain.Run.Count(RouteStatus.NoCarrierAllowed),
                "circuits reported as turned away for their cable group in a model where nobody named one");

            Skip.When(
                routed.Count == 0,
                "no circuit of the model this sweep opened has a route before anything is grouped, so there is nothing whose permission could be taken away");

            // Every circuit put in a group, and no carrier told to admit it.
            var mark = watch.Mark;
            TransactionStatus status;

            using (var transaction = new Transaction(document, "BHS test: every circuit is in a cable group"))
            {
                transaction.Start();

                foreach (var circuit in plain.Snapshot.Circuits.Described)
                    SetText(document.GetElement(new ElementId(circuit.Id.Value)), CablingParameters.CableGroup, TestGroup, "cable group");

                status = transaction.Commit();
            }

            Committed(watch, mark, status, "putting every circuit in a cable group");

            var orphaned = PlanFound(document, project, catalogue);

            Expect.Same(
                0,
                orphaned.Results.Count(one => one.Status == RouteStatus.Found),
                "routes found for circuits in a group no carrier in the model admits");

            foreach (var circuit in routed)
            {
                var result = orphaned.Results.Single(one => one.Circuit.Value == circuit);

                Expect.That(
                    result.Status == RouteStatus.NoCarrierAllowed,
                    "circuit " + circuit + " reached the structure and was refused by every carrier, and the run called that " + result.Status);
            }

            Note(context, "grouping: circuits turned away for their group", orphaned.Run.Count(RouteStatus.NoCarrierAllowed));

            // And now the host's carriers are told to admit it. The links are not written to, so what
            // routes here is whatever can be laid in the host alone - a number this case never claims.
            var hosted = orphaned.Snapshot.Carriers.Where(one => !one.Id.IsLinked).Select(one => one.Id.Value).ToList();

            mark = watch.Mark;

            using (var transaction = new Transaction(document, "BHS test: the host's carriers admit that group"))
            {
                transaction.Start();

                foreach (var carrier in hosted)
                    SetText(document.GetElement(new ElementId(carrier)), CablingParameters.AllowedGroups, TestGroup, "allowed groups");

                status = transaction.Commit();
            }

            Committed(watch, mark, status, "telling the host's carriers which cable group they admit");

            var admitted = PlanFound(document, project, catalogue);
            var found = admitted.Results.Where(one => one.Status == RouteStatus.Found).ToList();

            Note(context, "grouping: carriers of the host told to admit it", hosted.Count);
            Note(context, "grouping: routes found once the host admits the group", found.Count);

            // The invariant, and the only thing here worth asserting about a route: wherever a cable
            // ran, the carrier it ran along admits it.
            foreach (var route in found)
            {
                foreach (var carrier in route.Path)
                {
                    var node = admitted.Snapshot.Network.Node(carrier);

                    Expect.That(
                        node is not null && node.Groups.Admits(route.CableGroup),
                        "circuit " + route.Circuit.Value + " was laid along carrier " + carrier + ", which does not admit its cable group");
                }
            }

            Skip.When(
                found.Count == 0,
                "no circuit routes through the host's carriers alone once the links are refused, so there is no route whose carriers could be checked");
        });
}
