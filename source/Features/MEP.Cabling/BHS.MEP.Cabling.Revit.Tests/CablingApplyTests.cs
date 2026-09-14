using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Common.Parameters;
using BHS.Revit.Testing;
using BHS.Settings;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// That writing a run into the model binds what it writes to, places and matches and removes
/// indicators by the rules the owner set, and posts the warnings it owes - and refuses out loud when
/// it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first two guard the failures that are silent by construction, not the ones that are
/// loud.</b> Two of the three parameters the apply phase writes declare no category at compile time -
/// the indicator's family is the project's to choose - so a caller who forgot the runtime category
/// would bind none of them and nothing would say so. And a project whose indicator family has been
/// renamed would place nothing, which from outside is indistinguishable from a run with nothing to
/// place. Neither needs a route: both conditions are about the model and the project.
/// </para>
/// <para>
/// <b>The rest put every other rule of the apply in front of it, and each one builds its own
/// question.</b> The test models carry no connection values, no roles and no indicators, and they
/// should not - a model edited by hand to make a case pass tests the model. So a case cuts every
/// circuit in boxes, marks a fitting type a box, places a stray indicator or joins one to a conduit,
/// through the production path where there is one, and the harness rolls all of it back. What cannot
/// be built on the model a sweep opened is a loud skip naming what was missing, never a pass about
/// nothing.
/// </para>
/// <para>
/// <b>Invariants, never counts.</b> These run against somebody's real building. "One indicator of
/// ours at each recommended box" holds for any model; "four indicators" holds for this week. The
/// counts go into notes.
/// </para>
/// <para>
/// <b>The placement cases hand the apply only the routes that were found, and that is a departure
/// from the command, made on purpose.</b> Placement, matching, the references and the count of boxes
/// used read only the planned boxes and the found routes. The apply also posts a warning for every
/// route that failed, and a warning needs a failure definition that only an edition registers at
/// startup - so a placement case handed the whole run would stand down whenever some circuit of the
/// model failed to route and no edition was installed, for a reason that has nothing to do with
/// placement. The warnings have cases of their own, where a missing definition is the thing named.
/// </para>
/// <para>
/// <b>Every case that applies runs inside one failure watch, and its writes are undone while the watch
/// is still open.</b> See <see cref="PostedWarnings"/> for why a warning must not be left for Revit to
/// show in an unattended sweep. The undoing matters for the same reason: the harness rolls its own
/// group back after the case has returned, when nothing is watching - and whether undoing a deletion
/// or a joint posts failures of its own is not measured. Undone inside the watch, the harness is left
/// with nothing to undo.
/// </para>
/// <para>
/// <b>What these cases do not settle, named so it is not read as settled.</b>
/// </para>
/// <list type="bullet">
/// <item>Whether the four warnings stay in the model's warning list, which the apply's remarks rely
/// on. Revit's reference for <c>Document.PostFailure</c> says the opposite - "warnings posted via this
/// method will not be stored in the document after they are resolved" - and every case here dismisses
/// them, so none can observe it. Not measured; a question for the owner before those remarks are
/// relied on.</item>
/// <item>The format of <c>BHS_Cbl_CircuitRefs</c>: circuit ids on an indicator, circuit numbers on a
/// carrier. Only "names something" is asserted until the owner says which is meant.</item>
/// <item>Whether an indicator of ours that somebody joined, standing within the radius of a box this
/// run recommends, should be rewritten or left alone. The code rewrites it; the outcome's remarks say
/// it stopped being ours. Not pinned either way.</item>
/// <item>Whether Revit keeps the point an indicator was placed at, height included. Not measured;
/// the placement case notes the distance before it asserts anything, so a failure there says which.</item>
/// <item>That nothing appears on screen while these run. The first sweep that carries them is to be
/// run with somebody at the machine.</item>
/// </list>
/// </remarks>
public sealed class CablingApplyTests : IRevitTestSuite
{
    public string Name => "Cabling apply";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "applying binds every parameter it is about to write, including the runtime categories",
            BindsWhatItWrites,
            needsDocument: true,
            writes: true),

        new RevitTestCase(
            "an indicator family this model does not have is refused by name, not passed over",
            RefusesAMissingFamily,
            needsDocument: true,
            writes: true),

        new RevitTestCase(
            "every box the plan recommends gets one indicator of ours at its place, and nothing else is placed",
            PlacesWhatThePlanRecommends,
            writes: true),

        new RevitTestCase(
            "applying the same run again places nothing and removes nothing",
            ApplyingAgainChangesNothing,
            writes: true),

        new RevitTestCase(
            "the indicators an apply placed are not read back as structure",
            PlacedIndicatorsAreNotStructure,
            writes: true),

        new RevitTestCase(
            "a junction box already in the model is used instead of an indicator, and gets only the circuits through it",
            AnExistingBoxIsUsed,
            writes: true),

        new RevitTestCase(
            "an indicator of ours the plan no longer names is removed, and one without our recommendation is left",
            RemovesWhatThePlanNoLongerNames,
            writes: true),

        new RevitTestCase(
            "an indicator of ours joined to the structure is left, not removed",
            LeavesAJoinedIndicator,
            writes: true),

        new RevitTestCase(
            "an element of another type carrying our recommendation is never removed",
            LeavesAnotherTypeAlone,
            writes: true),

        new RevitTestCase(
            "an unreadable connection value is posted as a warning against its circuit",
            WarnsOfAnUnreadableConnection,
            writes: true),

        new RevitTestCase(
            "every circuit that reaches no carrier is posted as one warning",
            WarnsOfNoCarrierNear,
            writes: true),

        new RevitTestCase(
            "every circuit whose carriers do not join up is posted as one warning",
            WarnsOfNoConnectivity,
            writes: true),

        new RevitTestCase(
            "a junction box joined to nothing is posted as a warning against it",
            WarnsOfABoxJoinedToNothing,
            writes: true),
    };

    /// <summary>The value this suite writes wherever it wants a circuit's connection to fail to read.</summary>
    private const string Mistyped = "Junkbox";

    /// <summary>The categories the junction box role is declared on, as the parameter scheme ships them.</summary>
    /// <remarks>
    /// Repeated here rather than read from the scheme, which keeps them private: a fitting of any other
    /// category has no role parameter to set, and a case that tried would fail for a reason of
    /// construction.
    /// </remarks>
    private static readonly BuiltInCategory[] RoleCategories =
    {
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_ConduitFitting,
    };

    private static void BindsWhatItWrites(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);

        // Written here rather than trusted to a suite declared earlier. The apply binds from our
        // shared parameter file and never writes it, so without this the case passed only because
        // the connection and junction box suites run first and leave the file behind them.
        new CablingParameters().Export(application);

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        // An empty run: nothing to place, so what is left is exactly the part under test - the
        // parameters being bound before anything is written to them.
        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(run, snapshot);
        ApplyWatched(context, watch, "binding", run, snapshot, project, catalogue);

        // Asked of the document afterwards rather than taken from what Install reported, because the
        // question is what is bound now. The same runtime set the apply path composes.
        var missing = new CablingParameters()
            .Missing(document, RuntimeFor(symbol, catalogue))
            .Where(one => one.Id == CablingParameters.Recommendation
                          || one.Id == CablingParameters.CircuitRefs
                          || one.Id == CablingParameters.TapCount)
            .Select(one => string.Join(" / ", one.Names()))
            .ToList();

        Expect.Same(
            0,
            missing.Count,
            "parameters still unbound after applying, which means the run had nowhere to write: "
            + string.Join(", ", missing));

        context.Note("indicator category", CategoryOf(symbol).ToString());
        context.Note("carrier categories", catalogue.Categories.Count().ToString(CultureInfo.InvariantCulture));
    });

    /// <remarks>
    /// <b>Declared as writing, and watched, though today it writes nothing.</b> The case exists for
    /// the day the apply stops refusing, and on that day the production write path runs here in full:
    /// bindings committed, indicators of a matched type removed, warnings raised. Unguarded, that
    /// leaves the opened model changed and Revit asking about saving when the sweep closes it, with
    /// nobody to answer - a regression that would read as a stuck Revit rather than as this case
    /// failing. No definitions are asked for: an apply that went ahead and posted one this session
    /// does not have would throw inside the watched group, and be undone like any other failure there.
    /// </remarks>
    private static void RefusesAMissingFamily(RevitTestContext context) => Watched(context, _ =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();

        // A name no model has, and said so in the name itself: if this ever matched something, the
        // case would be passing for the wrong reason.
        const string Absent = "BHS_CBL_NoSuchFamily_ForTheSweepOnly";

        var project = CablingProjectSettings.Read(new Fixed
        {
            [RecommendedBoxes.FamilyKey] = Absent,
            [RecommendedBoxes.TypeKey] = "NoSuchType",
        });

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);
        var outcome = CablingApply.Apply(document, application, run, snapshot, project, catalogue);

        Expect.That(outcome.Refused, "applying accepted an indicator family this model does not have");

        // Named, not merely refused. "Nothing was placed" sends somebody looking through their model;
        // the family name sends them to the setting or to the rename that caused it.
        Expect.That(
            // IndexOf rather than Contains with a comparison: that overload of Contains arrived with
            // .NET Core and does not exist on net48, which is half the supported Revit range.
            outcome.Refusals.Any(one => one.IndexOf(Absent, StringComparison.Ordinal) >= 0),
            "the refusal does not name the family that is missing: " + string.Join(" ", outcome.Refusals));
    });

    /// <summary>
    /// The plan's recommendations become indicators of ours, one each, where the plan put them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What keeps this from passing about nothing</b> is not the count of indicators the apply
    /// reports - it increments one of two counters per recommended box whatever happens, so that sum
    /// cannot disagree with the plan. It is the model: a new instance of the indicator type for every
    /// placement reported, and exactly one of ours within the radius of every recommended box, read
    /// back from the document. The skip guarantees there is at least one box to look for.
    /// </para>
    /// <para>
    /// <b>The distance is noted before anything is asserted.</b> Whether Revit keeps the height an
    /// indicator is placed at, for a family placed on a level below it, is not measured - and if it
    /// does not, "exactly one within the radius" fails and the note is what says why.
    /// </para>
    /// <para>
    /// <b>The same apply also tells the carriers their circuits, and that is asserted here too</b>,
    /// after the indicators so a placement defect is named first: every host carrier a found route
    /// walks, which named nothing before, names something after; and a reference falls in a link
    /// exactly when a route or a used box passes through one. The case stands down before applying
    /// when there is no such host carrier - a model whose every carrier is linked, or already
    /// referenced - rather than pass that half about nothing.
    /// </para>
    /// </remarks>
    private static void PlacesWhatThePlanRecommends(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "placement");

        var cut = CutEveryCircuitInBoxes(watch, document, application);
        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Where(box => box.IsRecommendation).ToList();

        Note(context, "placement: circuits cut in boxes", cut);
        Note(context, "placement: routes found", plan.Run.Found);
        Note(context, "placement: routes not found, left out of the run", plan.Results.Count - plan.Run.Found);
        Note(context, "placement: boxes recommended", wanted.Count);
        Note(context, "placement: boxes already in the model used", plan.Run.Boxes.Count - wanted.Count);

        Skip.When(
            wanted.Count == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so the plan recommends no box to place");

        // The carriers the found routes walk in the host, and what they named before the apply. Read
        // first, because a reference already standing on a carrier would otherwise answer for one
        // the apply never wrote.
        var hostPath = plan.Run.Results
            .SelectMany(route => route.Path)
            .Where(carrier => !carrier.IsLinked)
            .Select(carrier => carrier.Value)
            .Distinct()
            .ToList();

        var fresh = hostPath.Where(id => Value(document.GetElement(new ElementId(id)), CablingParameters.CircuitRefs).Length == 0).ToList();

        Note(context, "placement: carriers on found routes in the host", hostPath.Count);
        Note(context, "placement: of them already naming a circuit before the apply", hostPath.Count - fresh.Count);

        Skip.When(
            fresh.Count == 0,
            hostPath.Count == 0
                ? "every carrier the found routes walk lives in a link, so the apply has no carrier in the host to tell its circuits"
                : "every carrier the found routes walk in the host already names a circuit before the apply, so a reference it writes cannot be told from one that stood there");

        NeedsDefinitionsFor(plan.Run, plan.Snapshot);

        var before = Ids(IndicatorsOf(document, symbol));
        var outcome = ApplyWatched(context, watch, "placement", plan.Run, plan.Snapshot, project, catalogue);
        var after = IndicatorsOf(document, symbol);
        var added = after.Where(one => !before.Contains(one.Id.Value)).ToList();

        Note(context, "placement: indicators placed", outcome.Placed);
        Note(context, "placement: indicators found in place", outcome.Updated);
        Note(context, "placement: carriers told their circuits", outcome.CarriersMarked);
        Note(context, "placement: references that fell in a link", outcome.InLinks);
        context.Note(
            "placement: farthest planned box from its nearest instance of the indicator type, internal feet",
            Farthest(wanted, after));

        // What the screen reports to the person who pressed Apply, and nothing more: see the remarks.
        Expect.Same(
            wanted.Count,
            outcome.Placed + outcome.Updated,
            "boxes the plan recommends, against indicators the apply reports placing or finding in place");

        Expect.Same(
            outcome.Placed,
            added.Count,
            "indicators the apply reports placing, against new instances of the indicator type in the model");

        // Two passes, and the order is the point. With one, an indicator that landed within the radius
        // of a neighbouring box would pass that box's count and then fail its entry count - the
        // neighbour's value against this box's - and a placement defect would read as a TapCount one.
        // Every box is shown to have exactly one indicator before any value on one is compared.
        var nearest = new List<(PlannedBox Box, FamilyInstance Indicator)>();

        foreach (var box in wanted)
        {
            var at = Planned(box);
            var near = after.Where(one => CarriesOurRecommendation(one) && Within(one, at, project.BoxRadius)).ToList();

            Expect.Same(
                1,
                near.Count,
                "indicators of ours within the box radius of the box planned at " + Describe(at) + " ft");

            nearest.Add((box, near[0]));
        }

        foreach (var (box, indicator) in nearest)
        {
            var id = indicator.Id.Value;
            var entries = indicator.get_Parameter(CablingParameters.TapCount);

            Expect.That(
                entries is { HasValue: true },
                "indicator " + id + " carries no cable entry count");

            Expect.Same(
                box.Entries,
                entries!.AsInteger(),
                "cable entries written on indicator " + id + ", against the plan's count for its box");

            // That it names something, and not how: the two formats this parameter is written in are
            // an open question for the owner, and a case that pinned one would decide it.
            Expect.That(
                !string.IsNullOrEmpty(indicator.get_Parameter(CablingParameters.CircuitRefs)?.AsString()),
                "indicator " + id + " names no circuit");
        }

        foreach (var instance in added)
        {
            Expect.That(
                CarriesOurRecommendation(instance),
                "instance " + instance.Id.Value + " of the indicator type was placed without our recommendation on it");

            Expect.That(
                wanted.Any(box => Within(instance, Planned(box), project.BoxRadius)),
                "indicator " + instance.Id.Value + " stands where the plan recommends no box");
        }

        // The second of the apply's three writes: the carriers are told their circuits. That it names
        // something, and not how - the format is the owner's open question. A carrier Revit keeps
        // read-only, which an element in a group may be, is not excused: the apply skips such a write
        // without a word, and a red here is the only place that would say so. Not measured whether
        // the models a sweep opens hold one.
        foreach (var id in fresh)
        {
            var refs = document.GetElement(new ElementId(id))?.get_Parameter(CablingParameters.CircuitRefs);

            Expect.That(
                !string.IsNullOrEmpty(refs?.AsString()),
                "carrier " + id + " on a found route names no circuit after the apply"
                + (refs is { IsReadOnly: true } ? ", and Revit keeps its circuit references read-only" : string.Empty));
        }

        // Presence only, not the count: what InLinks counts is an open question for the owner. A
        // reference falls in a link from a found route's path or from a used box standing in one.
        var throughALink = plan.Run.Results.Any(route => route.Path.Any(carrier => carrier.IsLinked))
                           || plan.Run.Boxes.Any(box => box.Existing is { } existing && existing.Id.IsLinked);

        Expect.That(
            (outcome.InLinks > 0) == throughALink,
            "the apply reports " + outcome.InLinks + " reference(s) that fell in a link, while the found routes and used boxes "
            + (throughALink ? "do" : "do not") + " pass through one");
    });

    /// <summary>
    /// A second press with nothing changed finds every indicator where the first one put it.
    /// </summary>
    /// <remarks>
    /// Matching is by place because place is all an indicator is between runs - so this is the case
    /// that fails if the place Revit keeps is not the place the plan asked for, or if the radius that
    /// decides "standing where" ever stops being the radius the plan kept its boxes apart by.
    /// </remarks>
    private static void ApplyingAgainChangesNothing(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "second apply");

        CutEveryCircuitInBoxes(watch, document, application);

        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Count(box => box.IsRecommendation);

        Note(context, "second apply: boxes recommended", wanted);

        Skip.When(
            wanted == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so there is nothing for a second apply to find in place");

        NeedsDefinitionsFor(plan.Run, plan.Snapshot);

        var first = ApplyWatched(context, watch, "second apply, first press", plan.Run, plan.Snapshot, project, catalogue);
        var standingFirst = Ids(IndicatorsOf(document, symbol).Where(CarriesOurRecommendation));

        var second = ApplyWatched(context, watch, "second apply, second press", plan.Run, plan.Snapshot, project, catalogue);
        var standingSecond = Ids(IndicatorsOf(document, symbol).Where(CarriesOurRecommendation));

        Note(context, "second apply: indicators placed by the first", first.Placed);
        Note(context, "second apply: indicators found in place by the second", second.Updated);

        Expect.Same(0, second.Placed, "indicators placed anew by a second apply of the same run");
        Expect.Same(0, second.Removed, "indicators removed by a second apply of the same run");

        Expect.Same(
            first.Placed + first.Updated,
            second.Updated,
            "indicators the first apply left standing, against the ones the second apply found in place");

        Expect.Same(first.Adopted, second.Adopted, "indicators left as joined by the first apply, against the second");

        foreach (var id in standingFirst)
        {
            Expect.That(
                document.GetElement(new ElementId(id)) is not null,
                "indicator " + id + ", standing after the first apply, is gone after the second");
        }

        Expect.Same(
            standingFirst.Count,
            standingSecond.Count,
            "indicators of ours after the first apply, against after the second");
    });

    /// <summary>
    /// Indicators the apply placed in a carrier category are recognised as ours on the next read.
    /// </summary>
    /// <remarks>
    /// <b>This closes a check that has so far been true of nothing.</b> The reading suite asserts that
    /// the carriers which disappear when markers are recognised are exactly the markers recognised -
    /// and on the models a sweep opens that is zero against zero, because none holds a marker. Here
    /// the markers are the apply's own, which is also the only way they ever reach a model.
    /// </remarks>
    private static void PlacedIndicatorsAreNotStructure(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "re-read");

        CutEveryCircuitInBoxes(watch, document, application);

        var plan = PlanFound(document, project, catalogue);

        Skip.When(
            !plan.Run.Boxes.Any(box => box.IsRecommendation),
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so the apply places nothing that could be read back");

        NeedsDefinitionsFor(plan.Run, plan.Snapshot);

        var before = Ids(IndicatorsOf(document, symbol));

        ApplyWatched(context, watch, "re-read", plan.Run, plan.Snapshot, project, catalogue);

        var inCatalogue = IndicatorsOf(document, symbol)
            .Count(one => !before.Contains(one.Id.Value)
                          && one.Category is { } category
                          && catalogue.ClassOf((BuiltInCategory)category.Id.Value).Length > 0);

        Skip.When(
            inCatalogue == 0,
            "the apply placed no new indicator of a category the catalogue collects, so nothing it placed could be mistaken for structure");

        var reread = CablingSnapshot.Build(
            document, Options, catalogue, version: 2, project.Boxes, project.DefaultConnection);

        Note(context, "re-read: indicators placed in a carrier category", inCatalogue);
        Note(context, "re-read: markers excluded before", plan.Snapshot.MarkersExcluded);
        Note(context, "re-read: markers excluded after", reread.MarkersExcluded);

        Expect.Same(
            plan.Snapshot.Carriers.Count,
            reread.Carriers.Count,
            "carriers read before the apply, against after it placed " + inCatalogue + " indicator(s) of a carrier category");

        Expect.Same(
            plan.Snapshot.MarkersExcluded + inCatalogue,
            reread.MarkersExcluded,
            "markers excluded before the apply plus the indicators it placed, against markers excluded reading again");
    });

    /// <summary>
    /// A box a designer put in the wiring serves its taps, and the apply adds only which circuits run
    /// through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every fitting type whose instances are all joined to a carrier is marked a box</b>, rather
    /// than the one the junction box suite marks. Whether any tap of a model comes within the box
    /// radius of a box is not known in advance, and more boxes is more chances. Only types whose every
    /// instance is joined, so that marking them makes no box joined to nothing and the apply has no
    /// warning to post here.
    /// </para>
    /// <para>
    /// <b>The radius is widened to a metre for the same reason</b>, against the project's hundred and
    /// fifty millimetres: a box's point is the middle of its connectors, and a tap within that little
    /// of it is a matter of luck on any given model. Nothing asserted here depends on the radius. The
    /// planner seeds the model's boxes first and opens a recommended one only where none stands within
    /// the radius, so no indicator of ours stands within it of a used box whatever the radius is; the
    /// references and the untouched values do not involve it at all.
    /// </para>
    /// <para>
    /// The joint is asked of Revit with this suite's own code rather than the reader's, so the case
    /// and the code under test do not agree by construction about which fittings qualify.
    /// </para>
    /// </remarks>
    private static void AnExistingBoxIsUsed(RevitTestContext context) => Watched(context, watch =>
    {
        const string RadiusMm = "1000";

        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed { [CablingProjectSettings.BoxRadiusKey] = RadiusMm });
        var symbol = NeedsIndicatorFamily(document, project);

        context.Note("existing box: box radius, mm", RadiusMm);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "existing box");

        // With the runtime categories, so the values compared before and after the apply are read
        // from the same bindings.
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        var joinedTypes = HostFittings(document, symbol)
            .GroupBy(one => one.GetTypeId().Value)
            .Where(group => group.All(one => JoinsACarrier(one, catalogue)))
            .Select(group => group.Key)
            .ToList();

        Note(context, "existing box: fitting types marked a box", joinedTypes.Count);

        Skip.When(
            joinedTypes.Count == 0,
            "the model this sweep opened has no fitting type whose every instance is joined to a carrier, so no box can be marked without making one joined to nothing");

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: every joined fitting type is a box"))
        {
            transaction.Start();

            foreach (var type in joinedTypes)
                SetText(document.GetElement(new ElementId(type)), CablingParameters.ElementRole, CablingParameters.JunctionBoxRole, "role");

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "marking the joined fitting types a box");
        CutEveryCircuitInBoxes(watch, document, application);

        var plan = PlanFound(document, project, catalogue);
        var used = plan.Run.Boxes.Where(box => box.Existing is not null).ToList();

        Note(context, "existing box: boxes in the model", plan.Snapshot.Boxes.Count);
        Note(context, "existing box: boxes in the model a tap used", used.Count);
        Note(context, "existing box: boxes recommended", plan.Run.Boxes.Count - used.Count);

        Skip.When(
            used.Count == 0,
            "with every circuit cut in boxes and every joined fitting type marked a box, no tap of the model this sweep opened comes within the box radius of one, so no box already in the model is used");

        NeedsDefinitionsFor(plan.Run, plan.Snapshot);

        var inHost = used.Where(box => !box.Existing!.Id.IsLinked).Select(box => box.Existing!.Id.Value).Distinct().ToList();
        var valuesBefore = inHost.ToDictionary(id => id, id => ValuesOf(document.GetElement(new ElementId(id))));

        // Apart from the values that must not change, because this one must: a reference that stood
        // on a box before the apply would otherwise answer for one the apply never wrote.
        var refsBefore = inHost.ToDictionary(id => id, id => Value(document.GetElement(new ElementId(id)), CablingParameters.CircuitRefs));

        Note(context, "existing box: used boxes in a link", used.Count - used.Count(box => !box.Existing!.Id.IsLinked));
        Note(context, "existing box: used boxes in the host already naming a circuit", refsBefore.Count(pair => pair.Value.Length > 0));

        var outcome = ApplyWatched(context, watch, "existing box", plan.Run, plan.Snapshot, project, catalogue);

        // Noted, not asserted: the apply derives it from the plan exactly as this case would, so a
        // comparison could not disagree. What the model says is asserted below instead.
        Note(context, "existing box: boxes in the model the apply reports using", outcome.ExistingUsed);

        // Collected once, not per box: every family instance of the host would be read again for
        // every box, on the API thread, under a question the sweep asks without a deadline.
        var ours = IndicatorsOf(document, symbol).Where(CarriesOurRecommendation).ToList();

        foreach (var box in used)
        {
            var at = Planned(box);
            var near = ours.Count(one => Within(one, at, project.BoxRadius));

            Expect.Same(
                0,
                near,
                "indicators of ours within the box radius of box " + box.Existing!.Id + ", which is already in the model and served a tap");
        }

        foreach (var id in inHost)
        {
            var element = document.GetElement(new ElementId(id));

            if (refsBefore[id].Length == 0)
            {
                Expect.That(
                    !string.IsNullOrEmpty(element?.get_Parameter(CablingParameters.CircuitRefs)?.AsString()),
                    "box " + id + ", already in the model and used by the plan, names no circuit after the apply");
            }

            Expect.That(
                ValuesOf(element) == valuesBefore[id],
                "box " + id + ", already in the model, had its recommendation or cable entry count written: before "
                + valuesBefore[id] + ", after " + ValuesOf(element));
        }
    });

    /// <summary>
    /// Removal takes exactly what a previous run of ours left and this one no longer wants.
    /// </summary>
    /// <remarks>
    /// <b>The plan is empty on purpose</b> - what the next press gives when a project turns to
    /// terminals - so removal is put to the apply without routing or matching in the way. Both
    /// indicators are the case's own, placed a hundred feet beyond everything the read found.
    /// </remarks>
    private static void RemovesWhatThePlanNoLongerNames(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);
        var snapshot = Prepare(watch, document, application, symbol, project, catalogue);
        var level = LowestLevel(document);
        var clear = Clearing(snapshot);

        var stale = PlaceLoose(
            watch, document, symbol, clear, level, "an indicator of ours the plan no longer names", RecommendJunctionBox).Id.Value;

        var loose = PlaceLoose(
            watch, document, symbol, clear + new XYZ(20, 0, 0), level, "an indicator without our recommendation", _ => { }).Id.Value;

        Expect.That(
            CarriesOurRecommendation(document.GetElement(new ElementId(stale))),
            "indicator " + stale + " does not read back the recommendation just written on it, so binding through the production path did not reach its category");

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(run, snapshot);

        var outcome = ApplyWatched(context, watch, "removal", run, snapshot, project, catalogue);

        Note(context, "removal: indicators removed", outcome.Removed);
        Note(context, "removal: indicators left as joined", outcome.Adopted);

        Expect.That(
            document.GetElement(new ElementId(stale)) is null,
            "indicator " + stale + ", ours and joined to nothing, is still standing after an apply whose plan names no box");

        Expect.That(
            document.GetElement(new ElementId(loose)) is not null,
            "instance " + loose + " of the indicator type, without our recommendation, was removed by an apply whose plan names no box");

        Expect.That(
            outcome.Removed >= 1,
            "the apply reports removing nothing, though indicator " + stale + " is gone");
    });

    /// <summary>
    /// The guard that makes removal safe at all: an indicator somebody joined into the wiring stays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The joint is built with a conduit drawn out of the indicator's own connector</b>, and very
    /// little of that is measured: the connectors' domain, whether a conduit of the model's first type
    /// accepts the connector's size, whether <c>ConnectTo</c> takes it, and whether the joint survives
    /// the commit. Each is a loud skip of its own, and a sweep that skips here has not covered this
    /// rule - which the record then says by name.
    /// </para>
    /// <para>
    /// The diameter is set from the connector before joining, when the connector is round. Not
    /// measured either; it removes the refusal that seemed likeliest.
    /// </para>
    /// </remarks>
    private static void LeavesAJoinedIndicator(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);
        var snapshot = Prepare(watch, document, application, symbol, project, catalogue);
        var level = LowestLevel(document);
        var clear = Clearing(snapshot);

        var conduitType = new FilteredElementCollector(document).OfClass(typeof(ConduitType)).FirstElementId();

        Skip.When(
            conduitType == ElementId.InvalidElementId,
            "the model this sweep opened holds no conduit type to join to the indicator");

        var mark = watch.Mark;
        long joined;
        long conduitId;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: an indicator joined to a conduit"))
        {
            transaction.Start();

            if (!symbol.IsActive)
                symbol.Activate();

            var indicator = Place(document, symbol, clear, level, "an indicator to join to a conduit");
            RecommendJunctionBox(indicator);
            document.Regenerate();

            var connectors = ConnectorsOf(indicator);

            context.Note(
                "joined: indicator connectors",
                connectors.Count.ToString(CultureInfo.InvariantCulture) + ": "
                + string.Join(", ", connectors.Select(one => one.Domain.ToString()).Distinct()));

            var socket = connectors.FirstOrDefault(one =>
                one.ConnectorType != ConnectorType.Logical
                && one.Domain == Domain.DomainCableTrayConduit
                && !one.IsConnected);

            Skip.When(socket is null, "the indicator family has no free conduit connector, so no conduit can be joined to it");

            var origin = socket!.Origin;
            Conduit? conduit = null;

            try
            {
                conduit = Conduit.Create(
                    document, conduitType, origin, origin + (socket.CoordinateSystem.BasisZ * 3), level.Id);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException refused)
            {
                Skip.Because("Revit refused to draw a conduit out of the indicator's connector: " + Describe(refused));
            }

            if (socket.Shape == ConnectorProfileType.Round
                && conduit!.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM) is { IsReadOnly: false } diameter)
            {
                diameter.Set(socket.Radius * 2);
                document.Regenerate();
            }

            var end = ConnectorsOf(conduit!).FirstOrDefault(one =>
                one.ConnectorType == ConnectorType.End && one.Origin.IsAlmostEqualTo(origin));

            Skip.When(end is null, "the conduit drawn from the indicator's connector has no end at that connector");

            try
            {
                if (!end!.IsConnectedTo(socket))
                    end.ConnectTo(socket);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException refused)
            {
                Skip.Because("Revit refused to join a conduit to the indicator: " + Describe(refused));
            }

            joined = indicator.Id.Value;
            conduitId = conduit!.Id.Value;
            status = transaction.Commit();
        }

        Expect.That(watch.Fault is null, "the test's own failure handler threw while joining a conduit to an indicator: " + Describe(watch.Fault));

        var worse = watch.Since(mark).Count(one => one.Severity != FailureSeverity.Warning);

        Skip.When(
            worse > 0,
            "joining a conduit to an indicator placed outside the model raised " + worse + " failure(s) worse than a warning");

        Skip.When(
            status != TransactionStatus.Committed,
            "joining a conduit to an indicator placed outside the model did not commit (" + status + ")");

        Skip.When(
            !JoinedTo(document.GetElement(new ElementId(joined)) as FamilyInstance, conduitId),
            "Revit did not keep the conduit joined to the indicator after the commit, so the joined case cannot be put");

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(run, snapshot);

        var outcome = ApplyWatched(context, watch, "joined", run, snapshot, project, catalogue);

        Note(context, "joined: indicators left as joined", outcome.Adopted);

        Expect.That(
            document.GetElement(new ElementId(joined)) is not null,
            "indicator " + joined + ", ours and joined to a conduit, was removed by an apply whose plan names no box");

        Expect.That(
            outcome.Adopted >= 1,
            "the apply counted no indicator as left for being joined, though indicator " + joined + " is joined to a conduit");
    });

    /// <summary>
    /// The first of removal's three conditions: only the configured type is ever ours to take away.
    /// </summary>
    /// <remarks>
    /// Our recommendation is bound to the indicator's whole category, so another fitting of that
    /// category can carry it - typed in by hand, or copied from an indicator. The type is what says a
    /// previous run of ours placed it; without that condition the apply would delete such an element
    /// and nothing else here would notice.
    /// </remarks>
    private static void LeavesAnotherTypeAlone(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);
        var snapshot = Prepare(watch, document, application, symbol, project, catalogue);
        var other = NeedsAnotherType(context, document, symbol, "another type");
        var level = LowestLevel(document);

        var stranger = PlaceLoose(
            watch,
            document,
            other,
            Clearing(snapshot) + new XYZ(40, 0, 0),
            level,
            "an element of another type carrying our recommendation",
            RecommendJunctionBox).Id.Value;

        Expect.That(
            CarriesOurRecommendation(document.GetElement(new ElementId(stranger))),
            "element " + stranger + " does not read back the recommendation just written on it, so binding through the production path did not reach its category");

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(run, snapshot);
        ApplyWatched(context, watch, "another type", run, snapshot, project, catalogue);

        Expect.That(
            document.GetElement(new ElementId(stranger)) is not null,
            "element " + stranger + " of a type other than the indicator's, carrying our recommendation and joined to nothing, was removed by an apply whose plan names no box");
    });

    private static void WarnsOfAnUnreadableConnection(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());

        NeedsIndicatorFamily(document, project);

        var described = new CircuitReader().Read(document).Described;

        Skip.When(
            described.Count == 0,
            "the model this sweep opened describes no circuit, so there is none to put a mistyped connection value on");

        var circuit = described[0].Id.Value;

        Bind(watch, document, application, runtime: null);

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: a mistyped connection"))
        {
            transaction.Start();
            SetText(document.GetElement(new ElementId(circuit)), CablingParameters.CircuitConnection, Mistyped, "connection");
            status = transaction.Commit();
        }

        Committed(watch, mark, status, "writing a mistyped connection value");

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var expected = snapshot.Circuits.UnreadableConnectionIds;

        Expect.That(
            expected.Contains(circuit),
            "circuit " + circuit + " holds '" + Mistyped + "' on itself and the read did not name it unreadable, so the apply had nothing to warn about");

        // The condition comes from the read, not from a route, so an empty run exercises it exactly.
        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(run, snapshot);

        var outcome = ApplyWatched(context, watch, "unreadable", run, snapshot, project, catalogue, out var processed);
        var cabling = processed.Where(one => one.IsCabling).ToList();
        var mine = processed.Where(one => one.Is(CablingFeature.ConnectionUnreadable)).ToList();

        Note(context, "unreadable: circuits named unreadable", expected.Count);
        Note(context, "unreadable: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, cabling);

        Expect.Same(
            1,
            mine.Count(one => one.Elements.Count == 1 && one.Elements[0] == circuit),
            "ConnectionUnreadable warnings Revit processed against circuit " + circuit + ", which holds '" + Mistyped + "' on itself");

        Expect.Same(
            expected.Count,
            mine.Count,
            "ConnectionUnreadable warnings Revit processed, against circuits the read named unreadable");

        EachAgainstOneOf(mine, expected, "ConnectionUnreadable", "one circuit the read called unreadable");
    });

    /// <summary>
    /// A circuit the router could not bring near any carrier is named in the model's warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Routed over a network with no carriers in it</b>, which is the command's own loop over a
    /// model whose carriers are gone: every circuit stops at its first end, on any model. Not by
    /// setting the reach to zero - the network indexes an open tray along its length in steps of the
    /// reach, and at zero that is millions of index points per tray, built on the API thread under a
    /// question the sweep asks without a deadline.
    /// </para>
    /// <para>
    /// <b>The name says nothing about which element the warning is set on</b>, and on purpose. Today
    /// it is the circuit, while the registered text says "this device" - a question for the owner. If
    /// the warning moves, the body of this case changes and its name, which the record keys on, stays.
    /// </para>
    /// </remarks>
    private static void WarnsOfNoCarrierNear(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());

        NeedsIndicatorFamily(document, project);

        // Written here, as every other case that binds writes it: the apply binds from our shared
        // parameter file and never writes it, and a file some earlier case left behind is the one
        // thing the rollback does not undo - on a clean profile, or run alone, the apply would refuse
        // instead of posting anything.
        new CablingParameters().Export(context.Application.Application);

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        Skip.When(
            snapshot.Circuits.Described.Count == 0,
            "the model this sweep opened describes no circuit, so none can fail to reach a carrier");

        var bare = NetworkBuilder.Build(snapshot.Network.Version, Array.Empty<CarrierNode>(), Options);
        var results = snapshot.Circuits.Described.Select(circuit => Router.Route(bare, circuit, Options)).ToList();

        var run = new RouteRun(results, bare.Version, TimeSpan.Zero)
        {
            Boxes = BoxPlanner.Plan(results, snapshot.Boxes, project.BoxRadius),
        };

        var blocked = run.Blocked(RouteStatus.NoCarrierNear).Select(one => one.Circuit.Value).ToList();

        Note(context, "no carrier: circuits blocked", blocked.Count);

        Skip.When(
            blocked.Count == 0,
            "routed over a network with no carriers, no circuit came back NoCarrierNear, so there is nothing to warn about");

        NeedsDefinitionsFor(run, snapshot);

        var outcome = ApplyWatched(context, watch, "no carrier", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.NoCarrierNear)).ToList();

        Note(context, "no carrier: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());
        OnePerCircuit(mine, blocked, "NoCarrierNear", "which reached no carrier", "circuits the run could not bring near a carrier");
    });

    /// <summary>
    /// A circuit whose ends are near carriers that do not join is named in the model's warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The model's own carriers, with every joint taken away.</b> Carriers are joined when their
    /// terminals lie within the join tolerance, and no two points lie within a negative distance - so
    /// the network keeps every carrier and its reach and loses only its adjacency. A circuit whose
    /// panel and next device come near different carriers then cannot cross; how many do is the
    /// model's to say, and none is a loud skip.
    /// </para>
    /// <para>
    /// <b>Only those circuits are handed to the apply.</b> The others either routed on one carrier or
    /// reached none, and the second kind would post the warning of the case above, which needs no
    /// second place to be proven.
    /// </para>
    /// </remarks>
    private static void WarnsOfNoConnectivity(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());

        NeedsIndicatorFamily(document, project);

        // See the case above: the file the apply binds from is written by the case, not inherited.
        new CablingParameters().Export(context.Application.Application);

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        Skip.When(
            snapshot.Carriers.Count == 0 || snapshot.Circuits.Described.Count == 0,
            "the model this sweep opened holds no carriers or describes no circuit, so no circuit can fail to cross between carriers");

        var disconnected = new RoutingOptions
        {
            JoinTolerance = -1,
            MaxApproach = Options.MaxApproach,
            AxisAlignedApproach = Options.AxisAlignedApproach,
        };

        var network = NetworkBuilder.Build(snapshot.Network.Version, snapshot.Carriers, disconnected);
        var results = snapshot.Circuits.Described.Select(circuit => Router.Route(network, circuit, disconnected)).ToList();
        var unreachable = results.Where(one => one.Status == RouteStatus.NoConnectivity).ToList();
        var run = new RouteRun(unreachable, network.Version, TimeSpan.Zero);
        var blocked = unreachable.Select(one => one.Circuit.Value).ToList();

        Note(context, "no joints: circuits that could not cross", blocked.Count);
        Note(context, "no joints: circuits with no carrier near", results.Count(one => one.Status == RouteStatus.NoCarrierNear));
        Note(context, "no joints: circuits routed on one carrier", results.Count(one => one.Status == RouteStatus.Found));

        Skip.When(
            blocked.Count == 0,
            "with every joint between carriers taken away, no circuit of the model this sweep opened came back NoConnectivity, so there is nothing to warn about");

        NeedsDefinitionsFor(run, snapshot);

        var outcome = ApplyWatched(context, watch, "no joints", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.NoConnectivity)).ToList();

        Note(context, "no joints: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());
        OnePerCircuit(mine, blocked, "NoConnectivity", "whose carriers do not join up", "circuits the run could not carry across the structure");
    });

    /// <summary>
    /// An element whose type calls it a box, standing beside the structure rather than in it, is named
    /// in the model's warnings.
    /// </summary>
    /// <remarks>
    /// The box is another type of the indicator's own category, placed where nothing is and given the
    /// role. Not the indicator's type itself: a marker of ours is excluded from the read before the
    /// role is ever asked, which is right, and would make the case about nothing.
    /// </remarks>
    private static void WarnsOfABoxJoinedToNothing(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());
        var symbol = NeedsIndicatorFamily(document, project);

        Skip.When(
            Array.IndexOf(RoleCategories, CategoryOf(symbol)) < 0,
            "the indicator family is not in a category the junction box role can be set on, so no other type of that category can be made a box");

        var before = Prepare(watch, document, application, symbol, project, catalogue);
        var other = NeedsAnotherType(context, document, symbol, "box joined to nothing");
        var level = LowestLevel(document);

        var box = PlaceLoose(
            watch,
            document,
            other,
            Clearing(before) + new XYZ(60, 0, 0),
            level,
            "a box joined to nothing",
            one => SetText(document.GetElement(one.GetTypeId()), CablingParameters.ElementRole, CablingParameters.JunctionBoxRole, "role"));

        var boxId = box.Id.Value;

        Skip.When(
            ConnectorsOf(box).Count == 0,
            "the other type of the indicator's category has no connector, so it could never be joined to anything and says nothing about a box that is not");

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 2, project.Boxes, project.DefaultConnection);

        var expected = snapshot.BoxesUnconnectedIds;

        Expect.That(
            expected.Contains(boxId),
            "element " + boxId + ", whose type says JunctionBox and which is joined to nothing, was not named by the read, so the apply had nothing to warn about");

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(run, snapshot);

        var outcome = ApplyWatched(context, watch, "box joined to nothing", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.JunctionBoxJoinedToNothing)).ToList();

        Note(context, "box joined to nothing: boxes named", expected.Count);
        Note(context, "box joined to nothing: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());

        Expect.Same(
            1,
            mine.Count(one => one.Elements.Count == 1 && one.Elements[0] == boxId),
            "JunctionBoxJoinedToNothing warnings Revit processed against element " + boxId + ", whose type says JunctionBox and which is joined to nothing");

        Expect.Same(
            expected.Count,
            mine.Count,
            "JunctionBoxJoinedToNothing warnings Revit processed, against boxes the read named joined to nothing");

        EachAgainstOneOf(mine, expected, "JunctionBoxJoinedToNothing", "one box the read called joined to nothing");
    });

    /// <summary>
    /// Runs a case with one failure watch open for all of it, and undoes what it wrote while the watch
    /// is still open.
    /// </summary>
    /// <remarks>
    /// See the class remarks. The group here is inside the harness's own, which is then left with
    /// nothing to roll back; a case that throws, skips or fails is undone the same way, in the
    /// <c>finally</c>, before the watch lets go.
    /// </remarks>
    private static void Watched(RevitTestContext context, Action<PostedWarnings> body)
    {
        using var watch = PostedWarnings.Watch(context.Application.Application);
        using var undone = new TransactionGroup(context.Document!, "BHS test: undone while failures are watched");

        undone.Start();

        try
        {
            body(watch);
        }
        finally
        {
            if (undone.GetStatus() == TransactionStatus.Started)
                undone.RollBack();
        }
    }

    private static ApplyOutcome ApplyWatched(
        RevitTestContext context,
        PostedWarnings watch,
        string label,
        RouteRun run,
        CablingSnapshot snapshot,
        CablingProjectSettings project,
        CarrierCatalogue catalogue) =>
        ApplyWatched(context, watch, label, run, snapshot, project, catalogue, out _);

    /// <summary>
    /// The production apply, with what Revit processed while it ran, and the three things every case
    /// needs to be true of it before asserting anything of its own.
    /// </summary>
    /// <remarks>
    /// <b>Failures that are not the apply's own four are noted, by definition id, on every case.</b>
    /// Each was dismissed by the watch, and each is something the person pressing Apply would have
    /// seen in a window - placing an indicator where Revit objects, deleting one it minds. A test that
    /// hides them is quieter than the product. Noted rather than asserted until a canonical sweep has
    /// shown what the ordinary number is; ids and not text, because the text is Revit's and names
    /// things in the owner's model.
    /// </remarks>
    private static ApplyOutcome ApplyWatched(
        RevitTestContext context,
        PostedWarnings watch,
        string label,
        RouteRun run,
        CablingSnapshot snapshot,
        CablingProjectSettings project,
        CarrierCatalogue catalogue,
        out IReadOnlyList<ProcessedFailure> processed)
    {
        var mark = watch.Mark;
        var outcome = CablingApply.Apply(context.Document!, context.Application.Application, run, snapshot, project, catalogue);

        processed = watch.Since(mark);

        var foreign = processed.Where(one => !one.IsCabling).ToList();

        context.Note(
            label + ": other failures while applying",
            foreign.Count == 0
                ? "0"
                : foreign.Count.ToString(CultureInfo.InvariantCulture) + " - "
                  + string.Join(", ", foreign.Select(one => one.Definition.ToString()).Distinct()));

        Note(context, label + ": warnings dismissed while applying", processed.Count(one => one.Severity == FailureSeverity.Warning));

        Expect.That(
            watch.Fault is null,
            "the test's own failure handler threw, so what Revit did with the apply's failures is unknown: " + Describe(watch.Fault));

        var worse = processed.Where(one => one.Severity != FailureSeverity.Warning).ToList();

        Expect.Same(
            0,
            worse.Count,
            "failures worse than a warning while applying, which the handler rolled back: " + string.Join("; ", worse));

        Expect.That(
            !outcome.Refused,
            "applying refused on a model that has the indicator family: " + string.Join(" ", outcome.Refusals));

        return outcome;
    }

    /// <summary>
    /// First, before any case asserts about one warning: what the apply reports posting is what Revit
    /// processed.
    /// </summary>
    /// <remarks>
    /// First because of what a shortfall means. This watch subscribes inside the case, so any other
    /// add-in's handler subscribed at startup runs before it - and a handler that deletes warnings
    /// leaves this one seeing fewer. Asserted later, that would read as the apply posting nothing.
    /// </remarks>
    private static void SawWhatWasPosted(ApplyOutcome outcome, IReadOnlyList<ProcessedFailure> cabling) =>
        Expect.Same(
            outcome.Warnings,
            cabling.Count,
            "cabling warnings Revit processed, against warnings the apply reports posting - fewer means something dismissed them before this handler ran, another add-in's FailuresProcessing handler for one");

    private static void OnePerCircuit(
        IReadOnlyList<ProcessedFailure> mine,
        IReadOnlyList<long> blocked,
        string definition,
        string why,
        string which)
    {
        foreach (var id in blocked)
        {
            Expect.Same(
                1,
                mine.Count(one => one.Elements.Count == 1 && one.Elements[0] == id),
                definition + " warnings Revit processed against circuit " + id + ", " + why);
        }

        Expect.Same(blocked.Count, mine.Count, definition + " warnings Revit processed, against " + which);

        EachAgainstOneOf(mine, blocked, definition, "one circuit the run could not route");
    }

    private static void EachAgainstOneOf(
        IReadOnlyList<ProcessedFailure> mine,
        IReadOnlyList<long> allowed,
        string definition,
        string what)
    {
        var wrong = mine
            .Where(one => one.Severity != FailureSeverity.Warning
                          || one.Elements.Count != 1
                          || !allowed.Contains(one.Elements[0]))
            .ToList();

        Expect.That(
            wrong.Count == 0,
            "a " + definition + " failure was not a warning, or named something other than " + what + ": "
            + string.Join("; ", wrong));
    }

    /// <summary>
    /// Stands a case down when the apply would post a warning this session has no definition for.
    /// </summary>
    /// <remarks>
    /// <c>new FailureMessage(id)</c> throws for an id nobody registered, inside the apply's own
    /// transaction, and the case would report "threw before it could assert". The definitions are
    /// created only by an edition that lists the cabling module, at startup - the probe that hosts
    /// these cases lists no such module - so a sweep without an edition installed beside the probe
    /// cannot put this question, and says so. The registry is static, measured by the compiler.
    /// </remarks>
    private static void NeedsDefinitionsFor(RouteRun run, CablingSnapshot snapshot)
    {
        var posting = new List<(FailureDefinitionId Id, string Name)>();

        if (run.Count(RouteStatus.NoCarrierNear) > 0)
            posting.Add((CablingFeature.NoCarrierNear, nameof(CablingFeature.NoCarrierNear)));

        if (run.Count(RouteStatus.NoConnectivity) > 0)
            posting.Add((CablingFeature.NoConnectivity, nameof(CablingFeature.NoConnectivity)));

        if (snapshot.Circuits.UnreadableConnectionIds.Count > 0)
            posting.Add((CablingFeature.ConnectionUnreadable, nameof(CablingFeature.ConnectionUnreadable)));

        if (snapshot.BoxesUnconnectedIds.Count > 0)
            posting.Add((CablingFeature.JunctionBoxJoinedToNothing, nameof(CablingFeature.JunctionBoxJoinedToNothing)));

        var registry = RevitApplication.GetFailureDefinitionRegistry();
        var missing = posting.Where(one => registry.FindFailureDefinition(one.Id) is null).Select(one => one.Name).ToList();

        Skip.When(
            missing.Count > 0,
            "the apply would post " + string.Join(", ", missing) + ", and this session has no such failure definition - "
            + "an edition that lists CablingFeature registers them at startup, and the sweep installs one beside the probe "
            + "only with --edition or where one is already installed");
    }

    private static FamilySymbol NeedsIndicatorFamily(Document document, CablingProjectSettings project)
    {
        var symbol = project.Boxes.In(document);

        Skip.When(
            symbol is null,
            "the model this sweep opened does not hold the indicator family, which the apply phase places");

        return symbol!;
    }

    /// <summary>
    /// Stands a placement case down on a model that already holds indicators of ours.
    /// </summary>
    /// <remarks>
    /// The apply matches what stands and removes what it no longer wants, so on such a model a new
    /// indicator, a matched one and a removed one cannot be told apart by reading the model - and the
    /// accounting these cases assert would fail on correct code. Recognised as broadly as the apply
    /// recognises its own: trimmed, and in any case.
    /// </remarks>
    private static void NeedsNoIndicatorsOfOurs(RevitTestContext context, Document document, FamilySymbol symbol, string label)
    {
        var standing = IndicatorsOf(document, symbol).Count(one => string.Equals(
            one.get_Parameter(CablingParameters.Recommendation)?.AsString()?.Trim(),
            CablingApply.RecommendsJunctionBox,
            StringComparison.OrdinalIgnoreCase));

        Note(context, label + ": indicators of ours already in the model", standing);

        Skip.When(
            standing > 0,
            "the model this sweep opened already holds indicators of ours, so placing cannot be told apart from matching and removal");
    }

    /// <summary>Another type of the indicator's category, preferring the indicator's own family.</summary>
    private static FamilySymbol NeedsAnotherType(RevitTestContext context, Document document, FamilySymbol symbol, string label)
    {
        // Compared as numbers: ElementId's own equality operator is not promised to take a null, and
        // a symbol's family or category can be one.
        var category = symbol.Category?.Id.Value;
        var family = symbol.Family?.Id.Value;

        var others = new FilteredElementCollector(document)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(one => one.Id.Value != symbol.Id.Value && category is not null && one.Category?.Id.Value == category)
            .ToList();

        var other = others.FirstOrDefault(one => family is not null && one.Family?.Id.Value == family) ?? others.FirstOrDefault();

        Skip.When(
            other is null,
            "the model this sweep opened has no other type in the indicator's category, which the case places");

        // Which, and not by name: the names are the owner's.
        context.Note(
            label + ": the other type is of",
            family is not null && other!.Family?.Id.Value == family ? "the indicator's family" : "another family");

        return other!;
    }

    /// <summary>Writes our shared parameter files, binds with the indicator's category, and reads.</summary>
    private static CablingSnapshot Prepare(
        PostedWarnings watch,
        Document document,
        RevitApplication application,
        FamilySymbol symbol,
        CablingProjectSettings project,
        CarrierCatalogue catalogue)
    {
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        return CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);
    }

    /// <summary>
    /// Writes our shared parameter files and binds through the production path, and stands the case
    /// down when that binding was rolled back.
    /// </summary>
    /// <remarks>
    /// <b>Asked of the watch, because the binding path does not say.</b> <c>Install</c> opens and
    /// commits a transaction of its own and returns what it bound, not what the commit came to - and
    /// inside a watch, a commit that raised something worse than a warning is rolled back without a
    /// word on screen. The case would then carry on against a document with nothing bound and fail at
    /// its first write, with a message blaming the binding path for an error the watch swallowed.
    /// </remarks>
    private static void Bind(PostedWarnings watch, Document document, RevitApplication application, RuntimeCategories? runtime)
    {
        var scheme = new CablingParameters();

        scheme.Export(application);

        var mark = watch.Mark;

        scheme.Install(document, application, runtime);
        NothingWorse(watch, mark, "binding the shared parameters");
    }

    /// <summary>
    /// Sets every described circuit's own connection to JunctionBox, and says how many.
    /// </summary>
    /// <remarks>
    /// <b>On the circuit itself</b>, the first rung of the owner's chain: the mode then depends neither
    /// on what a panel says nor on the project's default - the connection suite has those - and it is
    /// the same on a model whose panels already say Terminal.
    /// </remarks>
    private static int CutEveryCircuitInBoxes(PostedWarnings watch, Document document, RevitApplication application)
    {
        Bind(watch, document, application, runtime: null);

        var described = new CircuitReader().Read(document).Described;
        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: every circuit cut in boxes"))
        {
            transaction.Start();

            foreach (var circuit in described)
            {
                SetText(
                    document.GetElement(new ElementId(circuit.Id.Value)),
                    CablingParameters.CircuitConnection,
                    CablingParameters.ConnectionAtJunctionBox,
                    "connection");
            }

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "cutting every circuit in boxes");
        return described.Count;
    }

    /// <summary>What the command computes before Apply is pressed, with only the found routes in the run.</summary>
    /// <remarks>
    /// The command's own lines, and the boxes planned from every result as the command plans them -
    /// the planner reads only the found ones anyway. See the class remarks for why the failures are
    /// left out of the run.
    /// </remarks>
    private static (CablingSnapshot Snapshot, IReadOnlyList<RouteResult> Results, RouteRun Run) PlanFound(
        Document document,
        CablingProjectSettings project,
        CarrierCatalogue catalogue)
    {
        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var results = snapshot.Circuits.Described.Select(circuit => Router.Route(snapshot.Network, circuit, Options)).ToList();
        var found = results.Where(one => one.Status == RouteStatus.Found).ToList();

        var run = new RouteRun(found, snapshot.Network.Version, TimeSpan.Zero)
        {
            Boxes = BoxPlanner.Plan(results, snapshot.Boxes, project.BoxRadius),
        };

        return (snapshot, results, run);
    }

    /// <summary>Why a plan recommends no box, as the middle of a skip reason; call only when it recommends none.</summary>
    /// <remarks>
    /// Chosen from the plan rather than written once, because the causes send somebody to different
    /// places. No route found points at the routing; routes found and every tap served by a box
    /// already in the model points at the model's boxes - which on a model whose fitting types carry
    /// the role is the ordinary answer, not a fault of the routing.
    /// </remarks>
    private static string WhyNothingIsRecommended((CablingSnapshot Snapshot, IReadOnlyList<RouteResult> Results, RouteRun Run) plan)
    {
        if (plan.Run.Found == 0)
            return "no circuit of the model this sweep opened routed";

        if (plan.Run.Boxes.Count > 0)
            return "routes were found and every tap was served by a junction box already in the model";

        return "the model this sweep opened gives no found route with a tap";
    }

    /// <summary>The parameters and categories the apply binds: the indicator's own for all three, every carrier's for the references.</summary>
    private static RuntimeCategories RuntimeFor(FamilySymbol symbol, CarrierCatalogue catalogue)
    {
        var indicator = CategoryOf(symbol);

        var runtime = new RuntimeCategories()
            .Add(CablingParameters.Recommendation, indicator)
            .Add(CablingParameters.TapCount, indicator)
            .Add(CablingParameters.CircuitRefs, indicator);

        foreach (var category in catalogue.Categories)
            runtime.Add(CablingParameters.CircuitRefs, category);

        return runtime;
    }

    /// <summary>
    /// Places a free-standing instance in a transaction of its own, and stands the case down unless it
    /// committed, raised nothing worse than a warning, and is joined to nothing.
    /// </summary>
    private static FamilyInstance PlaceLoose(
        PostedWarnings watch,
        Document document,
        FamilySymbol type,
        XYZ at,
        Level level,
        string what,
        Action<FamilyInstance> dress)
    {
        var mark = watch.Mark;
        long placed;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: " + what))
        {
            transaction.Start();

            if (!type.IsActive)
                type.Activate();

            var instance = Place(document, type, at, level, what);
            dress(instance);

            placed = instance.Id.Value;
            status = transaction.Commit();
        }

        Committed(watch, mark, status, "placing " + what + " outside the model's extents");

        var element = document.GetElement(new ElementId(placed)) as FamilyInstance;

        Skip.When(element is null, what + ", placed outside the model's extents, is not in the model after the commit");

        var joint = Joint(element!);

        Skip.When(
            joint is not null,
            what + ", placed 100 ft outside every carrier and circuit end, " + joint + ", so it cannot stand for one joined to nothing");

        return element!;
    }

    /// <summary>The call the apply makes to place an indicator, with Revit's refusal made a skip.</summary>
    private static FamilyInstance Place(Document document, FamilySymbol type, XYZ at, Level level, string what)
    {
        FamilyInstance? instance = null;
        string? refusal = null;

        try
        {
            instance = document.Create.NewFamilyInstance(at, type, level, StructuralType.NonStructural);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException refused)
        {
            refusal = Describe(refused);
        }

        Skip.When(refusal is not null, "Revit refused to place " + what + ": " + refusal);
        Skip.When(instance is null, "Revit placed nothing for " + what);

        return instance!;
    }

    /// <summary>Stands a case down when a transaction it built its question in did not go through.</summary>
    /// <remarks>
    /// Worse than a warning is asked first: the watch rolls such a transaction back, and "did not
    /// commit (RolledBack)" would then name the consequence rather than the cause. A fault in the
    /// watch itself is a failure, not a skip - it is this suite's own code.
    /// </remarks>
    private static void Committed(PostedWarnings watch, int mark, TransactionStatus status, string what)
    {
        NothingWorse(watch, mark, what);
        Skip.When(status != TransactionStatus.Committed, what + " did not commit (" + status + ")");
    }

    /// <summary>
    /// Stands a case down when what it built raised a failure worse than a warning since the mark,
    /// which the watch will have rolled back; fails it when the watch itself threw.
    /// </summary>
    private static void NothingWorse(PostedWarnings watch, int mark, string what)
    {
        Expect.That(
            watch.Fault is null,
            "the test's own failure handler threw while " + what + ": " + Describe(watch.Fault));

        var worse = watch.Since(mark).Count(one => one.Severity != FailureSeverity.Warning);

        Skip.When(worse > 0, what + " raised " + worse + " failure(s) worse than a warning, which the watch rolled back");
    }

    private static void RecommendJunctionBox(Element element) =>
        SetText(element, CablingParameters.Recommendation, CablingApply.RecommendsJunctionBox, "recommendation");

    private static void SetText(Element? element, Guid parameter, string value, string what)
    {
        var found = element?.get_Parameter(parameter);

        Expect.That(
            found is { IsReadOnly: false },
            "element " + element?.Id.Value + " has no writable " + what + " parameter after binding through the production path");

        Expect.That(
            found!.Set(value),
            "element " + element!.Id.Value + " refused the " + what + " value written to it");
    }

    /// <summary>Every instance of the indicator type in the host, ours or not.</summary>
    /// <remarks>
    /// The type is filtered by Revit rather than tested in LINQ, so the host's other family instances
    /// are never expanded into managed elements - these cases ask it several times each.
    /// </remarks>
    private static List<FamilyInstance> IndicatorsOf(Document document, FamilySymbol symbol) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(FamilyInstance))
            .WherePasses(new FamilyInstanceFilter(document, symbol.Id))
            .Cast<FamilyInstance>()
            .ToList();

    /// <summary>
    /// Whether the element carries exactly what the apply writes - not the apply's own, more forgiving,
    /// recognition, so a case and the code under test do not agree by sharing it.
    /// </summary>
    private static bool CarriesOurRecommendation(Element? element) =>
        string.Equals(
            element?.get_Parameter(CablingParameters.Recommendation)?.AsString(),
            CablingApply.RecommendsJunctionBox,
            StringComparison.Ordinal);

    /// <summary>
    /// Host fittings of the two categories a role can be set on, other than the indicator type.
    /// </summary>
    /// <remarks>
    /// The two categories the role parameter is declared for, as the parameter scheme ships them; a
    /// fitting of any other carrier category has no role to set.
    /// </remarks>
    private static IEnumerable<FamilyInstance> HostFittings(Document document, FamilySymbol symbol)
    {
        foreach (var category in RoleCategories)
        {
            var fittings = new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fitting in fittings)
            {
                if (fitting.GetTypeId() != symbol.Id)
                    yield return fitting;
            }
        }
    }

    /// <summary>Revit's own answer about a joint to a carrier, written here and not taken from the reader.</summary>
    private static bool JoinsACarrier(FamilyInstance element, CarrierCatalogue catalogue)
    {
        foreach (var connector in ConnectorsOf(element))
        {
            if (connector.ConnectorType == ConnectorType.Logical || !connector.IsConnected)
                continue;

            foreach (Connector other in connector.AllRefs)
            {
                var owner = other?.Owner;

                if (owner is null || owner.Id == element.Id || owner.Category is null)
                    continue;

                if (catalogue.ClassOf((BuiltInCategory)owner.Category.Id.Value).Length > 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>What joins this element to anything, or nothing when it is joined to nothing.</summary>
    /// <remarks>
    /// <b>At least as broad as the apply's own question</b>, which asks every connector whether it is
    /// connected. A logical connector throws when asked; the apply would throw with it, and here it
    /// counts as a joint that cannot be ruled out, so the case stands down instead of failing for a
    /// reason of construction.
    /// </remarks>
    private static string? Joint(FamilyInstance element)
    {
        var index = 0;

        foreach (var connector in ConnectorsOf(element))
        {
            try
            {
                if (connector.IsConnected)
                    return "reports its connector " + index + " connected";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return "has a connector " + index + " that cannot say whether it is connected";
            }

            index++;
        }

        return null;
    }

    private static bool JoinedTo(FamilyInstance? element, long other)
    {
        if (element is null)
            return false;

        foreach (var connector in ConnectorsOf(element))
        {
            if (connector.ConnectorType == ConnectorType.Logical || !connector.IsConnected)
                continue;

            foreach (Connector far in connector.AllRefs)
            {
                if (far?.Owner is { } owner && owner.Id.Value == other)
                    return true;
            }
        }

        return false;
    }

    private static List<Connector> ConnectorsOf(Element element)
    {
        var manager = element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null,
        };

        var connectors = new List<Connector>();

        if (manager is null)
            return connectors;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector is not null)
                connectors.Add(connector);
        }

        return connectors;
    }

    /// <summary>A place beyond everything the read found, for an element that must be near nothing.</summary>
    /// <remarks>
    /// A hundred feet past the largest coordinate of every carrier point, panel and device, at the
    /// lowest height among them. Beyond the model's structure, not beyond its walls - which is enough
    /// for what it is for: nothing a joint or a tap could reach.
    /// </remarks>
    private static XYZ Clearing(CablingSnapshot snapshot)
    {
        var points = new List<Point3>();

        foreach (var carrier in snapshot.Carriers)
        {
            points.Add(carrier.Start);
            points.Add(carrier.End);
            points.AddRange(carrier.Terminals);
        }

        foreach (var circuit in snapshot.Circuits.Described)
        {
            points.Add(circuit.Source.At);
            points.AddRange(circuit.Devices.Select(one => one.At));
        }

        if (points.Count == 0)
            return new XYZ(100, 100, 0);

        return new XYZ(points.Max(one => one.X) + 100, points.Max(one => one.Y) + 100, points.Min(one => one.Z));
    }

    private static Level LowestLevel(Document document)
    {
        var level = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(one => one.Elevation)
            .FirstOrDefault();

        Skip.When(level is null, "the model this sweep opened has no level to place an indicator on");

        return level!;
    }

    /// <summary>The farthest any recommended box is from its nearest instance of the indicator type.</summary>
    /// <remarks>
    /// Over every instance of the type, with our recommendation or without: this is the number that
    /// explains a failure of "one of ours within the radius", and the case it explains includes the
    /// one where the recommendation never arrived.
    /// </remarks>
    private static string Farthest(IReadOnlyList<PlannedBox> wanted, IReadOnlyList<FamilyInstance> instances)
    {
        var points = instances.Select(PlaceOf).Where(one => one is not null).Select(one => one!).ToList();

        if (wanted.Count == 0 || points.Count == 0)
            return "none";

        var farthest = wanted.Max(box => points.Min(point => point.DistanceTo(Planned(box))));

        return farthest.ToString("F4", CultureInfo.InvariantCulture);
    }

    /// <summary>The recommendation and cable entry count on an element, as one comparable value.</summary>
    private static string ValuesOf(Element? element) =>
        "recommendation '" + Value(element, CablingParameters.Recommendation)
        + "', entries '" + Value(element, CablingParameters.TapCount) + "'";

    private static string Value(Element? element, Guid parameter)
    {
        var found = element?.get_Parameter(parameter);

        if (found is null || !found.HasValue)
            return string.Empty;

        return found.StorageType == StorageType.Integer
            ? found.AsInteger().ToString(CultureInfo.InvariantCulture)
            : found.AsString() ?? string.Empty;
    }

    private static XYZ? PlaceOf(FamilyInstance instance) => (instance.Location as LocationPoint)?.Point;

    private static bool Within(FamilyInstance instance, XYZ at, double radius) =>
        PlaceOf(instance) is { } point && point.DistanceTo(at) <= radius;

    private static XYZ Planned(PlannedBox box) => new(box.At.X, box.At.Y, box.At.Z);

    private static HashSet<long> Ids(IEnumerable<Element> elements) =>
        new(elements.Select(one => one.Id.Value));

    private static void Note(RevitTestContext context, string what, long value) =>
        context.Note(what, value.ToString(CultureInfo.InvariantCulture));

    private static string Describe(XYZ at) => string.Format(
        CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", at.X, at.Y, at.Z);

    private static string Describe(Exception? error) =>
        error is null ? "nothing" : error.GetType().Name + ": " + error.Message;

    private static BuiltInCategory CategoryOf(FamilySymbol symbol) =>
        (BuiltInCategory)symbol.Category.Id.Value;

    /// <summary>
    /// Settings that hold exactly what a case puts in them.
    /// </summary>
    /// <remarks>
    /// <b>Fifteen lines instead of loosening the production type, which is the better trade.</b>
    /// <c>CablingProjectSettings</c> is constructed only through <c>Read(ISettings)</c>, and the
    /// first thought was to add a constructor for tests - a change to shipped code so that a test
    /// could reach it. <c>ISettings</c> turns out to be an indexer, a key list and a section view, so
    /// the seam that already exists is enough, and nothing a customer receives changes shape to be
    /// testable.
    /// </remarks>
    private sealed class Fixed : ISettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get => _values.TryGetValue(key, out var found) ? found : null;
            set => _values[key] = value ?? string.Empty;
        }

        public IEnumerable<string> Keys => _values.Keys;

        public ISettings Section(string name)
        {
            var prefix = name + ":";
            var section = new Fixed();

            foreach (var pair in _values)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    section[pair.Key.Substring(prefix.Length)] = pair.Value;
            }

            return section;
        }
    }

    /// <summary>
    /// Not the command's: those defaults live in the feature assembly, which this one cannot see.
    /// </summary>
    /// <remarks>
    /// Every assertion in this suite holds whatever these two distances are, so they are deliberately
    /// not copied into a second literal that would have to be kept in step with the command.
    /// </remarks>
    private static RoutingOptions Options { get; } = new()
    {
        JoinTolerance = 0.5,
        MaxApproach = 10,
        AxisAlignedApproach = true,
    };
}
