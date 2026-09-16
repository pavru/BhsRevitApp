using System.Globalization;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BHS.MEP.Cabling.Declaration;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Common.Parameters;

namespace BHS.MEP.Cabling.Revit;

/// <summary>What writing a run into the model came to.</summary>
/// <remarks>
/// <para>
/// <b>Refusals are a first-class outcome, not an exception.</b> Every way this can decline to write
/// is a condition about the model or the project - a family that is not loaded, a parameter that
/// could not be bound - and none of them is a fault in the caller. A thrown exception would reach a
/// modal window as a type name; a refusal reaches it as the sentence somebody acts on.
/// </para>
/// <para>
/// <b>A transaction Revit did not keep is a refusal too, and its counts are all zero.</b> The owner's
/// decision of 2026-09-14: anything but <c>Committed</c> means nothing was applied. The counts are
/// gathered while the transaction is open, and a commit that comes back <c>RolledBack</c> undoes every
/// placement and every write they describe - so an outcome that kept them would report "placed four"
/// about a model with nothing in it. The outcome that comes back instead carries no counts at all,
/// and <see cref="NotCommitted"/> names what Revit returned.
/// </para>
/// </remarks>
public sealed class ApplyOutcome
{
    internal ApplyOutcome(IReadOnlyList<string> refusals, TransactionStatus? notCommitted = null)
    {
        Refusals = refusals;
        NotCommitted = notCommitted;
    }

    /// <summary>Why nothing was written. Empty when something was.</summary>
    public IReadOnlyList<string> Refusals { get; }

    public bool Refused => Refusals.Count > 0;

    /// <summary>
    /// What Revit returned for a transaction of the apply that did not end as it should, or nothing.
    /// </summary>
    /// <remarks>
    /// Set only on a refusal, and only when the refusal is Revit's rather than ours: the transaction
    /// that binds the parameters, or the one that writes the run, came back as something other than
    /// <c>Started</c> on start or <c>Committed</c> on commit. A caller tells the two kinds of refusal
    /// apart by this - a family that is not loaded is a condition of the project, a commit Revit did
    /// not keep is a failure somebody has to look into, and the command logs it as an error.
    /// </remarks>
    public TransactionStatus? NotCommitted { get; }

    /// <summary>Indicators newly placed.</summary>
    public int Placed { get; internal set; }

    /// <summary>Indicators from a previous run that were found and rewritten in place.</summary>
    /// <remarks>
    /// Never a joined one: an indicator somebody connected into the network is found in place and
    /// left exactly as it is, and is counted in <see cref="JoinedInPlace"/> instead.
    /// </remarks>
    public int Updated { get; internal set; }

    /// <summary>Indicators of ours that this run no longer recommends, and took away.</summary>
    public int Removed { get; internal set; }

    /// <summary>
    /// Indicators of ours that somebody connected into the network: left untouched, and warned about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every one the apply meets, wherever it stands</b> - the owner's decision of 2026-09-14. An
    /// element of the indicator type, carrying our recommendation and connected to something, is never
    /// rewritten and never removed, and each one is posted with
    /// <c>CablingFeature.IndicatorJoinedIntoNetwork</c> against itself.
    /// </para>
    /// <para>
    /// <b>This used to be called <c>Adopted</c>, and the name said something the tool does not
    /// believe.</b> It read "stopped being ours the moment somebody joined it" - while the reader, which
    /// goes by the type, went on treating the element as an indicator. The warning exists because of
    /// exactly that gap, and the count is named for what is true of every element in it.
    /// </para>
    /// <para>
    /// <b>Removal stays safe for the reason it always was.</b> A marker is joined to nothing by
    /// construction - measured on the owner's model, a freshly placed one has four connectors and none
    /// of them connected - so an element of our type that is joined to something was joined by a
    /// person, and deleting it would delete part of their model.
    /// </para>
    /// </remarks>
    public int Joined { get; internal set; }

    /// <summary>
    /// Of <see cref="Joined"/>, those standing where this run recommends a box, which took that box's place.
    /// </summary>
    /// <remarks>
    /// Counted apart so the arithmetic of the recommended boxes still closes: each one is either placed,
    /// updated, or stood in for by a joined indicator of ours within the box radius - which is left as
    /// it is, and has no second indicator placed beside it.
    /// </remarks>
    public int JoinedInPlace { get; internal set; }

