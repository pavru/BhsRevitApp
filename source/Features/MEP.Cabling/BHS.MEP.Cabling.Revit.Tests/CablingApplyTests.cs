using System.Globalization;
using System.Reflection;
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
/// <b>What the owner decided on 2026-09-14, and these cases now pin.</b> Three questions this suite
/// used to leave open, and each is asserted rather than noted from here on.
/// </para>
/// <list type="bullet">
/// <item><c>BHS_Cbl_CircuitRefs</c> is one format on an indicator, a carrier and a box alike: circuit
/// element ids, each once, separated by "; ". The cases read the value back into ids and compare them
/// with the circuits the plan puts through the element, in the order the plan met them - on all three
/// writers alike. The separator is repeated here rather than taken from the apply, so the two do not
/// agree by sharing one constant.</item>
/// <item>An indicator of ours that somebody joined is left untouched, never rewritten and never
/// removed, and a warning is posted against it - also where it stands on a box this run recommends,
/// which it then stands for, with no second indicator placed beside it.</item>
/// <item>A transaction Revit does not keep means nothing was applied: a refusal naming what Revit
/// returned, and no work counted. Put by the watch rolling the apply's own transaction back.</item>
/// </list>
/// <para>
/// Whether a warning outlives its dismissal is no longer asked: the owner accepts Revit's reference for
/// <c>Document.PostFailure</c>, "warnings posted via this method will not be stored in the document
/// after they are resolved", and these cases dismiss every one they see.
/// </para>
/// <para>
/// <b>What these cases do not settle, named so it is not read as settled.</b>
/// </para>
/// <list type="bullet">
/// <item>Whether Revit keeps the point an indicator was placed at, height included. Not measured;
/// the placement case notes the distance before it asserts anything, so a failure there says which.</item>
/// <item>That a rollback asked for from a <c>FailuresProcessing</c> handler comes back from
/// <c>Commit</c> as <c>RolledBack</c>, silently. Not measured; the rollback case notes the status
/// before it asserts anything, and asserts only that it is not <c>Committed</c>.</item>
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
            "applying never narrows a binding: a category the parameter already covers stays covered, with the values written on it",
            KeepsWhatABindingAlreadyCovers,
            needsDocument: true,
            writes: true),

        new RevitTestCase(
            "an indicator family this model does not have is refused by name, not passed over",
            RefusesAMissingFamily,
            needsDocument: true,
            writes: true),

        new RevitTestCase(
            "an apply whose transaction Revit rolls back is refused with the status Revit returned, counts no work and leaves no indicator of ours",
            RefusesWhatRevitRolledBack,
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
            "every circuit that routed is told its length, the connection it was routed with, and the carriers it was measured along",
            CircuitsAreToldTheirRoute,
            writes: true),

        new RevitTestCase(
            "every circuit that routed is told how its length is laid - in trays, in conduits, in no carrier, in other carriers - and its slack, and the five add up to its length",
            CircuitsAreToldWhereTheirLengthIsLaid,
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
            "an indicator of ours joined into the network where a box is recommended is left untouched, gets no second indicator beside it, and is named in a warning",
            LeavesAJoinedIndicatorWhereABoxIsRecommended,
            writes: true),

        new RevitTestCase(
            "where a box is recommended, a joined indicator of ours takes it over a nearer one joined to nothing, which is removed",
            PrefersAJoinedIndicatorAtABox,
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

        new RevitTestCase(
            "routed without additional boxes, every device is served from a junction box already in the model, and no indicator is placed",
            ServesFromExistingBoxesOnly,
            writes: true),

        new RevitTestCase(
            "routed without additional boxes, every circuit with a device no existing box reaches is posted as one warning",
            WarnsOfNoBoxReachable,
            writes: true),

        new RevitTestCase(
            "applying writes the mode a run was computed with into the project inside its own transaction, and only when the project said otherwise",
            WritesTheModeItWasComputedWith,
            writes: true),
    };

    /// <summary>The value this suite writes wherever it wants a circuit's connection to fail to read.</summary>
    private const string Mistyped = "Junkbox";

    /// <summary>What separates two circuit ids in <c>BHS_Cbl_CircuitRefs</c>, the owner's one format.</summary>
    /// <remarks>
    /// Written again here rather than read from the apply, which keeps its helper private and should:
    /// a case that took the separator from the code under test would pass whatever that code wrote.
    /// </remarks>
    private const string RefsSeparator = "; ";

    /// <summary>The name the apply gives the transaction it writes the run in.</summary>
    /// <remarks>
    /// Repeated from <c>CablingApply</c>, which does not publish it, and the rollback case is the only
    /// reader. If the two drift, the watch never sees a transaction of this name, and that case fails
    /// saying so and naming the transactions it did see - not as a rollback that did nothing.
    /// </remarks>
    private const string ApplyTransaction = "BHS: apply cabling";

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

    /// <remarks>
    /// <para>
    /// <b>The owner's decision of 2026-09-17, pinned.</b> Red run 1 found it by the way: rebinding a
    /// parameter to another category set dropped the values already written - "entries '1'" before the
    /// apply, an empty string after. The apply rebinds whenever the parameter does not cover the
    /// indicator's category, so a project that moves the indicator to a family of another category
    /// would lose our recommendation on every indicator standing in the model, and with it the fact that
    /// they are ours.
    /// </para>
    /// <para>
    /// <b>Built on circuits, not on a second indicator family.</b> The question is about the binding,
    /// not the family: bind the recommendation where the apply does not want it, write a value there, and
    /// let the apply rebind for the indicator's category. Circuits are in every model this suite runs on
    /// and are never the indicator's category; the value written is not a word the apply ever writes, so
    /// finding it afterwards cannot be the apply's own doing.
    /// </para>
    /// </remarks>
    private static void KeepsWhatABindingAlreadyCovers(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);
        const BuiltInCategory Earlier = BuiltInCategory.OST_ElectricalCircuit;
        const string Kept = "written by the case before the apply";

        Skip.When(CategoryOf(symbol) == Earlier, "the indicator family is itself a circuit category, so no other category can stand for an earlier one");

        var scheme = new CablingParameters();
        scheme.Export(application);

        // What the document binds before the case touches it: a model already bound on circuits would
        // make the first Install below a no-op, and the case would prove nothing about widening.
        var boundBefore = BoundCategories(document, CablingParameters.Recommendation);
        context.Note("recommendation bound before the case to", boundBefore.Count == 0 ? "(nothing)" : string.Join(", ", boundBefore));

        Skip.When(
            boundBefore.Contains(CategoryOf(symbol)),
            "the model already binds the recommendation on the indicator's category, so the apply would not rebind it and the case would ask nothing");

        var circuit = new FilteredElementCollector(document)
            .OfCategory(Earlier)
            .WhereElementIsNotElementType()
            .FirstElement();

        Skip.When(circuit is null, "the model has no electrical circuit to hold a value on the earlier category");

        // The earlier binding: the recommendation on circuits only, as a project whose indicator
        // family used to be of that category would have it.
        var mark = watch.Mark;
        var earlier = scheme.Install(document, application, new RuntimeCategories().Add(CablingParameters.Recommendation, Earlier), out var installed);

        NothingWorse(watch, mark, "binding the recommendation on circuits");
        Skip.When(installed is { } status && status != TransactionStatus.Committed, "binding the recommendation on circuits did not commit (" + installed + ")");
        Expect.That(
            BoundCategories(document, CablingParameters.Recommendation).Contains(Earlier),
            "the recommendation is not bound on circuits after Install was asked for exactly that; Install reported "
            + earlier.Count + " parameter(s) bound");

        mark = watch.Mark;
        TransactionStatus wrote;

        using (var transaction = new Transaction(document, "BHS test: a value on the earlier category"))
        {
            transaction.Start();
            SetText(circuit, CablingParameters.Recommendation, Kept, "recommendation");
            wrote = transaction.Commit();
        }

        Committed(watch, mark, wrote, "writing a recommendation on a circuit");

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);
        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(document, symbol, run, snapshot);
        ApplyWatched(context, watch, "rebinding", run, snapshot, project, catalogue);

        var boundAfter = BoundCategories(document, CablingParameters.Recommendation);
        context.Note("recommendation bound after the apply to", string.Join(", ", boundAfter));

        // The rebinding itself is the premise: without it the case would prove only that nothing
        // happened. The indicator's category is what the apply wants and circuits did not cover it.
        Expect.That(
            boundAfter.Contains(CategoryOf(symbol)),
            "the apply did not bind the recommendation on the indicator's category " + CategoryOf(symbol)
            + ", so it never rebound and the case asked nothing");

        Expect.That(
            boundAfter.Contains(Earlier),
            "the apply rebound the recommendation without circuits, which it already covered: now bound to "
            + string.Join(", ", boundAfter));

        var value = document.GetElement(circuit!.Id)?.get_Parameter(CablingParameters.Recommendation)?.AsString();

        // Both sides named, as Expect.Same would name them for numbers.
        Expect.That(
            value == Kept,
            "the recommendation written on circuit " + circuit.Id.Value + " before the apply rebound the parameter: expected '"
            + Kept + "', got " + (value is null ? "no value" : "'" + value + "'"));
    });

    /// <summary>The categories a parameter is bound to in the document, by GUID, or none.</summary>
    private static List<BuiltInCategory> BoundCategories(Document document, Guid parameter)
    {
        var bound = new List<BuiltInCategory>();

        if (SharedParameterElement.Lookup(document, parameter) is not { } element
            || document.ParameterBindings.get_Item(element.GetDefinition()) is not ElementBinding binding)
        {
            return bound;
        }

        foreach (Category category in binding.Categories)
            bound.Add((BuiltInCategory)category.Id.Value);

        return bound;
    }

    private static void BindsWhatItWrites(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
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

        NeedsDefinitionsFor(document, symbol, run, snapshot);
        ApplyWatched(context, watch, "binding", run, snapshot, project, catalogue);

        // Asked of the document afterwards rather than taken from what Install reported, because the
        // question is what is bound now. The same runtime set the apply path composes.
        var missing = new CablingParameters()
            .Missing(document, RuntimeFor(symbol, catalogue))
            .Where(one => one.Id == CablingParameters.Recommendation
                          || one.Id == CablingParameters.CircuitRefs
                          || one.Id == CablingParameters.TapCount
                          || one.Id == CablingParameters.CableLength
                          || one.Id == CablingParameters.RouteConnection
                          || one.Id == CablingParameters.RouteStamp)
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

        var project = CablingProjectSettings.Read(new FixedSettings
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
    /// A write Revit declined to keep is reported as what it is: nothing applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's decision of 2026-09-14, put to the apply the only way a case can make Revit
    /// decline a commit.</b> The watch rolls the apply's own transaction back by name - see
    /// <see cref="PostedWarnings"/>. Whether its handler hears of a transaction with nothing to process
    /// is not measured, so that transaction is given something: a circuit holding a mistyped connection,
    /// which the apply posts a warning about inside it. That warning needs the failure definitions only
    /// an edition registers, and without them the case stands down by name.
    /// </para>
    /// <para>
    /// <b>The project says JunctionBox, and every circuit says it on itself</b> - except the mistyped
    /// one, which the read routes with the project's answer. So the circuit carrying the warning is
    /// planned in boxes like the rest, and the plan places something. The skip makes sure of that: an
    /// apply that would have placed nothing proves nothing by leaving nothing behind.
    /// </para>
    /// <para>
    /// <b>The order of the assertions is the order of the argument.</b> First that the watch was the
    /// reason: it saw the transaction and answered, and nothing worse than a warning was raised - an
    /// error rolls back without being asked, and the case would then pass for a reason that is not its
    /// own. Then that the rollback took: an apply that was not refused while new indicators stand means
    /// Revit kept the transaction despite the answer, and the apply reporting its work was then right -
    /// so that is named as this case's mechanism failing, before the refusal is asked for and a red
    /// would blame the production status check. Whether Revit honours <c>ProceedWithRollBack</c> for a
    /// processing that holds only warnings is not measured. Then the outcome: refused, a status that is
    /// not <c>Committed</c> named in the sentence, and every count zero. Then the model: no instance of
    /// the indicator type that was not there before, no indicator of ours, and every carrier on a found
    /// route holding the references it held before.
    /// </para>
    /// </remarks>
    private static void RefusesWhatRevitRolledBack(RevitTestContext context) => Watched(context, watch =>
    {
        // Before anything that can skip, so a sweep that stands the case down still runs it: see CountsOf.
        Expect.Same(
            typeof(ApplyOutcome).GetProperties(BindingFlags.Public | BindingFlags.Instance).Count(one => one.PropertyType == typeof(int)),
            CountsOf(null).Length,
            "int counts on ApplyOutcome, against the counts the rollback case checks - a count added to the outcome has to be added to CountsOf");

        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings
        {
            [CablingProjectSettings.ConnectionKey] = CablingParameters.ConnectionAtJunctionBox,
        });

        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "rolled back");
        CutEveryCircuitInBoxes(watch, document, application);

        var described = new CircuitReader().Read(document).Described;

        Skip.When(
            described.Count == 0,
            "the model this sweep opened describes no circuit, so none can give the apply a warning of its own to roll back on");

        var circuit = described[0].Id.Value;
        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: a mistyped connection to roll back on"))
        {
            transaction.Start();
            SetText(document.GetElement(new ElementId(circuit)), CablingParameters.CircuitConnection, Mistyped, "connection");
            status = transaction.Commit();
        }

        Committed(watch, mark, status, "writing a mistyped connection value");

        // With the runtime categories, so the apply finds nothing left to bind and the first
        // transaction it opens is the one the watch is waiting for.
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Count(box => box.IsRecommendation);

        Note(context, "rolled back: routes found", plan.Run.Found);
        Note(context, "rolled back: boxes recommended", wanted);

        Skip.When(
            !plan.Snapshot.Circuits.UnreadableConnectionIds.Contains(circuit),
            "circuit " + circuit + " holds '" + Mistyped + "' on itself and the read did not name it unreadable, so the apply would post nothing for the watch to roll back on - the unreadable connection case says why");

        Skip.When(
            wanted == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so an apply rolled back would have placed nothing whose absence could be asserted");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

        // Read before the apply, compared after it. Not printed on a difference: a value that stood
        // here before is the model's, and on a model applied before the format was settled it holds
        // circuit numbers, which can carry a panel's name.
        var hostPath = HostPath(plan.Run);
        var refsBefore = hostPath.ToDictionary(id => id, id => Value(document.GetElement(new ElementId(id)), CablingParameters.CircuitRefs));
        var before = Ids(IndicatorsOf(document, symbol));

        mark = watch.Mark;
        ApplyOutcome outcome;
        int answered;

        using (var request = watch.RollingBack(ApplyTransaction))
        {
            outcome = CablingApply.Apply(document, application, plan.Run, plan.Snapshot, project, catalogue);
            answered = request.Answered;
        }

        var processed = watch.Since(mark);

        context.Note("rolled back: what Revit returned", outcome.NotCommitted?.ToString() ?? "nothing");
        Note(context, "rolled back: rollbacks the watch answered", answered);
        Note(context, "rolled back: failures processed while applying", processed.Count);

        Expect.That(
            watch.Fault is null,
            "the test's own failure handler threw, so whether it rolled the apply back is unknown: " + Describe(watch.Fault));

        Expect.That(
            answered > 0,
            "the watch was asked to roll back '" + ApplyTransaction + "' and never saw a transaction of that name; transactions that processed failures while applying: "
            + TransactionsIn(processed));

        var worse = processed.Where(one => one.Severity != FailureSeverity.Warning).ToList();

        Expect.Same(
            0,
            worse.Count,
            "failures worse than a warning while applying, which roll a transaction back without being asked: " + string.Join("; ", worse));

        // Read before the outcome is judged, so that a rollback which did not take is named as this
        // case's own mechanism failing and not as the apply ignoring a status: see the remarks.
        var after = IndicatorsOf(document, symbol);
        var added = after.Where(one => !before.Contains(one.Id.Value)).Select(one => one.Id.Value).ToList();

        Expect.That(
            outcome.Refused || added.Count == 0,
            "the watch answered '" + ApplyTransaction + "' with a rollback " + answered + " time(s), yet Revit kept the transaction: "
            + added.Count + " new indicator(s) stand and the apply reports " + Claimed(outcome) + " - the test's rollback did not take");

        Expect.That(
            outcome.Refused,
            "an apply whose transaction Revit rolled back was not refused, and reports " + Claimed(outcome));

        Expect.That(
            outcome.NotCommitted is { } returned && returned != TransactionStatus.Committed,
            "an apply whose transaction Revit rolled back reports no status other than Committed: "
            + (outcome.NotCommitted?.ToString() ?? "nothing"));

        var named = outcome.NotCommitted!.Value.ToString();

        Expect.That(
            // IndexOf rather than Contains with a comparison, for net48: see the case above.
            outcome.Refusals.Any(one => one.IndexOf(named, StringComparison.Ordinal) >= 0),
            "the refusal does not name what Revit returned, " + named + ": " + string.Join(" ", outcome.Refusals));

        foreach (var (what, count) in CountsOf(outcome))
            Expect.Same(0, count, what + " reported by an apply whose transaction Revit rolled back");

        Expect.That(
            added.Count == 0,
            "instances of the indicator type standing after an apply Revit rolled back, that were not there before it: "
            + string.Join(", ", added));

        Expect.Same(
            0,
            after.Count(RecognisedAsOurs),
            "indicators of ours standing after an apply Revit rolled back, on a model that held none before it");

        var changed = hostPath
            .Where(id => !string.Equals(Value(document.GetElement(new ElementId(id)), CablingParameters.CircuitRefs), refsBefore[id], StringComparison.Ordinal))
            .ToList();

        Expect.That(
            changed.Count == 0,
            "carriers on a found route whose circuit references changed across an apply Revit rolled back: " + string.Join(", ", changed));
    });

    /// <summary>
    /// The plan's recommendations become indicators of ours, one each, where the plan put them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What keeps this from passing about nothing</b> is not the count of indicators the apply
    /// reports - it increments one of three counters per recommended box whatever happens, so that sum
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
    /// <b>Why routes were not found is noted before the case can stand down for want of them.</b> The
    /// attended run of 2026-09-14 on the linked set, Revit 2026, stood this case down exactly there: every
    /// circuit came back NoCarrierNear with every circuit cut in boxes. <see cref="ReachNotes"/> says which
    /// end stopped each one, how far that end is from the structure and by which measure, where its point
    /// came from, and whether a link's transform is to blame - as notes, which cannot change what this
    /// case asserts or when it stands down.
    /// </para>
    /// <para>
    /// <b>The same apply also tells the carriers their circuits, and that is asserted here too</b>,
    /// after the indicators so a placement defect is named first: every host carrier a found route
    /// walks names, in the one format, exactly the circuits the plan puts through it - the id of every
    /// found route that walks it, and of every circuit a used box standing on it serves; and a reference
    /// falls in a link exactly when a route or a used box passes through one. The case stands down
    /// before applying when every such host carrier already named something, or there is none because
    /// every carrier is linked - on such a model nothing separates a reference the apply wrote from one
    /// that stood there with the same ids.
    /// </para>
    /// <para>
    /// <b>The order is pinned on all three writers</b>, because the owner's rule of 2026-09-14 is one
    /// format with one separator and one ordering everywhere. An indicator names its box's circuits in
    /// the order the plan lists them, which is the order it met them. A carrier and a box already in the
    /// model name theirs first seen over the found routes' paths, then over the used boxes' circuits -
    /// the order <see cref="ThroughEachElement"/> builds from the plan itself. On an indicator the order
    /// says something only for a box serving two or more circuits, and the note of how many such boxes
    /// the plan has is what shows whether a sweep exercised it at all.
    /// </para>
    /// </remarks>
    private static void PlacesWhatThePlanRecommends(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "placement");

        var cut = CutEveryCircuitInBoxes(watch, document, application);
        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Where(box => box.IsRecommendation).ToList();

        Note(context, "placement: circuits cut in boxes", cut);
        Note(context, "placement: routes found", plan.Run.Found);
        Note(context, "placement: routes not found, left out of the run", plan.Results.Count - plan.Run.Found);
        Note(context, "placement: boxes recommended", wanted.Count);
        Note(context, "placement: recommended boxes serving more than one circuit", wanted.Count(box => box.Circuits.Count > 1));
        Note(context, "placement: boxes already in the model used", plan.Run.Boxes.Count - wanted.Count);

        // Before the skip, because the skip is where the answer is wanted: see the remarks.
        ReachNotes.Explain(
            context,
            "placement reach",
            document,
            plan.Snapshot.Network,
            plan.Snapshot.Circuits.Described,
            plan.Results,
            Options,
            catalogue,
            project.Boxes);

        Skip.When(
            wanted.Count == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so the plan recommends no box to place");

        // The carriers the found routes walk in the host, and what they named before the apply. Read
        // first, because a reference already standing on a carrier would otherwise answer for one
        // the apply never wrote.
        var hostPath = HostPath(plan.Run);

        var fresh = hostPath.Where(id => Value(document.GetElement(new ElementId(id)), CablingParameters.CircuitRefs).Length == 0).ToList();

        Note(context, "placement: carriers on found routes in the host", hostPath.Count);
        Note(context, "placement: of them already naming a circuit before the apply", hostPath.Count - fresh.Count);

        Skip.When(
            fresh.Count == 0,
            hostPath.Count == 0
                ? "every carrier the found routes walk lives in a link, so the apply has no carrier in the host to tell its circuits"
                : "every carrier the found routes walk in the host already names a circuit before the apply, so a reference it writes cannot be told from one that stood there");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

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
        // A joined indicator of ours standing on a box stands for it; on this model there is none, and
        // the sum still has to close with it in.
        Expect.Same(
            wanted.Count,
            outcome.Placed + outcome.Updated + outcome.JoinedInPlace,
            "boxes the plan recommends, against indicators the apply reports placing, finding in place, or finding joined in place");

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

            // The owner's one format, and on an indicator the plan's own order as well: see the remarks.
            ExpectCircuitRefs(
                indicator,
                "indicator " + id,
                box.Circuits.Select(one => one.Value).ToList(),
                inOrder: true);
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

        // The second of the apply's three writes: the carriers are told their circuits. Every host
        // carrier on a found route, not only the fresh ones - the apply overwrites what stood, so one
        // that named something before has to name exactly this after as well, and a value left in an
        // earlier format is a red here. A carrier Revit keeps read-only, which an element in a group
        // may be, is not excused: the apply skips such a write without a word, and a red here is the
        // only place that would say so. Not measured whether the models a sweep opens hold one.
        var through = ThroughEachElement(plan.Run);

        foreach (var id in hostPath)
        {
            ExpectCircuitRefs(
                document.GetElement(new ElementId(id)),
                "carrier " + id + " on a found route",
                through[id],
                inOrder: true);
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
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "second apply");

        CutEveryCircuitInBoxes(watch, document, application);

        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Count(box => box.IsRecommendation);

        Note(context, "second apply: boxes recommended", wanted);

        Skip.When(
            wanted == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so there is nothing for a second apply to find in place");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

        var first = ApplyWatched(context, watch, "second apply, first press", plan.Run, plan.Snapshot, project, catalogue);
        var standingFirst = Ids(IndicatorsOf(document, symbol).Where(CarriesOurRecommendation));

        var second = ApplyWatched(context, watch, "second apply, second press", plan.Run, plan.Snapshot, project, catalogue);
        var standingSecond = Ids(IndicatorsOf(document, symbol).Where(CarriesOurRecommendation));

        Note(context, "second apply: indicators placed by the first", first.Placed);
        Note(context, "second apply: indicators found in place by the second", second.Updated);

        Expect.Same(0, second.Placed, "indicators placed anew by a second apply of the same run");
        Expect.Same(0, second.Removed, "indicators removed by a second apply of the same run");

        Expect.Same(
            first.Placed + first.Updated + first.JoinedInPlace,
            second.Updated + second.JoinedInPlace,
            "indicators the first apply left standing, against the ones the second apply found in place");

        Expect.Same(first.Joined, second.Joined, "indicators left as joined by the first apply, against the second");

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
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "re-read");

        CutEveryCircuitInBoxes(watch, document, application);

        var plan = PlanFound(document, project, catalogue);

        Skip.When(
            !plan.Run.Boxes.Any(box => box.IsRecommendation),
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so the apply places nothing that could be read back");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

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
        var project = CablingProjectSettings.Read(new FixedSettings { [CablingProjectSettings.BoxRadiusKey] = RadiusMm });
        var symbol = NeedsIndicatorFamily(document, project);

        context.Note("existing box: box radius, mm", RadiusMm);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "existing box");

        // With the runtime categories, so the values compared before and after the apply are read
        // from the same bindings.
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        MarkJoinedFittingTypesBoxes(context, watch, document, symbol, catalogue, "existing box");
        CutEveryCircuitInBoxes(watch, document, application);

        var plan = PlanFound(document, project, catalogue);
        var used = plan.Run.Boxes.Where(box => box.Existing is not null).ToList();

        Note(context, "existing box: boxes in the model", plan.Snapshot.Boxes.Count);
        Note(context, "existing box: boxes in the model a tap used", used.Count);
        Note(context, "existing box: boxes recommended", plan.Run.Boxes.Count - used.Count);

        Skip.When(
            used.Count == 0,
            "with every circuit cut in boxes and every joined fitting type marked a box, no tap of the model this sweep opened comes within the box radius of one, so no box already in the model is used");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

        var inHost = used.Where(box => !box.Existing!.Id.IsLinked).Select(box => box.Existing!.Id.Value).Distinct().ToList();
        var valuesBefore = inHost.ToDictionary(id => id, id => ValuesOf(document.GetElement(new ElementId(id))));

        // Apart from the values that must not change, because this one must - noted, so a box that
        // named something before is visible as one whose reference the apply overwrote.
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

        // Exactly the circuits the plan puts through the box, in the owner's one format: the ones it
        // serves, and the ones whose route walks it as a carrier - a box is a fitting, and the same
        // element is both. Every used box, not only those that named nothing before: the apply
        // overwrites, so a value in an earlier format left standing is a red.
        var through = ThroughEachElement(plan.Run);

        foreach (var id in inHost)
        {
            var element = document.GetElement(new ElementId(id));

            ExpectCircuitRefs(
                element,
                "box " + id + ", already in the model and used by the plan,",
                through[id],
                inOrder: true);

            Expect.That(
                ValuesOf(element) == valuesBefore[id],
                "box " + id + ", already in the model, had its recommendation or cable entry count written: before "
                + valuesBefore[id] + ", after " + ValuesOf(element));
        }
    });

    /// <summary>
    /// Marks a box every fitting type of the host whose every instance is joined to a carrier, and stands
    /// the case down when there is none.
    /// </summary>
    /// <remarks>
    /// Every instance, so that marking makes no box joined to nothing. The join is asked by the suite's
    /// own code, not the reader's, so that a case and the code under test do not agree by construction.
    /// </remarks>
    private static void MarkJoinedFittingTypesBoxes(
        RevitTestContext context,
        PostedWarnings watch,
        Document document,
        FamilySymbol symbol,
        CarrierCatalogue catalogue,
        string label)
    {
        var joinedTypes = HostFittings(document, symbol)
            .GroupBy(one => one.GetTypeId().Value)
            .Where(group => group.All(one => JoinsACarrier(one, catalogue)))
            .Select(group => group.Key)
            .ToList();

        Note(context, label + ": fitting types marked a box", joinedTypes.Count);

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
    }

    /// <summary>
    /// Routed without additional boxes, a device is served from a box that stands, however far, and the
    /// apply adds nothing to the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's rules of 2026-09-17, asserted where the model can show them.</b> Every tap of a found
    /// route names a box the read found; the plan recommends nothing; the apply places no indicator. How
    /// far the boxes are from their taps is noted - that some are beyond the box radius is the whole point
    /// of the mode, and whether this model has such a tap is the model's to say.
    /// </para>
    /// <para>
    /// <b>Routed in memory, cut in boxes by the snapshot rather than by a parameter.</b> What the mode
    /// changes is the routing; the chain that decides a circuit's connection has its own case. Only the
    /// found routes go to the apply: the others would post a warning, which the next case proves.
    /// </para>
    /// </remarks>
    private static void ServesFromExistingBoxesOnly(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings { [CablingProjectSettings.ExistingBoxesOnlyKey] = "true" });
        var symbol = NeedsIndicatorFamily(document, project);

        Expect.That(project.ExistingBoxesOnly, "the project setting that asks for no additional boxes did not read back as asked");

        NeedsNoIndicatorsOfOurs(context, document, symbol, "existing only");
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));
        MarkJoinedFittingTypesBoxes(context, watch, document, symbol, catalogue, "existing only");

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var circuits = snapshot.Circuits.Described.Select(circuit => InMode(circuit, CircuitConnection.AtJunctionBox)).ToList();
        var results = circuits.Select(circuit => Router.Route(snapshot.Network, circuit, Options, snapshot.Boxes)).ToList();
        var found = results.Where(one => one.Status == RouteStatus.Found).ToList();

        Note(context, "existing only: boxes in the model", snapshot.Boxes.Count);
        context.Note("existing only: circuits by status", ReachNotes.ByStatus(results));

        Skip.When(
            snapshot.Boxes.Count == 0 || found.Count == 0,
            "with every joined fitting type marked a box, no circuit of the model this sweep opened has every device reached by a box through the structure, so nothing is served from one");

        var run = new RouteRun(found, snapshot.Network.Version, TimeSpan.Zero)
        {
            Boxes = BoxPlanner.Plan(found, snapshot.Boxes, project.BoxRadius),
            ExistingBoxesOnly = true,
        };

        var standing = snapshot.Boxes.ToDictionary(box => box.Id, box => box.At);
        var taps = found.SelectMany(route => route.Taps).ToList();

        Note(context, "existing only: taps served", taps.Count);
        Note(context, "existing only: taps farther from their box than the box radius",
            taps.Count(tap => tap.Box is { } box && standing.TryGetValue(box, out var at) && at.DistanceTo(tap.At) > project.BoxRadius));

        foreach (var route in found)
        {
            foreach (var tap in route.Taps)
            {
                Expect.That(
                    tap.Box is { } box && standing.ContainsKey(box),
                    "circuit " + route.Circuit + ": a tap of a route found without additional boxes names no box the read found - "
                    + (tap.Box?.ToString() ?? "none"));
            }
        }

        Expect.Same(0, run.Boxes.Count(box => box.IsRecommendation), "boxes the plan recommends, routed without additional boxes");
        Expect.Same(taps.Count, run.Boxes.Sum(box => box.Spurs), "spurs the plan's boxes serve, against the taps of the found routes");

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var before = IndicatorsOf(document, symbol).Select(one => one.Id.Value).ToList();
        var outcome = ApplyWatched(context, watch, "existing only", run, snapshot, project, catalogue);
        var placed = IndicatorsOf(document, symbol).Select(one => one.Id.Value).Except(before).ToList();

        Note(context, "existing only: boxes in the model the apply reports using", outcome.ExistingUsed);

        Expect.Same(0, outcome.Placed, "indicators the apply reports placing, routed without additional boxes");
        Expect.Same(0, placed.Count, "instances of the indicator type standing after the apply that did not stand before it");
    });

    /// <summary>
    /// Routed without additional boxes over a model with none, every circuit whose ends reach the
    /// structure fails for want of a box, and each is posted once.
    /// </summary>
    /// <remarks>
    /// No box at all, rather than the model's boxes, so that the condition does not depend on which of
    /// the owner's fittings could be marked: a device that reaches a carrier and no box reaches is the
    /// exact failure, and with no boxes every circuit whose ends reach the structure is one.
    /// </remarks>
    private static void WarnsOfNoBoxReachable(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings { [CablingProjectSettings.ExistingBoxesOnlyKey] = "true" });
        var symbol = NeedsIndicatorFamily(document, project);

        // See the NoCarrierNear case: the file the apply binds from is written by the case, not inherited.
        new CablingParameters().Export(context.Application.Application);

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var results = snapshot.Circuits.Described
            .Select(circuit => Router.Route(snapshot.Network, InMode(circuit, CircuitConnection.AtJunctionBox), Options, Array.Empty<ExistingBox>()))
            .ToList();

        var unserved = results.Where(one => one.Status == RouteStatus.NoBoxReachable).ToList();
        var run = new RouteRun(unserved, snapshot.Network.Version, TimeSpan.Zero) { ExistingBoxesOnly = true };
        var blocked = unserved.Select(one => one.Circuit.Value).ToList();

        context.Note("no box: circuits by status", ReachNotes.ByStatus(results));

        Expect.Same(0, results.Count(one => one.Status == RouteStatus.Found), "circuits routed without additional boxes over no box at all");

        Skip.When(
            blocked.Count == 0,
            "routed without additional boxes over no box at all, no circuit of the model this sweep opened has its ends reach the structure, so none fails for want of a box");

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "no box", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.NoBoxReachable)).ToList();

        Note(context, "no box: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());
        OnePerCircuit(mine, blocked, "NoBoxReachable", "a device of which no existing box reaches", "circuits no existing box could serve");
    });

    /// <summary>
    /// The mode travels from the run into the project inside the apply's own transaction, and only when
    /// the project said otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the apply promises, asserted through the writer it is handed.</b> The command hands it the
    /// framework's one-key write, whose own checks are in the probe; here the writer records what it was
    /// asked and whether a transaction was open when it was asked - which is the owner's decision of
    /// 2026-09-17, that a mode is never kept while the run it describes is rolled back.
    /// </para>
    /// <para>
    /// On an empty plan, the next press of a project with nothing to route: the mode is all there is to
    /// write, and nothing else of the apply stands in the way.
    /// </para>
    /// </remarks>
    private static void WritesTheModeItWasComputedWith(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var ordinary = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, ordinary);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "mode");

        var snapshot = Prepare(watch, document, application, symbol, ordinary, catalogue);
        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero) { ExistingBoxesOnly = true };

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var asked = new List<(string Key, string? Value, bool InTransaction)>();
        var mark = watch.Mark;

        var changed = CablingApply.Apply(
            document, application, run, snapshot, ordinary, catalogue,
            (key, value) => asked.Add((key, value, document.IsModifiable)));

        Expect.That(watch.Since(mark).All(one => one.Severity == FailureSeverity.Warning), "failures worse than a warning while applying the mode");
        Expect.That(!changed.Refused, "applying the mode refused: " + string.Join(" ", changed.Refusals));

        Expect.Same(1, asked.Count, "project settings the apply wrote, for a run computed without additional boxes in a project that said otherwise");
        Expect.That(
            asked.Count == 1 && asked[0].Key == CablingProjectSettings.ExistingBoxesOnlyKey && asked[0].Value == "true",
            "the apply wrote " + string.Join("; ", asked.Select(one => one.Key + " = " + (one.Value ?? "(cleared)")))
            + ", not " + CablingProjectSettings.ExistingBoxesOnlyKey + " = true");
        Expect.That(
            asked.All(one => one.InTransaction),
            "the apply wrote the mode with no transaction open, so it would be kept even when the run's transaction is not");
        Expect.That(changed.ModeWritten == true, "the apply does not report writing the mode it wrote");

        asked.Clear();

        var same = CablingProjectSettings.Read(new FixedSettings { [CablingProjectSettings.ExistingBoxesOnlyKey] = "true" });
        var unchanged = CablingApply.Apply(
            document, application, run, snapshot, same, catalogue,
            (key, value) => asked.Add((key, value, document.IsModifiable)));

        Expect.That(!unchanged.Refused, "applying the mode again refused: " + string.Join(" ", unchanged.Refusals));
        Expect.Same(0, asked.Count, "project settings the apply wrote, for a run computed in the mode the project already has");
        Expect.That(unchanged.ModeWritten is null, "the apply reports writing a mode the project already had");
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
        var project = CablingProjectSettings.Read(new FixedSettings());
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

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "removal", run, snapshot, project, catalogue);

        Note(context, "removal: indicators removed", outcome.Removed);
        Note(context, "removal: indicators left as joined", outcome.Joined);

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
    /// The guard that makes removal safe at all: an indicator somebody joined into the wiring stays -
    /// untouched, and warned about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The joint is built with a conduit drawn out of the indicator's own connector</b> - see
    /// <see cref="PlaceJoined"/> for how little of that is measured. Each step is a loud skip of its
    /// own, and a sweep that skips here has not covered this rule - which the record then says by name.
    /// </para>
    /// <para>
    /// <b>Untouched is asserted as well as standing</b>, the owner's decision of 2026-09-14. The case
    /// writes a cable entry count of its own on the indicator before joining it, so "unchanged" compares
    /// a value and not two empty ones. With a plan that names no box the apply has nothing to rewrite it
    /// with, which is why the rule is put again, by the case after this one, where a box is recommended.
    /// </para>
    /// <para>
    /// <b>The warnings are counted against this suite's own reading of the model</b>: every indicator of
    /// ours that is, or cannot say it is not, joined to something. On a model that already holds such
    /// indicators the apply warns about each of those too, and "exactly one" would fail on correct code.
    /// Exactly one is asserted only against the indicator the case joined.
    /// </para>
    /// </remarks>
    private static void LeavesAJoinedIndicator(RevitTestContext context) => Watched(context, watch =>
    {
        // Any count will do, so long as it is one the case wrote: see the remarks.
        const int Entries = 1;

        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);
        var snapshot = Prepare(watch, document, application, symbol, project, catalogue);

        var joined = PlaceJoined(
            context, watch, document, symbol, Clearing(snapshot), LowestLevel(document), "joined", "placed outside the model", Entries);

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var ours = JoinedIndicatorsOfOurs(document, symbol);
        var written = WrittenOn(document.GetElement(new ElementId(joined)));

        var outcome = ApplyWatched(context, watch, "joined", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.IndicatorJoinedIntoNetwork)).ToList();
        var element = document.GetElement(new ElementId(joined));

        Note(context, "joined: indicators left as joined", outcome.Joined);
        Note(context, "joined: indicators of ours this case reads as joined", ours.Count);
        Note(context, "joined: cabling warnings posted", outcome.Warnings);

        Expect.That(
            element is not null,
            "indicator " + joined + ", ours and joined to a conduit, was removed by an apply whose plan names no box");

        Expect.That(
            outcome.Joined >= 1,
            "the apply counted no indicator as left for being joined, though indicator " + joined + " is joined to a conduit");

        Expect.That(
            WrittenOn(element) == written,
            "indicator " + joined + ", ours and joined to a conduit, had what the apply writes changed: before "
            + written + ", after " + WrittenOn(element));

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());

        Expect.Same(
            1,
            mine.Count(one => one.Elements.Count == 1 && one.Elements[0] == joined),
            "IndicatorJoinedIntoNetwork warnings Revit processed against indicator " + joined + ", ours and joined to a conduit");

        Expect.Same(
            ours.Count,
            mine.Count,
            "IndicatorJoinedIntoNetwork warnings Revit processed, against indicators of ours this case reads as joined");

        EachAgainstOneOf(mine, ours, "IndicatorJoinedIntoNetwork", "one indicator of ours this case reads as joined");
    });

    /// <summary>
    /// Where a box is recommended and an indicator of ours already stands there joined into the network,
    /// that indicator stands for the box: nothing is placed beside it, nothing on it is rewritten, and a
    /// warning names it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's decisions of 2026-09-14, both of them.</b> A joined indicator of ours is never
    /// rewritten and never removed, and is warned about. And within the radius of a recommended box it
    /// takes that box's place, with no second indicator beside it, a joined one preferred over one joined
    /// to nothing - first a reading of the decision made while implementing it, confirmed by the owner the
    /// same day. This case pins the first half and the no-second-indicator part of the second; the
    /// preference between a joined and an unjoined indicator at one box is
    /// <see cref="PrefersAJoinedIndicatorAtABox"/>.
    /// </para>
    /// <para>
    /// <b>The plan is computed first, and the joined indicator stood on one of its boxes afterwards.</b>
    /// The other order would make the conduit the case draws a carrier of the plan, and a plan that moved
    /// with the construction could leave no box where the indicator stands. The order changes nothing the
    /// apply is asked: it reads what stands from the model when it runs, and the plan only for where the
    /// boxes go.
    /// </para>
    /// <para>
    /// <b>Placed on the level the apply would choose</b>, the nearest at or below the point, so whatever
    /// Revit does with the height of an instance placed on a level below it, it does here as it does to
    /// the apply's own. Whether it keeps the point is not measured: the distance is noted, and an
    /// indicator Revit moved out of the radius is a skip that says so.
    /// </para>
    /// <para>
    /// <b>Its values are chosen to differ from what the apply would write</b>: a cable entry count one
    /// more than the box's, and no circuit references. A rewrite changes both.
    /// </para>
    /// </remarks>
    private static void LeavesAJoinedIndicatorWhereABoxIsRecommended(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "joined at a box");

        CutEveryCircuitInBoxes(watch, document, application);

        // With the runtime categories, so the cable entry count the case writes has somewhere to go.
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Where(box => box.IsRecommendation).ToList();

        Note(context, "joined at a box: boxes recommended", wanted.Count);

        Skip.When(
            wanted.Count == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so there is no recommended box for a joined indicator of ours to stand on");

        var box = wanted[0];
        var at = Planned(box);

        var joined = PlaceJoined(
            context, watch, document, symbol, at, LevelAtOrBelow(document, at.Z), "joined at a box", "placed where a box is recommended", box.Entries + 1);

        var indicator = (FamilyInstance)document.GetElement(new ElementId(joined));

        context.Note(
            "joined at a box: distance of the joined indicator from its box, internal feet",
            PlaceOf(indicator) is { } point ? point.DistanceTo(at).ToString("F4", CultureInfo.InvariantCulture) : "no point");

        Skip.When(
            !Within(indicator, at, project.BoxRadius),
            "Revit did not keep indicator " + joined + " within the box radius of the recommended box it was placed at, so it cannot stand for that box");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

        var written = WrittenOn(indicator);

        // What stood before the apply, so "only the joined one stands at the box" is asked of what the
        // apply could have put there. An instance of the indicator type without our recommendation - a
        // hand-placed one - is the model's, and the apply rightly leaves it alone wherever it stands.
        var standingBefore = Ids(IndicatorsOf(document, symbol));

        var outcome = ApplyWatched(context, watch, "joined at a box", plan.Run, plan.Snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.IndicatorJoinedIntoNetwork)).ToList();
        var element = document.GetElement(new ElementId(joined));

        var near = IndicatorsOf(document, symbol)
            .Where(one => Within(one, at, project.BoxRadius)
                          && (!standingBefore.Contains(one.Id.Value) || one.Id.Value == joined))
            .Select(one => one.Id.Value)
            .ToList();

        Note(context, "joined at a box: indicators placed", outcome.Placed);
        Note(context, "joined at a box: joined indicators standing for a box", outcome.JoinedInPlace);
        Note(context, "joined at a box: cabling warnings posted", outcome.Warnings);

        Expect.That(
            element is not null,
            "indicator " + joined + ", ours and joined into the network where a box is recommended, was removed");

        Expect.Same(
            1,
            outcome.JoinedInPlace,
            "joined indicators of ours the apply reports standing for a recommended box, on a model whose only indicator of ours is joined and stands on one");

        Expect.Same(
            wanted.Count,
            outcome.Placed + outcome.Updated + outcome.JoinedInPlace,
            "boxes the plan recommends, against indicators the apply reports placing, finding in place, or finding joined in place");

        Expect.That(
            near.Count == 1 && near[0] == joined,
            "instances of the indicator type within the box radius of the box indicator " + joined + " stands on, new or that one, are ["
            + string.Join(", ", near) + "], where only that indicator should stand");

        Expect.That(
            WrittenOn(element) == written,
            "indicator " + joined + ", ours and joined where a box is recommended, was rewritten: before "
            + written + ", after " + WrittenOn(element));

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());

        Expect.Same(
            1,
            mine.Count(one => one.Elements.Count == 1 && one.Elements[0] == joined),
            "IndicatorJoinedIntoNetwork warnings Revit processed against indicator " + joined + ", ours and joined where a box is recommended");

        Expect.Same(
            1,
            mine.Count,
            "IndicatorJoinedIntoNetwork warnings Revit processed, on a model whose only indicator of ours is the joined one");

        EachAgainstOneOf(mine, new[] { joined }, "IndicatorJoinedIntoNetwork", "indicator " + joined);
    });

    /// <summary>
    /// Two indicators of ours stand within the radius of one recommended box, the unjoined one nearer:
    /// the joined one takes the box, and the unjoined one, left unclaimed, is removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's decision of 2026-09-14, the last part of it without a case until now.</b> A box
    /// asks for a joined indicator first and for one joined to nothing only when there is none.
    /// Nearest-first over both kinds - what the apply did before - lets the nearer unjoined one take the
    /// box: it is rewritten in place, the joined one stands unclaimed beside it, and the model is left
    /// with two marks of ours inside one radius.
    /// </para>
    /// <para>
    /// <b>The unjoined one stands exactly on the box and the joined one inside the radius but off it</b>,
    /// because only then is the unjoined one nearer, which is the whole question. The joined one is placed
    /// first, so the conduit drawn out of it exists when the other is placed; if Revit joins the second to
    /// anything, <c>PlaceLoose</c> stands the case down saying so.
    /// </para>
    /// <para>
    /// <b>The same order as the case before it</b>: the plan first, both indicators after, for the
    /// reason given there.
    /// </para>
    /// </remarks>
    private static void PrefersAJoinedIndicatorAtABox(RevitTestContext context) => Watched(context, watch =>
    {
        const string Label = "joined preferred";

        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, Label);

        CutEveryCircuitInBoxes(watch, document, application);
        Bind(watch, document, application, RuntimeFor(symbol, catalogue));

        var plan = PlanFound(document, project, catalogue);
        var wanted = plan.Run.Boxes.Where(box => box.IsRecommendation).ToList();

        Note(context, Label + ": boxes recommended", wanted.Count);

        Skip.When(
            wanted.Count == 0,
            "with every circuit cut in boxes, " + WhyNothingIsRecommended(plan) + ", so there is no recommended box for two indicators of ours to stand on");

        var box = wanted[0];
        var at = Planned(box);

        // Inside the radius and clearly off the box; along X, the direction the apply is told nothing about.
        var aside = at + new XYZ(project.BoxRadius * 0.6, 0, 0);

        var joined = PlaceJoined(
            context, watch, document, symbol, aside, LevelAtOrBelow(document, aside.Z), Label, "placed inside a recommended box's radius", box.Entries + 1);

        var loose = PlaceLoose(
            watch,
            document,
            symbol,
            at,
            LevelAtOrBelow(document, at.Z),
            "an indicator of ours joined to nothing, placed on a recommended box",
            instance =>
            {
                RecommendJunctionBox(instance);
                SetInteger(instance, CablingParameters.TapCount, box.Entries + 2, "cable entry count");
            }).Id.Value;

        var joinedIndicator = (FamilyInstance)document.GetElement(new ElementId(joined));
        var looseIndicator = (FamilyInstance)document.GetElement(new ElementId(loose));
        var joinedDistance = PlaceOf(joinedIndicator)?.DistanceTo(at);
        var looseDistance = PlaceOf(looseIndicator)?.DistanceTo(at);

        context.Note(
            Label + ": distances from the box, joined and joined to nothing, internal feet",
            (joinedDistance?.ToString("F4", CultureInfo.InvariantCulture) ?? "no point") + ", "
            + (looseDistance?.ToString("F4", CultureInfo.InvariantCulture) ?? "no point"));

        Skip.When(
            !Within(joinedIndicator, at, project.BoxRadius) || !Within(looseIndicator, at, project.BoxRadius),
            "Revit did not keep both indicators within the radius of the recommended box they were placed at");

        Skip.When(
            joinedDistance is null || looseDistance is null || looseDistance >= joinedDistance,
            "Revit did not keep the indicator joined to nothing nearer the box than the joined one, so the case would not ask which one the box prefers");

        NeedsDefinitionsFor(document, symbol, plan.Run, plan.Snapshot);

        var written = WrittenOn(joinedIndicator);
        var standingBefore = Ids(IndicatorsOf(document, symbol));

        var outcome = ApplyWatched(context, watch, Label, plan.Run, plan.Snapshot, project, catalogue);

        var near = IndicatorsOf(document, symbol)
            .Where(one => Within(one, at, project.BoxRadius) && (!standingBefore.Contains(one.Id.Value) || one.Id.Value == joined || one.Id.Value == loose))
            .Select(one => one.Id.Value)
            .ToList();

        Note(context, Label + ": indicators placed", outcome.Placed);
        Note(context, Label + ": indicators updated in place", outcome.Updated);
        Note(context, Label + ": joined indicators standing for a box", outcome.JoinedInPlace);
        Note(context, Label + ": indicators removed", outcome.Removed);

        Expect.Same(
            1,
            outcome.JoinedInPlace,
            "joined indicators of ours the apply reports standing for a recommended box, where a joined one and a nearer one joined to nothing stand on the same box");

        Expect.That(
            document.GetElement(new ElementId(joined)) is not null,
            "indicator " + joined + ", ours and joined, standing inside a recommended box's radius, was removed");

        Expect.That(
            WrittenOn(document.GetElement(new ElementId(joined))) == written,
            "indicator " + joined + ", ours and joined, was rewritten: before " + written + ", after " + WrittenOn(document.GetElement(new ElementId(joined))));

        Expect.That(
            document.GetElement(new ElementId(loose)) is null,
            "indicator " + loose + ", ours and joined to nothing, nearer the box than the joined one, still stands: the box went to it, not to the joined one");

        Expect.That(
            near.Count == 1 && near[0] == joined,
            "instances of the indicator type within the box radius, new or the two the case placed, are ["
            + string.Join(", ", near) + "], where only the joined indicator " + joined + " should stand");

        Expect.Same(
            wanted.Count,
            outcome.Placed + outcome.Updated + outcome.JoinedInPlace,
            "boxes the plan recommends, against indicators the apply reports placing, finding in place, or finding joined in place");
    });

    /// <summary>
    /// Places an indicator of ours carrying a cable entry count of the case's own, joins a conduit
    /// drawn out of its own connector, and stands the case down unless the joint is there after the
    /// commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Very little of this is measured: the connectors' domain, whether a conduit of the model's first
    /// type accepts the connector's size, whether <c>ConnectTo</c> takes it, and whether the joint
    /// survives the commit. Each is a loud skip of its own.
    /// </para>
    /// <para>
    /// The diameter is set from the connector before joining, when the connector is round. Not
    /// measured either; it removes the refusal that seemed likeliest.
    /// </para>
    /// </remarks>
    /// <param name="label">What the note this writes is prefixed with.</param>
    /// <param name="where">Where the indicator stands, as the skip reasons say it.</param>
    /// <param name="entries">The cable entry count written on the indicator before it is joined.</param>
    /// <returns>The indicator's id.</returns>
    private static long PlaceJoined(
        RevitTestContext context,
        PostedWarnings watch,
        Document document,
        FamilySymbol symbol,
        XYZ at,
        Level level,
        string label,
        string where,
        int entries)
    {
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

            var indicator = Place(document, symbol, at, level, "an indicator to join to a conduit");
            RecommendJunctionBox(indicator);
            SetInteger(indicator, CablingParameters.TapCount, entries, "cable entry count");
            document.Regenerate();

            var connectors = ConnectorsOf(indicator);

            context.Note(
                label + ": indicator connectors",
                connectors.Count.ToString(CultureInfo.InvariantCulture) + ": "
                + string.Join(", ", connectors.Select(one => one.Domain.ToString()).Distinct()));

            var socket = connectors.FirstOrDefault(one =>
                IsPhysical(one)
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

        Expect.That(
            watch.Fault is null,
            "the test's own failure handler threw while joining a conduit to an indicator " + where + ": " + Describe(watch.Fault));

        var worse = watch.Since(mark).Count(one => one.Severity != FailureSeverity.Warning);

        Skip.When(
            worse > 0,
            "joining a conduit to an indicator " + where + " raised " + worse + " failure(s) worse than a warning");

        Skip.When(
            status != TransactionStatus.Committed,
            "joining a conduit to an indicator " + where + " did not commit (" + status + ")");

        Skip.When(
            !JoinedTo(document.GetElement(new ElementId(joined)) as FamilyInstance, conduitId),
            "Revit did not keep the conduit joined to the indicator " + where + " after the commit, so the joined case cannot be put");

        return joined;
    }

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
        var project = CablingProjectSettings.Read(new FixedSettings());
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

        NeedsDefinitionsFor(document, symbol, run, snapshot);
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
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

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

        NeedsDefinitionsFor(document, symbol, run, snapshot);

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
    /// A circuit the router could not bring near any carrier is posted as a warning against it.
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
    /// <b>The name says nothing about which element the warning is set on</b>, and on purpose. It is
    /// the circuit, and since the owner's decision of 2026-09-14 the registered text says so too: an
    /// end of this circuit, its panel or one of its devices, has nothing within reach - where it used to
    /// say "this device" about an element that was never a device. If the warning moves, the body of
    /// this case changes and its name, which the record keys on, stays.
    /// </para>
    /// <para>
    /// <b>The model's own carriers are routed as well, in both connection modes, for notes only.</b> The
    /// attended run of 2026-09-14 found no route on the linked set with every circuit cut in boxes. The
    /// router asks the same ends in the same order in both modes - panel, then each device - so the mode
    /// should not decide whether a circuit reaches the structure; the two counts and the comparison of
    /// where each circuit stopped say whether it does, and <see cref="ReachNotes"/> says why the circuits
    /// stopped at terminals, where no case writes anything to them first.
    /// </para>
    /// </remarks>
    private static void WarnsOfNoCarrierNear(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

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

        // The same circuits over the model's own carriers, in both connection modes, as notes: see the
        // remarks. Nothing below them asserts on any of it.
        var atTerminals = snapshot.Circuits.Described.Select(circuit => InMode(circuit, CircuitConnection.AtTerminal)).ToList();
        var inBoxes = snapshot.Circuits.Described.Select(circuit => InMode(circuit, CircuitConnection.AtJunctionBox)).ToList();
        var routedAtTerminals = atTerminals.Select(circuit => Router.Route(snapshot.Network, circuit, Options)).ToList();
        var routedInBoxes = inBoxes.Select(circuit => Router.Route(snapshot.Network, circuit, Options)).ToList();

        context.Note("no carrier: over the model's own carriers, at terminals", ReachNotes.ByStatus(routedAtTerminals));
        context.Note("no carrier: over the model's own carriers, cut in boxes", ReachNotes.ByStatus(routedInBoxes));
        context.Note("no carrier: NoCarrierNear in both modes", ReachNotes.SameEnd(routedAtTerminals, routedInBoxes));

        ReachNotes.Explain(
            context,
            "no carrier reach, at terminals",
            document,
            snapshot.Network,
            atTerminals,
            routedAtTerminals,
            Options,
            catalogue,
            project.Boxes);

        Skip.When(
            blocked.Count == 0,
            "routed over a network with no carriers, no circuit came back NoCarrierNear, so there is nothing to warn about");

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "no carrier", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.NoCarrierNear)).ToList();

        Note(context, "no carrier: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());
        OnePerCircuit(mine, blocked, "NoCarrierNear", "which reached no carrier", "circuits the run could not bring near a carrier");
    });

    /// <summary>
    /// A circuit whose ends are near carriers that do not join is posted as a warning against it.
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
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

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

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "no joints", run, snapshot, project, catalogue, out var processed);
        var mine = processed.Where(one => one.Is(CablingFeature.NoConnectivity)).ToList();

        Note(context, "no joints: cabling warnings posted", outcome.Warnings);

        SawWhatWasPosted(outcome, processed.Where(one => one.IsCabling).ToList());
        OnePerCircuit(mine, blocked, "NoConnectivity", "whose carriers do not join up", "circuits the run could not carry across the structure");
    });

    /// <summary>
    /// An element whose type calls it a box, standing beside the structure rather than in it, is posted
    /// as a warning against it.
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
        var project = CablingProjectSettings.Read(new FixedSettings());
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

        NeedsDefinitionsFor(document, symbol, run, snapshot);

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
    /// <b>Failures that are not the apply's own five are noted, by definition id, on every case.</b>
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

    /// <summary>The transactions that processed failures, by name, for a message about one that did not.</summary>
    private static string TransactionsIn(IReadOnlyList<ProcessedFailure> processed)
    {
        var names = processed.Select(one => "'" + one.Transaction + "'").Distinct().ToList();

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>
    /// Every circuit that routed carries the length, the connection it was routed with and the
    /// carriers it was measured along; a circuit that did not route carries what it carried before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three values, one write, and all three read back out of the model.</b> The length is
    /// compared against the number the search produced, in internal feet - what the apply answers for
    /// is storing it, and the value it stores is the router's. The connection is compared against the
    /// vocabulary the reader parses, so a run that wrote a word its own reader does not recognise is
    /// red here rather than on somebody's next check.
    /// </para>
    /// <para>
    /// <b>The stamp is built here, spelled out, rather than asked of the apply.</b> Ids in the order
    /// the route walked them, each once, joined by "; ", and a carrier in a link written
    /// <c>link:element</c> - spelled out in this case rather than taken from <c>CarrierId.ToString</c>,
    /// so that a change which dropped the link qualifier turns red instead of agreeing with itself.
    /// The distinction is not academic on the owner's set: 13 of its 49 carriers live in a link, and
    /// an unqualified id there names a different element in the host.
    /// </para>
    /// <para>
    /// <b>The circuits that did not route are the other half of it.</b> A zero written where nothing
    /// was found is a number somebody puts in a journal, and an empty stamp beside a length left over
    /// from an earlier run is worse than either - it makes a stale answer look current. So their three
    /// values are read before the apply and compared after.
    /// </para>
    /// <para>
    /// <b>And so the apply is handed every route, found or not - unlike the placement cases.</b> Until
    /// 2026-09-17 this case gave the apply only the found routes, as <c>PlanFound</c> builds its run, so
    /// a circuit that did not route never reached the write at all and "carries what it carried before"
    /// held by construction: no defect in the apply could have turned it red. Found while designing the
    /// second round of red runs, before any Revit was started. The blocked routes make the apply post
    /// NoCarrierNear and NoConnectivity, whose definitions only the edition registers - the case stands
    /// down by name without them, as the warning cases do.
    /// </para>
    /// </remarks>
    private static void CircuitsAreToldTheirRoute(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "route written back");

        var cut = CutEveryCircuitInBoxes(watch, document, application);
        var plan = PlanFound(document, project, catalogue);

        Note(context, "route written back: circuits cut in boxes", cut);
        Note(context, "route written back: routes found", plan.Run.Found);
        Note(context, "route written back: routes not found", plan.Results.Count - plan.Run.Found);

        Skip.When(
            plan.Run.Found == 0,
            "no circuit of this model routed, so there is no length, connection or set of carriers to write");

        // Every route, the blocked ones too, and the plan's own boxes: see the remarks.
        var run = new RouteRun(plan.Results, plan.Snapshot.Network.Version, TimeSpan.Zero)
        {
            Boxes = plan.Run.Boxes,
        };

        // Read before the apply, so that a value already standing on a circuit answers for itself
        // rather than for something this apply wrote.
        var untouched = plan.Results
            .Where(one => one.Status != RouteStatus.Found)
            .Select(one => (Circuit: one.Circuit.Value, Before: Stored(document, one.Circuit.Value)))
            .ToList();

        NeedsDefinitionsFor(document, symbol, run, plan.Snapshot);

        var outcome = ApplyWatched(context, watch, "route written back", run, plan.Snapshot, project, catalogue);

        Note(context, "route written back: circuits told their route", outcome.CircuitsWritten);
        Note(context, "route written back: circuits that did not route", untouched.Count);

        Expect.Same(
            plan.Run.Found,
            outcome.CircuitsWritten,
            "routes found, against circuits the apply reports telling their length, connection and carriers");

        foreach (var route in run.Results.Where(one => one.Status == RouteStatus.Found))
        {
            var circuit = document.GetElement(new ElementId(route.Circuit.Value));
            var where = "circuit " + route.Circuit.Value.ToString(CultureInfo.InvariantCulture);
            var length = circuit?.get_Parameter(CablingParameters.CableLength);
            var stored = length is { HasValue: true }
                ? length.AsDouble().ToString("F6", CultureInfo.InvariantCulture)
                : "nothing";

            Expect.That(
                length is { HasValue: true } && Math.Abs(length.AsDouble() - route.TotalLength) < 1e-9,
                where + ": the length stored against the length the run computed - stored " + stored
                + ", computed " + route.TotalLength.ToString("F6", CultureInfo.InvariantCulture) + " ft");

            var connection = Value(circuit, CablingParameters.RouteConnection);
            var routedWith = route.Connection == CircuitConnection.AtJunctionBox
                ? CablingParameters.ConnectionAtJunctionBox
                : CablingParameters.ConnectionAtTerminal;

            Expect.That(
                string.Equals(connection, routedWith, StringComparison.Ordinal),
                where + ": the connection stored against the one it was routed with - stored " + connection
                + ", routed with " + routedWith);

            var stamp = Value(circuit, CablingParameters.RouteStamp);
            var walked = Stamp(route.Path);

            Expect.That(
                string.Equals(stamp, walked, StringComparison.Ordinal),
                where + ": the carriers stored against the ones the route walked - stored " + stamp
                + ", walked " + walked);
        }

        var changed = untouched
            .Where(one => !string.Equals(Stored(document, one.Circuit), one.Before, StringComparison.Ordinal))
            .ToList();

        Expect.That(
            changed.Count == 0,
            "circuits that did not route whose stored length, connection or carriers changed across the apply: "
            + string.Join(", ", changed.Select(one => one.Circuit.ToString(CultureInfo.InvariantCulture))));
    });

    /// <summary>The values a run writes on a circuit, as one comparable string.</summary>
    /// <remarks>
    /// Round-trip formatting on the lengths, because this is used to show that nothing changed: a value
    /// compared after rounding would hide a write that moved the number by less than it prints. The five
    /// parts of the length are here too, since 2026-09-17: a circuit that did not route must not be given a
    /// breakdown any more than a length.
    /// </remarks>
    private static string Stored(Document document, long circuit)
    {
        var element = document.GetElement(new ElementId(circuit));

        return RoundTrip(element, CablingParameters.CableLength)
            + " | " + Value(element, CablingParameters.RouteConnection)
            + " | " + Value(element, CablingParameters.RouteStamp)
            + " | " + RoundTrip(element, CablingParameters.LengthInTray)
            + " | " + RoundTrip(element, CablingParameters.LengthInConduit)
            + " | " + RoundTrip(element, CablingParameters.LengthFree)
            + " | " + RoundTrip(element, CablingParameters.LengthOther)
            + " | " + RoundTrip(element, CablingParameters.LengthSlack);
    }

    private static string RoundTrip(Element? element, Guid parameter)
    {
        var found = element?.get_Parameter(parameter);

        return found is { HasValue: true } ? found.AsDouble().ToString("R", CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>
    /// The case for the length laid by where: every found route's five parts read back out of the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Routed with slack, so that the fifth number is not zero by default.</b> The command's own default
    /// extension is none, and a slack of zero written and a slack never written read the same; a twentieth
    /// is asked for here, and the case computes what it has to be from the route's own two lengths rather
    /// than reading <c>Slack</c> back from the result it is checking.
    /// </para>
    /// <para>
    /// <b>The classes are spelled here, "tray" and "conduit"</b>, not taken from <c>CarrierCatalogue</c>,
    /// so that a catalogue which renamed a class turns this red rather than agreeing with the apply through
    /// the constant both of them read. The other carriers are what is left of the length along carriers
    /// once those two are taken, and the five together have to make the stored total.
    /// </para>
    /// <para>
    /// Only the found routes go to the apply, as the placement cases hand it: what a circuit that did not
    /// route carries is the length case's to guard, and <see cref="Stored"/> reads the five parts there.
    /// </para>
    /// </remarks>
    private static void CircuitsAreToldWhereTheirLengthIsLaid(RevitTestContext context) => Watched(context, watch =>
    {
        const double extension = 0.05;

        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "length by where");

        var cut = CutEveryCircuitInBoxes(watch, document, application);
        var withSlack = new RoutingOptions
        {
            JoinTolerance = Options.JoinTolerance,
            MaxApproach = Options.MaxApproach,
            AxisAlignedApproach = Options.AxisAlignedApproach,
            LengthExtend = extension,
        };

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var results = snapshot.Circuits.Described.Select(circuit => Router.Route(snapshot.Network, circuit, withSlack)).ToList();
        var found = results.Where(one => one.Status == RouteStatus.Found).ToList();

        Note(context, "length by where: circuits cut in boxes", cut);
        Note(context, "length by where: routes found", found.Count);

        Skip.When(found.Count == 0, "no circuit of this model routed, so there is no length to divide");

        var run = new RouteRun(found, snapshot.Network.Version, TimeSpan.Zero)
        {
            Boxes = BoxPlanner.Plan(results, snapshot.Boxes, project.BoxRadius),
        };

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "length by where", run, snapshot, project, catalogue);

        Expect.Same(found.Count, outcome.CircuitsWritten, "routes found, against circuits the apply reports telling their length");

        Note(context, "length by where: routes with a length in trays", found.Count(one => one.AlongClass("tray") > 0));
        Note(context, "length by where: routes with a length in conduits", found.Count(one => one.AlongClass("conduit") > 0));
        Note(context, "length by where: routes with a length in other carriers",
            found.Count(one => one.AlongCarriers - one.AlongClass("tray") - one.AlongClass("conduit") > 1e-9));

        foreach (var route in found)
        {
            var circuit = document.GetElement(new ElementId(route.Circuit.Value));
            var where = "circuit " + route.Circuit.Value.ToString(CultureInfo.InvariantCulture);

            var tray = route.AlongClass("tray");
            var conduit = route.AlongClass("conduit");
            var other = route.AlongCarriers - tray - conduit;
            var slack = (route.AlongCarriers + route.Approaches) * extension;

            StoredLength(circuit, CablingParameters.LengthInTray, tray, where + ": the length in trays");
            StoredLength(circuit, CablingParameters.LengthInConduit, conduit, where + ": the length in conduits");
            StoredLength(circuit, CablingParameters.LengthFree, route.Approaches, where + ": the length in no carrier");
            StoredLength(circuit, CablingParameters.LengthOther, other, where + ": the length in other carriers");
            StoredLength(circuit, CablingParameters.LengthSlack, slack, where + ": the slack");

            var parts = new[]
            {
                CablingParameters.LengthInTray, CablingParameters.LengthInConduit, CablingParameters.LengthFree,
                CablingParameters.LengthOther, CablingParameters.LengthSlack,
            };

            var sum = parts.Sum(one => circuit?.get_Parameter(one) is { HasValue: true } stored ? stored.AsDouble() : double.NaN);
            var total = circuit?.get_Parameter(CablingParameters.CableLength) is { HasValue: true } length ? length.AsDouble() : double.NaN;

            Expect.That(
                Math.Abs(sum - total) < 1e-9,
                where + ": the five parts stored add up to the length stored - parts "
                + sum.ToString("F6", CultureInfo.InvariantCulture) + ", length " + total.ToString("F6", CultureInfo.InvariantCulture) + " ft");
        }
    });

    /// <summary>One part of a circuit's length, read back and compared with what it has to be.</summary>
    private static void StoredLength(Element? circuit, Guid parameter, double expected, string what)
    {
        var found = circuit?.get_Parameter(parameter);
        var stored = found is { HasValue: true } ? found.AsDouble().ToString("F6", CultureInfo.InvariantCulture) : "nothing";

        Expect.That(
            found is { HasValue: true } && Math.Abs(found.AsDouble() - expected) < 1e-9,
            what + " stored against what the route walked - stored " + stored
            + ", walked " + expected.ToString("F6", CultureInfo.InvariantCulture) + " ft");
    }

    /// <summary>The carriers of a route, spelled the way the stamp has to spell them.</summary>
    /// <remarks>
    /// Spelled out here rather than taken from <c>CarrierId.ToString</c>: a case and the code it checks
    /// must not agree by sharing the one thing being checked.
    /// </remarks>
    private static string Stamp(IEnumerable<CarrierId> path)
    {
        var seen = new HashSet<CarrierId>();
        var parts = new List<string>();

        foreach (var id in path)
        {
            if (!seen.Add(id))
                continue;

            parts.Add(id.IsLinked
                ? id.Source.ToString(CultureInfo.InvariantCulture) + ":" + id.Value.ToString(CultureInfo.InvariantCulture)
                : id.Value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("; ", parts);
    }

    /// <summary>Every count an outcome reports, by what it counts.</summary>
    /// <remarks>
    /// All ten the outcome has today, including the ones no case here makes non-zero. The list is
    /// guarded: the rollback case first compares its length with the public <c>int</c> properties of
    /// <see cref="ApplyOutcome"/>, before anything that can stand the case down - so an eleventh count
    /// added to the outcome and not here goes red on any sweep, rather than letting a refusal claim that
    /// work unnoticed. Given no outcome, every count reads zero, which is all that comparison needs.
    /// </remarks>
    private static (string What, int Count)[] CountsOf(ApplyOutcome? outcome) => new[]
    {
        ("indicators placed", outcome?.Placed ?? 0),
        ("indicators found in place", outcome?.Updated ?? 0),
        ("indicators removed", outcome?.Removed ?? 0),
        ("joined indicators left untouched", outcome?.Joined ?? 0),
        ("joined indicators standing for a recommended box", outcome?.JoinedInPlace ?? 0),
        ("boxes already in the model used", outcome?.ExistingUsed ?? 0),
        ("carriers and boxes told their circuits", outcome?.CarriersMarked ?? 0),
        ("circuits told their length, connection and route", outcome?.CircuitsWritten ?? 0),
        ("references that fell in a link", outcome?.InLinks ?? 0),
        ("warnings posted", outcome?.Warnings ?? 0),
    };

    private static string Claimed(ApplyOutcome outcome) =>
        string.Join(", ", CountsOf(outcome).Select(one => one.What + " " + one.Count.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Stands a case down when the apply would post a warning this session has no definition for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>new FailureMessage(id)</c> throws for an id nobody registered, inside the apply's own
    /// transaction, and the case would report "threw before it could assert". The definitions are
    /// created only by an edition that lists the cabling module, at startup - the probe that hosts
    /// these cases lists no such module - so a sweep without an edition installed beside the probe
    /// cannot put this question, and says so. The registry is static, measured by the compiler.
    /// </para>
    /// <para>
    /// <b>The joined indicator warning is asked of the model, not of the case.</b> The apply posts it for
    /// every joined indicator of ours it meets, and a model can hold those before any case builds one -
    /// so every case that applies has to ask, not only the ones that join an indicator themselves.
    /// </para>
    /// </remarks>
    private static void NeedsDefinitionsFor(Document document, FamilySymbol symbol, RouteRun run, CablingSnapshot snapshot)
    {
        var posting = new List<(FailureDefinitionId Id, string Name)>();

        if (run.Count(RouteStatus.NoCarrierNear) > 0)
            posting.Add((CablingFeature.NoCarrierNear, nameof(CablingFeature.NoCarrierNear)));

        if (run.Count(RouteStatus.NoConnectivity) > 0)
            posting.Add((CablingFeature.NoConnectivity, nameof(CablingFeature.NoConnectivity)));

        if (run.Count(RouteStatus.NoBoxReachable) > 0)
            posting.Add((CablingFeature.NoBoxReachable, nameof(CablingFeature.NoBoxReachable)));

        if (snapshot.Circuits.UnreadableConnectionIds.Count > 0)
            posting.Add((CablingFeature.ConnectionUnreadable, nameof(CablingFeature.ConnectionUnreadable)));

        if (snapshot.BoxesUnconnectedIds.Count > 0)
            posting.Add((CablingFeature.JunctionBoxJoinedToNothing, nameof(CablingFeature.JunctionBoxJoinedToNothing)));

        if (JoinedIndicatorsOfOurs(document, symbol).Count > 0)
            posting.Add((CablingFeature.IndicatorJoinedIntoNetwork, nameof(CablingFeature.IndicatorJoinedIntoNetwork)));

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
        var standing = IndicatorsOf(document, symbol).Count(RecognisedAsOurs);

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
    /// <b>Asked of the watch first, and of the status after.</b> <c>Install</c> now reports what its
    /// transaction came to, and a binding that did not commit is a skip here rather than a case carrying
    /// on against a document with nothing bound, to fail at its first write with a message blaming the
    /// binding path. The watch is still asked before the status: inside a watch, a commit that raised
    /// something worse than a warning is rolled back without a word on screen, and naming that error is
    /// naming the cause - where "did not commit (RolledBack)" would name only what it led to.
    /// </remarks>
    private static void Bind(PostedWarnings watch, Document document, RevitApplication application, RuntimeCategories? runtime)
    {
        var scheme = new CablingParameters();

        scheme.Export(application);

        var mark = watch.Mark;

        scheme.Install(document, application, runtime, out var status);
        NothingWorse(watch, mark, "binding the shared parameters");

        Skip.When(
            status is { } returned && returned != TransactionStatus.Committed,
            "binding the shared parameters did not commit (" + status + ")");
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

    /// <summary>The same circuit, routed in the connection mode given rather than the one it was read with.</summary>
    /// <remarks>
    /// Built with the public constructor and nothing written to the model, so a question about the mode
    /// costs no transaction. What the router reads is carried across: the ends, their order, the number
    /// and the length Revit reports.
    /// </remarks>
    private static CircuitSnapshot InMode(CircuitSnapshot circuit, CircuitConnection connection) =>
        new(circuit.Id, circuit.Number, circuit.Source, circuit.Devices)
        {
            Connection = connection,
            BuiltInLength = circuit.BuiltInLength,
        };

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

    /// <summary>The host carriers the found routes of a run walk, each once.</summary>
    private static List<long> HostPath(RouteRun run) =>
        run.Results
            .Where(route => route.Status == RouteStatus.Found)
            .SelectMany(route => route.Path)
            .Where(carrier => !carrier.IsLinked)
            .Select(carrier => carrier.Value)
            .Distinct()
            .ToList();

    /// <summary>
    /// Every host element the plan puts a circuit through, with the ids of those circuits: the found
    /// routes that walk it, and the circuits a used box standing on it serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What <c>BHS_Cbl_CircuitRefs</c> means, read from the plan - its route paths and its boxes - and
    /// not from the way the apply collects it. An element in a link is left out: nothing is written
    /// there, and the placement case asserts that separately.
    /// </para>
    /// <para>
    /// <b>Each list is in the owner's order, and the cases compare it in order</b>: first seen over the
    /// found routes' paths, in the order the run lists its routes, then over the used boxes' circuits,
    /// in the order the run lists its boxes - each circuit once. The same rule the indicators keep, so
    /// all three writers are held to one.
    /// </para>
    /// </remarks>
    private static Dictionary<long, List<long>> ThroughEachElement(RouteRun run)
    {
        var through = new Dictionary<long, List<long>>();

        void Add(CarrierId element, CarrierId circuit)
        {
            if (element.IsLinked)
                return;

            if (!through.TryGetValue(element.Value, out var circuits))
                through[element.Value] = circuits = new List<long>();

            if (!circuits.Contains(circuit.Value))
                circuits.Add(circuit.Value);
        }

        foreach (var route in run.Results.Where(one => one.Status == RouteStatus.Found))
        {
            foreach (var carrier in route.Path)
                Add(carrier, route.Circuit);
        }

        foreach (var box in run.Boxes)
        {
            if (box.Existing is not { } existing)
                continue;

            foreach (var circuit in box.Circuits)
                Add(existing.Id, circuit);
        }

        return through;
    }

    /// <summary>
    /// Fails unless an element's <c>BHS_Cbl_CircuitRefs</c> is in the owner's one format and names
    /// exactly the circuits given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read back into ids rather than compared as text</b>: circuit element ids in invariant digits,
    /// separated by exactly <see cref="RefsSeparator"/>, each once. A value in any other format fails as
    /// that, before circuits are compared, and the set before the order - so a wrong circuit is not
    /// reported as a wrong order.
    /// </para>
    /// <para>
    /// <b>The value itself is never printed.</b> A reference written before the format was settled holds
    /// circuit numbers, and a circuit number can carry a panel's name - which the public record must not.
    /// Which part is not an id, and the ids compared, say enough to act on.
    /// </para>
    /// </remarks>
    /// <param name="subject">The element as the message names it, by id.</param>
    /// <param name="expected">The circuit ids it has to name, in the order they are expected when that matters.</param>
    /// <param name="inOrder">Whether the order is part of what is asserted.</param>
    private static void ExpectCircuitRefs(Element? element, string subject, IReadOnlyList<long> expected, bool inOrder)
    {
        var parameter = element?.get_Parameter(CablingParameters.CircuitRefs);
        var value = parameter is { HasValue: true } ? parameter.AsString() ?? string.Empty : string.Empty;

        Expect.That(
            value.Length > 0,
            subject + " names no circuit after the apply"
            + (parameter is { IsReadOnly: true } ? ", and Revit keeps its circuit references read-only" : string.Empty));

        var parts = value.Split(new[] { RefsSeparator }, StringSplitOptions.None);
        var named = new List<long>();

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var isId = long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                       && id > 0
                       && string.Equals(part, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            Expect.That(
                isId,
                subject + " holds circuit references that are not circuit element ids separated by '" + RefsSeparator
                + "': part " + (index + 1) + " of " + parts.Length + " is not an id");

            named.Add(id);
        }

        var repeated = named.GroupBy(one => one).Where(group => group.Count() > 1).Select(group => group.Key).ToList();

        Expect.That(repeated.Count == 0, subject + " names circuit(s) more than once: " + Listed(repeated));

        var missing = expected.Where(one => !named.Contains(one)).ToList();
        var foreign = named.Where(one => !expected.Contains(one)).ToList();

        Expect.That(
            missing.Count == 0 && foreign.Count == 0,
            subject + " names circuits " + Listed(named) + " where the plan puts " + Listed(expected) + " through it: missing "
            + Listed(missing) + ", not through it " + Listed(foreign));

        if (inOrder)
        {
            Expect.That(
                named.SequenceEqual(expected),
                subject + " names its circuits in the order " + Listed(named) + ", not in the order the plan met them, " + Listed(expected));
        }
    }

    private static string Listed(IEnumerable<long> ids) =>
        "[" + string.Join(", ", ids.Select(one => one.ToString(CultureInfo.InvariantCulture))) + "]";

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

        Committed(watch, mark, status, "placing " + what);

        var element = document.GetElement(new ElementId(placed)) as FamilyInstance;

        Skip.When(element is null, what + " is not in the model after the commit that placed it");

        var joint = Joint(element!);

        Skip.When(
            joint is not null,
            what + " " + joint + " as soon as it is placed, so it cannot stand for one joined to nothing");

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

    private static void SetInteger(Element? element, Guid parameter, int value, string what)
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
    /// Whether the apply would take the element for its own: as broadly as it recognises one, trimmed
    /// and in any case.
    /// </summary>
    /// <remarks>
    /// For the questions where being too narrow is the mistake - whether a model already holds
    /// indicators of ours, and which of them the apply will warn about - as opposed to
    /// <see cref="CarriesOurRecommendation"/>, which asks what the apply wrote.
    /// </remarks>
    private static bool RecognisedAsOurs(Element? element) =>
        string.Equals(
            element?.get_Parameter(CablingParameters.Recommendation)?.AsString()?.Trim(),
            CablingApply.RecommendsJunctionBox,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Indicators of ours in the host that are joined to something through a physical connector - read
    /// with this suite's own code, not the apply's.
    /// </summary>
    /// <remarks>
    /// Each one is owed the warning the apply posts about a joined indicator, so the question has to be
    /// exactly the apply's; <see cref="Joint"/> says why it is no broader.
    /// </remarks>
    private static List<long> JoinedIndicatorsOfOurs(Document document, FamilySymbol symbol) =>
        IndicatorsOf(document, symbol)
            .Where(RecognisedAsOurs)
            .Where(one => Joint(one) is not null)
            .Select(one => one.Id.Value)
            .ToList();

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
            if (!IsPhysical(connector) || !connector.IsConnected)
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
    /// <b>The same question the apply asks, no broader</b>: only a physical connector can be joined, the
    /// owner's decision of 2026-09-16. It was once broader on purpose - every connector asked, a refusal
    /// counted as a joint that cannot be ruled out - while the apply itself asked every connector. Since
    /// the apply asks only physical ones, broader here is wrong in the dangerous direction:
    /// <see cref="JoinedIndicatorsOfOurs"/> would expect a warning for an indicator whose family carries
    /// a surface or logical connector, and the apply, rightly, would post none. A physical connector
    /// answering <c>IsConnected</c> is the premise the connector census asserts, so it is not caught.
    /// </remarks>
    private static string? Joint(FamilyInstance element)
    {
        var index = 0;

        foreach (var connector in ConnectorsOf(element))
        {
            if (IsPhysical(connector) && connector.IsConnected)
                return "reports its connector " + index + " connected";

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
            if (!IsPhysical(connector) || !connector.IsConnected)
                continue;

            foreach (Connector far in connector.AllRefs)
            {
                if (far?.Owner is { } owner && owner.Id.Value == other)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>Connectors.IsPhysical</c>, again: spelled here so a case and the code it checks cannot agree
    /// through the very rule under test. Not <c>!= Logical</c> - a surface connector (type 32) is not
    /// logical either, and it refuses <c>IsConnected</c> the same way.
    /// </summary>
    private static bool IsPhysical(Connector connector) =>
        connector is not null && (connector.ConnectorType & ConnectorType.Physical) != 0;

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

    /// <summary>The level the apply would place an indicator on at a height: the nearest at or below it, else the lowest.</summary>
    /// <remarks>
    /// The apply's rule, repeated for construction only: an instance a case stands on a recommended box
    /// then sits on the level the apply's own indicators would, so that whatever Revit does with the
    /// height, it does the same to both. No assertion reads the level.
    /// </remarks>
    private static Level LevelAtOrBelow(Document document, double z)
    {
        var levels = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(one => one.Elevation)
            .ToList();

        Skip.When(levels.Count == 0, "the model this sweep opened has no level to place an indicator on");

        var chosen = levels[0];

        foreach (var level in levels)
        {
            if (level.Elevation <= z)
                chosen = level;
        }

        return chosen;
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

    /// <summary>Everything the apply writes on an indicator, as one comparable value.</summary>
    /// <remarks>
    /// Printed on a difference, which is safe only because the cases that use it wrote every one of
    /// these values themselves or left it empty, and what the apply writes is our token, a count and ids.
    /// </remarks>
    private static string WrittenOn(Element? element) =>
        ValuesOf(element) + ", circuits '" + Value(element, CablingParameters.CircuitRefs) + "'";

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
    /// Not the command's: those defaults live in the feature assembly, which this one cannot see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every assertion in this suite holds whatever these two distances are, so they are deliberately
    /// not copied into a second literal that would have to be kept in step with the command.
    /// </para>
    /// <para>
    /// <b>They are not the command's, and the two cases that explain a failed route say so in a note.</b>
    /// The command reads 50 mm and 3000 mm by default; half a foot is 152.4 mm and ten feet 3048 mm, so
    /// these cases join carriers across gaps three times as wide and reach 48 mm farther. By the code, not
    /// by a measurement, neither difference can turn a circuit the command routes into one these cases
    /// cannot - a wider joint only adds adjacency, the join tolerance never enters the reach, and the reach
    /// here is the larger - but a run that routes here and not in the command is possible, and the attended
    /// run of 2026-09-14 found no route here at all. <see cref="ReachNotes"/> restates the command's
    /// defaults beside these, for a note and never for an assertion. Changing these is the owner's
    /// decision, not a side effect of a diagnosis.
    /// </para>
    /// </remarks>
    private static RoutingOptions Options { get; } = new()
    {
        JoinTolerance = 0.5,
        MaxApproach = 10,
        AxisAlignedApproach = true,
    };
}