    /// <summary>Junction boxes already in the model that the plan used instead of recommending one.</summary>
    public int ExistingUsed { get; internal set; }

    /// <summary>Carriers, and boxes already in the model, that were told which circuits run through them.</summary>
    public int CarriersMarked { get; internal set; }

    /// <summary>Circuits told the length of their route, how it was routed and what it ran through.</summary>
    /// <remarks>
    /// Three parameters and one count, because they are written together or not at all: a length
    /// without the connection it was computed with is a number nobody can check, and without the
    /// carriers it was measured along nobody can tell a current one from a stale one.
    /// </remarks>
    public int CircuitsWritten { get; internal set; }

    /// <summary>Elements that could not be written to because they live in a link.</summary>
    /// <remarks>
    /// Said out loud rather than skipped in silence. A model whose trays are all in a link gets no
    /// references written at all, and the difference between "nothing to write" and "nowhere to
    /// write it" is the whole of what somebody needs to know.
    /// </remarks>
    public int InLinks { get; internal set; }

    /// <summary>Warnings posted for Revit to show when the apply commits.</summary>
    /// <remarks>
    /// Posted, not stored. Revit's reference for <c>Document.PostFailure</c>: "warnings posted via
    /// this method will not be stored in the document after they are resolved" - so this counts what
    /// was put in front of whoever pressed Apply, not entries anybody will find in the model later.
    /// </remarks>
    public int Warnings { get; internal set; }
}

/// <summary>
/// Writes a finished run into the model: the indicators it recommends, the references it found,
/// and the warnings it owes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction for the whole act.</b> Half-applied is the one state nobody can reason about:
/// indicators standing for a plan whose references were never written, or references naming boxes
/// that are not there. Binding the parameters is the exception and opens its own - it is a change to
/// the document's schema rather than to its contents, and <c>SharedParameterScheme.Install</c> owns
/// that decision already.
/// </para>
/// <para>
/// <b>Both transactions are asked how they ended, and anything but the expected answer is a
/// refusal.</b> Revit's reference for <c>Transaction.Commit</c> tells callers to "always check the
/// returned status", and names <c>RolledBack</c> as a possible outcome of failure handling and
/// <c>Pending</c> as one where Revit is still waiting for a person. Neither is an exception, so code
/// that ignored the status reported a full count about a model that holds none of it.
/// </para>
/// <para>
/// <b>Nothing is written into a link, ever.</b> A linked document is not modifiable from the host,
/// and the carriers of a real project routinely live in one - measured, a surveyed model held 13
/// trays and 71 conduits in a link. So the references have nowhere to go for those, and the count of
/// what was skipped travels back rather than the write quietly doing less than it says.
/// </para>
/// <para>
/// <b>Warnings are posted here because here is the only place they can be.</b>
/// <c>Document.PostFailure</c> is legal inside a transaction and nowhere else, and the compute phase
/// has none - which is why the same conditions appear twice: as lines on the result screen, and as
/// warnings Revit shows when the transaction commits. The two addressees are the same person at two
/// moments. The warning is not kept - Revit's reference says a warning posted this way "will not be
/// stored in the document after they are resolved", and the owner's decision of 2026-09-14 accepts
/// that - so what outlives the moment is the result screen and the log, not the model.
/// </para>
/// </remarks>
public static class CablingApply
{
    /// <summary>The value written into <c>BHS_Cbl_Recommendation</c> on an indicator.</summary>
    /// <remarks>
    /// The same token as the role a real box carries, and deliberately so: one spelling of one idea.
    /// The parameter it is written to is what differs - a recommendation of ours against a role a
    /// designer set - and that difference is carried by which parameter holds it, not by the word.
    /// </remarks>
    public const string RecommendsJunctionBox = CablingParameters.JunctionBoxRole;

    /// <summary>Writes the run into the host document. Call on the API thread.</summary>
    /// <param name="host">The document the run was read from.</param>
    /// <param name="application">Needed to bind the parameters, which lives on <c>Application</c>.</param>
    /// <param name="run">A finished run, whose boxes were planned with the project's radius.</param>
    /// <param name="snapshot">The read the run was computed from, for the conditions it has to report.</param>
    /// <param name="project">The project's answers, for the indicator family and the box radius.</param>
    /// <param name="catalogue">What counts as a carrier here, so the references can reach all of them.</param>
    public static ApplyOutcome Apply(
        Document host,
        Application application,
        RouteRun run,
        CablingSnapshot snapshot,
        CablingProjectSettings project,
        CarrierCatalogue catalogue)
    {
        if (host is null || application is null || run is null || snapshot is null || project is null)
            return new ApplyOutcome(new[] { "Nothing to apply." });

        var symbol = project.Boxes.In(host);

        if (symbol is null)
        {
            // Named rather than shrugged at, and this is the failure the owner asked be loud: the
            // family or the type was renamed, and a run that quietly placed nothing would look
            // exactly like a run that had nothing to place.
            return new ApplyOutcome(new[]
            {
                string.Format(
                    CultureInfo.CurrentCulture,
                    "This model has no family '{0}' with a type '{1}', so there is nothing to place. "
                    + "Load it, or point Model:Cabling:RecommendedBox:Family and :Type at what this "
                    + "project uses.",
                    project.Boxes.Family,
                    project.Boxes.Type),
            });
        }

        if (symbol.Category is null)
        {
            return new ApplyOutcome(new[]
            {
                "The indicator family has no category, so its parameters cannot be bound to anything.",
            });
        }

        var indicator = (BuiltInCategory)symbol.Category.Id.Value;
        var scheme = new CablingParameters();
        var runtime = Runtime(indicator, catalogue);

        scheme.Install(host, application, runtime, out var binding);

        // Before the check for what is bound, and before anything else is opened. A binding Revit
        // rolled back would otherwise surface below as "these parameters could not be bound" - true,
        // and pointing at the categories rather than at the commit. And while a commit is Pending,
        // Revit's reference says no new transaction may start - so going on would reach the screen as
        // an exception from Start rather than as a sentence. Not measured; the reference is the source.
        if (binding is { } bound && bound != TransactionStatus.Committed)
        {
            return NotKept(
                bound,
                binding: null,
                "Revit did not keep the parameter binding: its transaction returned {0}, not Committed, "
                + "so the run has nowhere to write and nothing was written.",
                "Revit has not finished binding the parameters: its transaction returned Pending.");
        }

        // The loud check the empty category list makes necessary. Two of the three parameters
        // declare no categories at compile time - the indicator's family is the project's to choose
        // - so a caller who did not pass the runtime category would bind nothing at all and nothing
        // would say so. Asked after binding rather than trusted: Install reports what it bound, and
        // what matters here is what is bound now.
        var missing = scheme.Missing(host, runtime)
            .Where(one => one.Id == CablingParameters.Recommendation
                          || one.Id == CablingParameters.CircuitRefs
                          || one.Id == CablingParameters.TapCount
                          || one.Id == CablingParameters.CableLength
                          || one.Id == CablingParameters.RouteConnection
                          || one.Id == CablingParameters.RouteStamp)
            .ToList();

        if (missing.Count > 0)
        {
            return new ApplyOutcome(new[]
            {
                "These parameters could not be bound, so the run has nowhere to write: "
                + string.Join(", ", missing.Select(one => string.Join(" / ", one.Names())))
                + ". Press Shared parameters, or check that the categories are not excluded from this model.",
            });
        }

        var outcome = new ApplyOutcome(Array.Empty<string>());

        using var transaction = new Transaction(host, "BHS: apply cabling");
        var started = transaction.Start();

        // Revit documents a status here as well as exceptions, and says that unless starting succeeds
        // no change can be made - so a start that returned anything else writes nothing and says so.
        if (started != TransactionStatus.Started)
        {
            return NotKept(
                started,
                binding,
                "Revit would not start the transaction the run is written in: starting it returned {0}, "
                + "not Started, so nothing was placed, updated, removed or written.",
                "Revit has not finished starting the transaction the run is written in: it returned Pending.");
        }

        // Once, and before anything is placed: an inactive symbol places nothing and says nothing
        // about why. Autodesk's own samples do exactly this, immediately before NewFamilyInstance.
        if (!symbol.IsActive)
            symbol.Activate();

        var joined = Indicators(host, symbol, run, project, outcome);
        References(host, run, outcome);
        Lengths(host, run, outcome);
        Warn(host, run, snapshot, joined, outcome);

        var committed = transaction.Commit();

        // The owner's decision of 2026-09-14, and the counts are why it matters: every number in the
        // outcome above was counted inside a transaction Revit has just declined to keep.
        if (committed != TransactionStatus.Committed)
        {
            return NotKept(
                committed,
                binding,
                "Revit did not keep the changes: committing them returned {0}, not Committed, so nothing "
                + "was placed, updated, removed or written.",
                "Revit has not finished committing the changes: it returned Pending.");
        }

        return outcome;
    }

    /// <summary>
    /// A refusal for a transaction that did not end as it had to, with every count left at zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Pending</c> gets its own sentence, because the other one would be false about it.</b>
    /// Revit's reference for <c>Transaction.Commit</c> describes it as failure handling that "has not
    /// been finalized yet" while Revit "awaits user's actions" - so the changes are not known to be
    /// discarded, only not known to be kept. "Revit did not keep them" there would send somebody to
    /// apply again over a write that may yet land. Whether a real apply ever returns it is not
    /// measured; it is still a refusal, because nothing here can say what was written. The command
    /// carries the same distinction onto the screen's headline and into the log.
    /// </para>
    /// <para>
    /// <b>A binding that did commit is said, because "nothing was written" would be false about the
    /// document.</b> The parameters are bound in a transaction of their own before the run's is opened,
    /// so when the run's then fails the new bindings stay, the document is modified, and Revit will ask
    /// about saving it - while the sentence above it says nothing changed.
    /// </para>
    /// </remarks>
    /// <param name="status">What Revit returned.</param>
    /// <param name="binding">What the binding transaction returned before this one, or nothing when there was none.</param>
    /// <param name="notKept">The sentence for every status but Pending, with the status as <c>{0}</c>.</param>
    /// <param name="pending">The first sentence for Pending; what it means and what to do is added here.</param>
    private static ApplyOutcome NotKept(TransactionStatus status, TransactionStatus? binding, string notKept, string pending)
    {
        var sentence = status == TransactionStatus.Pending
            ? pending + " Revit describes that as waiting for somebody to act on a message about it, so "
              + "nothing is reported as written; look at the model before applying again."
            : string.Format(CultureInfo.CurrentCulture, notKept, status);

        if (binding == TransactionStatus.Committed)
            sentence += " The parameters bound just before, in a transaction of their own, stay bound.";

        return new ApplyOutcome(new[] { sentence }, status);
    }

    /// <summary>Where each parameter is wanted beyond what it could declare at compile time.</summary>
    /// <remarks>
    /// The indicator's own category for all three, and every carrier category for the references -
    /// the catalogue is the user's list, so a project that calls something else a carrier gets the
    /// parameter there too. <c>RuntimeCategories</c> adds to what a parameter declares and never
    /// replaces it, so the shipped four cost nothing here.
    /// </remarks>
    private static RuntimeCategories Runtime(BuiltInCategory indicator, CarrierCatalogue catalogue)
    {
        var runtime = new RuntimeCategories()
            .Add(CablingParameters.Recommendation, indicator)
            .Add(CablingParameters.TapCount, indicator)
            .Add(CablingParameters.CircuitRefs, indicator);

        foreach (var category in catalogue?.Categories ?? Array.Empty<BuiltInCategory>())
            runtime.Add(CablingParameters.CircuitRefs, category);

        return runtime;
    }

    /// <summary>
    /// Brings the indicators in the model into line with what this run recommends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Matched by place, because place is what an indicator is.</b> It carries no identity of its
    /// own between runs - it is a mark at a point - so an indicator standing where this run wants one
    /// is that one, and the radius that decides "standing where" is the same box radius that decided
    /// which taps share a box. A second distance would be a second rule about the same nearness.
    /// </para>
    /// <para>
    /// <b>Deletion is narrow on purpose, and each condition is load-bearing.</b> It has to be of the
    /// configured type, it has to carry our recommendation, and it has to be joined to nothing. The
    /// last one is what keeps a designer's work safe: a marker somebody has cut into the wiring is
    /// part of their model now, and taking it away would delete it to tidy up after ourselves.
    /// </para>
    /// <para>
    /// <b>A joined indicator of ours is left exactly as it is, and still takes a recommended box's
    /// place</b> - the owner's decisions of 2026-09-14, the second half first a reading of the first and
    /// then confirmed by the owner the same day. Not rewritten, because its values are now the designer's; not
    /// removed, for the reason above; and no second indicator is placed beside it, because a second
    /// mark within the radius of the first is exactly what the radius exists to prevent. Every one is
    /// returned for a warning, in place or not.
    /// </para>
    /// <para>
    /// <b>So a box asks for a joined indicator first, and for one joined to nothing only when there is
    /// none.</b> Nearest-first over both kinds let an unjoined indicator of ours that happened to stand
    /// nearer take the box, which left the joined one unclaimed and rewrote the other beside it - two
    /// marks of ours inside one radius, the state the decision above rules out. It is not far-fetched:
    /// a copied indicator keeps our recommendation, and the copy is the one somebody joins. The unjoined
    /// one, left unclaimed, is then removed like any other the plan no longer names.
    /// </para>
    /// <para>
    /// <b>Whether it is joined is asked once per indicator, before anything is placed or removed</b>,
    /// so one answer decides both what happens to it in place and whether it may be removed.
    /// </para>
    /// </remarks>
    /// <returns>Every joined indicator of ours the apply met, for the warning each one is owed.</returns>
    private static List<ElementId> Indicators(
        Document host,
        FamilySymbol symbol,
        RouteRun run,
        CablingProjectSettings project,
        ApplyOutcome outcome)
    {
        var wanted = run.Boxes.Where(box => box.IsRecommendation).ToList();
        var standing = Standing(host, symbol);
        var joined = standing.Select(Joined).ToArray();
        var taken = new bool[standing.Count];
        var level = Levels(host);

        foreach (var box in wanted)
        {
            var at = new XYZ(box.At.X, box.At.Y, box.At.Z);

            // A joined one first, and only then one joined to nothing - see the remarks.
            var found = Match(standing, taken, at, project.BoxRadius, joined, joinedOnes: true);

            if (found >= 0)
            {
                outcome.JoinedInPlace++;
                continue;
            }

            found = Match(standing, taken, at, project.BoxRadius, joined, joinedOnes: false);

            if (found >= 0)
            {
                Describe(standing[found], box);
                outcome.Updated++;
                continue;
            }

            var placed = host.Create.NewFamilyInstance(at, symbol, level(at.Z), StructuralType.NonStructural);
            Describe(placed, box);
            outcome.Placed++;
        }

        var warned = new List<ElementId>();

        for (var i = 0; i < standing.Count; i++)
        {
            // Before the claimed test, and that order is the decision: a joined indicator is owed its
            // warning whether or not a box of this run stood on it.
            if (joined[i])
            {
                warned.Add(standing[i].Id);
                continue;
            }

            if (taken[i])
                continue;

            host.Delete(standing[i].Id);
            outcome.Removed++;
        }

        outcome.Joined = warned.Count;
        outcome.ExistingUsed = run.Boxes.Count - wanted.Count;
        return warned;
    }

    /// <summary>Every instance of the indicator type in the host that carries our recommendation.</summary>
    /// <remarks>
    /// Both tests, and neither alone would do. The type alone would sweep up boxes a designer placed
    /// from the same family by hand; the parameter alone would trust a value anybody can type into a
    /// family of their own. Together they name what a previous run of ours left behind.
    /// </remarks>
    private static List<FamilyInstance> Standing(Document host, FamilySymbol symbol) =>
        new FilteredElementCollector(host)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(one => one.GetTypeId() == symbol.Id && Ours(one))
            .ToList();

    private static bool Ours(Element element) =>
        string.Equals(
            element.get_Parameter(CablingParameters.Recommendation)?.AsString()?.Trim(),
            RecommendsJunctionBox,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The nearest unclaimed indicator within the radius that is joined, or is not, as asked; or -1.</summary>
    /// <param name="joined">Whether each standing indicator is joined to anything, by index.</param>
    /// <param name="joinedOnes">True to consider only the joined ones, false only the ones joined to nothing.</param>
    private static int Match(List<FamilyInstance> standing, bool[] taken, XYZ at, double radius, bool[] joined, bool joinedOnes)
    {
        var best = -1;
        var distance = double.MaxValue;

        for (var i = 0; i < standing.Count; i++)
        {
            if (taken[i] || joined[i] != joinedOnes || (standing[i].Location as LocationPoint)?.Point is not { } point)
                continue;

            var candidate = point.DistanceTo(at);

            if (candidate > radius || candidate >= distance)
                continue;

            distance = candidate;
            best = i;
        }

        if (best >= 0)
            taken[best] = true;

        return best;
    }

    /// <summary>Whether any physical connector of this element is joined to anything at all.</summary>
    /// <remarks>
    /// Physical only - the owner's decision of 2026-09-16, and a connector that is not physical
    /// refuses the question rather than answering it. See <see cref="Connectors"/>.
    /// </remarks>
    private static bool Joined(FamilyInstance instance)
    {
        var manager = instance.MEPModel?.ConnectorManager;

        if (manager is null)
            return false;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector.IsJoined())
                return true;
        }

        return false;
    }

    /// <summary>Writes what an indicator is for.</summary>
    private static void Describe(Element indicator, PlannedBox box)
    {
        Set(indicator, CablingParameters.Recommendation, RecommendsJunctionBox);
        Set(indicator, CablingParameters.CircuitRefs, CircuitRefs(box.Circuits));
        Set(indicator, CablingParameters.TapCount, box.Entries);
    }

    /// <summary>
    /// The level an indicator is placed on: the nearest one at or below it.
    /// </summary>
    /// <remarks>
    /// <b>A modelling assumption, and it is stated rather than buried.</b> Revit wants a level for a
    /// placed instance, and the honest one for something hanging under a slab is the storey it
    /// belongs to - which reads as the nearest level not above it. The lowest level in the model,
    /// which is the cheap answer, would put every indicator in a tower on the ground floor and make
    /// every plan view of them empty.
    /// </remarks>
    private static Func<double, Level> Levels(Document host)
    {
        var levels = new FilteredElementCollector(host)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(one => one.Elevation)
            .ToList();

        return z =>
        {
            var chosen = levels[0];

            foreach (var level in levels)
            {
                if (level.Elevation <= z)
                    chosen = level;
            }

            return chosen;
        };
    }

    /// <summary>Tells the carriers and the boxes that were used which circuits run through them.</summary>
    /// <remarks>
    /// <b>Circuit element ids, in the one format an indicator carries</b> - the owner's decision of
    /// 2026-09-14. This wrote circuit numbers here while indicators got ids, so one parameter meant two
    /// different things depending on which element held it, and a schedule or filter over it could
    /// only ever be right about half of them. A number is also not an identity: two panels each have a
    /// circuit 1.
    /// </remarks>
    private static void References(Document host, RouteRun run, ApplyOutcome outcome)
    {
        var byElement = new Dictionary<long, List<CarrierId>>();
        var inLinks = 0;

        void Note(CarrierId element, CarrierId circuit)
        {
            if (element.IsLinked)
            {
                inLinks++;
                return;
            }

            if (!byElement.TryGetValue(element.Value, out var circuits))
                byElement[element.Value] = circuits = new List<CarrierId>();

            circuits.Add(circuit);
        }

        foreach (var route in run.Results)
        {
            if (route.Status != RouteStatus.Found)
                continue;

            foreach (var carrier in route.Path)
                Note(carrier, route.Circuit);
        }

        // A junction box already in the model gets the references and nothing else - the owner's
        // decision. It is somebody's element; what we may add to it is a reading of the model, not a
        // proposal about it.
        foreach (var box in run.Boxes)
        {
            if (box.Existing is not { } existing)
                continue;

            foreach (var circuit in box.Circuits)
                Note(existing.Id, circuit);
        }

        foreach (var pair in byElement)
        {
            if (host.GetElement(new ElementId(pair.Key)) is not { } element)
                continue;

            if (Set(element, CablingParameters.CircuitRefs, CircuitRefs(pair.Value)))
                outcome.CarriersMarked++;
        }

        outcome.InLinks = inLinks;
    }

    /// <summary>
    /// Posts the five conditions the owner chose, for Revit to show when the apply commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All five are warnings, never errors.</b> An error rolls the transaction back, and each of
    /// these describes work that did happen and should stay: a circuit the router could not reach is
    /// still a circuit the rest of the run served.
    /// </para>
    /// <para>
    /// <b>A message says what was registered, and the element says which one - because the
    /// per-occurrence string is not ours to set.</b> This was written the other way round first, on
    /// the strength of <c>FailureMessage.SetMessageString</c> appearing in the reference XML for all
    /// four releases. The compiler refused it on all four alike, and the XML article for that member
    /// carries no summary at all, only its exceptions; Autodesk's own six calls across two SDKs
    /// construct <c>new FailureMessage(id)</c> and post it without setting any text. So the wording
    /// registered with the definition is the whole of what Revit shows, and it has to stand on its
    /// own for every element it will ever describe.
    /// </para>
    /// <para>
    /// <b>What the specifics cost is nothing, because they were never only here.</b>
    /// <c>SetFailingElement</c> does compile, so each warning selects its own circuit, box or
    /// indicator in the model - which is the part somebody acts on. The number, the value that could
    /// not be read and the address the route stopped at are on the result screen and in the log,
    /// where they were before any of this was posted.
    /// </para>
    /// </remarks>
    private static void Warn(
        Document host,
        RouteRun run,
        CablingSnapshot snapshot,
        IReadOnlyList<ElementId> joined,
        ApplyOutcome outcome)
    {
        foreach (var route in run.Blocked(RouteStatus.NoCarrierNear))
            Post(host, outcome, CablingFeature.NoCarrierNear, route.Circuit.Value);

        foreach (var route in run.Blocked(RouteStatus.NoConnectivity))
            Post(host, outcome, CablingFeature.NoConnectivity, route.Circuit.Value);

        foreach (var id in snapshot.Circuits.UnreadableConnectionIds)
            Post(host, outcome, CablingFeature.ConnectionUnreadable, id);

        foreach (var id in snapshot.BoxesUnconnectedIds)
            Post(host, outcome, CablingFeature.JunctionBoxJoinedToNothing, id);

        foreach (var id in joined)
            Post(host, outcome, CablingFeature.IndicatorJoinedIntoNetwork, id.Value);
    }

    /// <summary>Posts one warning against one element, when the element is still there to post against.</summary>
    /// <remarks>
    /// The existence check is not defensive noise: these ids were read before the transaction opened,
    /// and posting against an element somebody deleted in between throws inside our own transaction.
    /// </remarks>
    private static void Post(Document host, ApplyOutcome outcome, FailureDefinitionId id, long element)
    {
        var target = new ElementId(element);

        if (host.GetElement(target) is null)
            return;

        var failure = new FailureMessage(id);
        failure.SetFailingElement(target);

        host.PostFailure(failure);
        outcome.Warnings++;
    }

    /// <summary>
    /// Tells every circuit that routed its length, how it was routed, and what it ran through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the circuits that routed, and what is already there is left alone.</b> A circuit the
    /// search could not finish has no length to store, and a zero written in its place is a number
    /// somebody schedules. The previous run's three still describe the model they were computed on -
    /// which is what lets a check tell a stale answer from an absent one, and is the whole point of
    /// the stamp.
    /// </para>
    /// <para>
    /// <b>The three are one write.</b> A length without the connection it was computed with cannot be
    /// checked by anyone - the two connections differ by about 38 % on the owner's own model - and
    /// without the carriers it was measured along, "has this changed" has no answer but re-running
    /// everything and comparing numbers. So they are counted together, and a circuit counts when
    /// Revit accepted any of them: the alternative is to report nothing about a model that now holds
    /// two values out of three, which is the state hardest to reason about later.
    /// </para>
    /// <para>
    /// <b>Circuits are host elements by construction</b> - the reader takes them from the document
    /// that owns the panel - so there is no link to skip here, unlike the carriers in
    /// <see cref="References"/>. An id that no longer resolves is stepped over the same way, because
    /// between the read and the write somebody may have deleted the circuit.
    /// </para>
    /// </remarks>
    private static void Lengths(Document host, RouteRun run, ApplyOutcome outcome)
    {
        foreach (var route in run.Results)
        {
            if (route.Status != RouteStatus.Found)
                continue;

            if (host.GetElement(new ElementId(route.Circuit.Value)) is not { } circuit)
                continue;

            var written = Set(circuit, CablingParameters.CableLength, route.TotalLength);

            written |= Set(circuit, CablingParameters.RouteConnection, CircuitConnections.Text(route.Connection));
            written |= Set(circuit, CablingParameters.RouteStamp, RouteStamp(route.Path));

            if (written)
                outcome.CircuitsWritten++;
        }
    }

    /// <summary>
    /// The one value of <c>BHS_Cbl_CircuitRefs</c>, on an indicator, a carrier and a box alike.
    /// </summary>
    /// <remarks>
    /// Circuit element ids in invariant digits, each once, in the order first seen, joined by "; ".
    /// One helper for all three writers, so that the rule cannot be kept in one place and drift in
    /// another - which is how the same parameter came to hold ids on indicators and numbers on
    /// carriers. The repeats are dropped here rather than trusted to each caller: a box lists its
    /// circuits once already, a carrier walked by two taps of one route would otherwise name it twice.
    /// </remarks>
    private static string CircuitRefs(IEnumerable<CarrierId> circuits) =>
        Ids(circuits, one => one.Value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The one value of <c>BHS_Cbl_RouteStamp</c>: the carriers a stored length was measured along.</summary>
    /// <remarks>
    /// <b>The same rule as <see cref="CircuitRefs"/> and one deliberate difference: the id is
    /// qualified.</b> A carrier in a link is written <c>&lt;link instance&gt;:&lt;element&gt;</c>, because
    /// an element id alone means different elements in the host and in each link - and carriers live in
    /// links routinely, 13 of the 49 on the owner's set. Circuits never do, so the references keep the
    /// bare number. The qualified spelling is <c>CarrierId.ToString</c> itself rather than a second
    /// rendering written here, which is also what the reach notes print.
    /// </remarks>
    private static string RouteStamp(IEnumerable<CarrierId> path) => Ids(path, one => one.ToString());

    /// <summary>Ids, each once, in the order first seen, joined by "; ".</summary>
    /// <remarks>
    /// The shape both values share, held once. What differs between them is how a single id is
    /// spelled, and that is the argument - so the part that could drift silently cannot, and the part
    /// that must differ is named where it differs.
    /// </remarks>
    private static string Ids(IEnumerable<CarrierId> ids, Func<CarrierId, string> spell)
    {
        var seen = new HashSet<CarrierId>();
        var values = new List<string>();

        foreach (var id in ids)
        {
            if (seen.Add(id))
                values.Add(spell(id));
        }

        return string.Join("; ", values);
    }

    private static bool Set(Element element, Guid parameter, string value)
    {
        var found = element?.get_Parameter(parameter);

        return found is { IsReadOnly: false } && found.Set(value);
    }

    private static bool Set(Element element, Guid parameter, int value)
    {
        var found = element?.get_Parameter(parameter);

        return found is { IsReadOnly: false } && found.Set(value);
    }

    /// <summary>Sets a length, in Revit's internal units.</summary>
    /// <remarks>
    /// Internal feet, raw, exactly as the search computed them: the document's units decide how the
    /// number is shown, and converting here would store one project's display in every project's
    /// model. The predecessor formatted its breakdown through <c>CurrentCulture</c> and left "30,2"
    /// or "30.2" in the model depending on whose Revit wrote it.
    /// </remarks>
    private static bool Set(Element element, Guid parameter, double value)
    {
        var found = element?.get_Parameter(parameter);

        return found is { IsReadOnly: false } && found.Set(value);
    }
}
