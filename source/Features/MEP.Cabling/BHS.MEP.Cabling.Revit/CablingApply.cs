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
/// <b>Refusals are a first-class outcome, not an exception.</b> Every way this can decline to write
/// is a condition about the model or the project - a family that is not loaded, a parameter that
/// could not be bound - and none of them is a fault in the caller. A thrown exception would reach a
/// modal window as a type name; a refusal reaches it as the sentence somebody acts on.
/// </remarks>
public sealed class ApplyOutcome
{
    internal ApplyOutcome(IReadOnlyList<string> refusals) => Refusals = refusals;

    /// <summary>Why nothing was written. Empty when something was.</summary>
    public IReadOnlyList<string> Refusals { get; }

    public bool Refused => Refusals.Count > 0;

    /// <summary>Indicators newly placed.</summary>
    public int Placed { get; internal set; }

    /// <summary>Indicators from a previous run that were found and rewritten in place.</summary>
    public int Updated { get; internal set; }

    /// <summary>Indicators of ours that this run no longer recommends, and took away.</summary>
    public int Removed { get; internal set; }

    /// <summary>Indicators of ours left alone because somebody has since joined them to the structure.</summary>
    /// <remarks>
    /// <b>Counted rather than deleted, and this is the guard that makes removal safe at all.</b> A
    /// marker is joined to nothing by construction - measured on the owner's model, a freshly placed
    /// one has four connectors and none of them connected. So an element of our type that <i>is</i>
    /// joined to the structure is one somebody adopted into the wiring, and it stopped being ours
    /// the moment they did.
    /// </remarks>
    public int Adopted { get; internal set; }

    /// <summary>Junction boxes already in the model that the plan used instead of recommending one.</summary>
    public int ExistingUsed { get; internal set; }

    /// <summary>Carriers that were told which circuits run through them.</summary>
    public int CarriersMarked { get; internal set; }

    /// <summary>Elements that could not be written to because they live in a link.</summary>
    /// <remarks>
    /// Said out loud rather than skipped in silence. A model whose trays are all in a link gets no
    /// references written at all, and the difference between "nothing to write" and "nowhere to
    /// write it" is the whole of what somebody needs to know.
    /// </remarks>
    public int InLinks { get; internal set; }

    /// <summary>Warnings posted into the model's own warning list.</summary>
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
/// <b>Nothing is written into a link, ever.</b> A linked document is not modifiable from the host,
/// and the carriers of a real project routinely live in one - measured, a surveyed model held 13
/// trays and 71 conduits in a link. So the references have nowhere to go for those, and the count of
/// what was skipped travels back rather than the write quietly doing less than it says.
/// </para>
/// <para>
/// <b>Warnings are posted here because here is the only place they can be.</b>
/// <c>Document.PostFailure</c> is legal inside a transaction and nowhere else, and the compute phase
/// has none - which is why the same four conditions appear twice, as lines on the result screen and
/// as entries in Revit's warning list. The screen is read once, by whoever pressed the button; the
/// warning list is read later, by whoever reviews the model.
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

        scheme.Install(host, application, runtime);

        // The loud check the empty category list makes necessary. Two of the three parameters
        // declare no categories at compile time - the indicator's family is the project's to choose
        // - so a caller who did not pass the runtime category would bind nothing at all and nothing
        // would say so. Asked after binding rather than trusted: Install reports what it bound, and
        // what matters here is what is bound now.
        var missing = scheme.Missing(host, runtime)
            .Where(one => one.Id == CablingParameters.Recommendation
                          || one.Id == CablingParameters.CircuitRefs
                          || one.Id == CablingParameters.TapCount)
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
        transaction.Start();

        // Once, and before anything is placed: an inactive symbol places nothing and says nothing
        // about why. Autodesk's own samples do exactly this, immediately before NewFamilyInstance.
        if (!symbol.IsActive)
            symbol.Activate();

        Indicators(host, symbol, run, project, outcome);
        References(host, run, snapshot, outcome);
        Warn(host, run, snapshot, outcome);

        transaction.Commit();
        return outcome;
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
    /// last one is what keeps a designer's work safe: a marker somebody has cut into the wiring is a
    /// box now, whatever family it came from, and taking it away would delete part of the model to
    /// tidy up after ourselves.
    /// </para>
    /// </remarks>
    private static void Indicators(
        Document host,
        FamilySymbol symbol,
        RouteRun run,
        CablingProjectSettings project,
        ApplyOutcome outcome)
    {
        var wanted = run.Boxes.Where(box => box.IsRecommendation).ToList();
        var standing = Standing(host, symbol);
        var taken = new bool[standing.Count];
        var level = Levels(host);

        foreach (var box in wanted)
        {
            var at = new XYZ(box.At.X, box.At.Y, box.At.Z);
            var found = Match(standing, taken, at, project.BoxRadius);

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

        for (var i = 0; i < standing.Count; i++)
        {
            if (taken[i])
                continue;

            // Joined to something, so not ours to remove any more - see the remarks above.
            if (Joined(standing[i]))
            {
                outcome.Adopted++;
                continue;
            }

            host.Delete(standing[i].Id);
            outcome.Removed++;
        }

        outcome.ExistingUsed = run.Boxes.Count - wanted.Count;
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

    /// <summary>The nearest unclaimed indicator within the radius, or -1.</summary>
    private static int Match(List<FamilyInstance> standing, bool[] taken, XYZ at, double radius)
    {
        var best = -1;
        var distance = double.MaxValue;

        for (var i = 0; i < standing.Count; i++)
        {
            if (taken[i] || (standing[i].Location as LocationPoint)?.Point is not { } point)
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

    /// <summary>Whether any connector of this element is joined to anything at all.</summary>
    private static bool Joined(FamilyInstance instance)
    {
        var manager = instance.MEPModel?.ConnectorManager;

        if (manager is null)
            return false;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector is not null && connector.IsConnected)
                return true;
        }

        return false;
    }

    /// <summary>Writes what an indicator is for.</summary>
    private static void Describe(Element indicator, PlannedBox box)
    {
        Set(indicator, CablingParameters.Recommendation, RecommendsJunctionBox);
        Set(indicator, CablingParameters.CircuitRefs, Refs(box.Circuits));
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
    private static void References(Document host, RouteRun run, CablingSnapshot snapshot, ApplyOutcome outcome)
    {
        var numbers = snapshot.Circuits.Described.ToDictionary(one => one.Id, one => one.Number);
        var byElement = new Dictionary<long, List<string>>();
        var inLinks = 0;

        void Note(CarrierId carrier, CarrierId circuit)
        {
            if (carrier.IsLinked)
            {
                inLinks++;
                return;
            }

            if (!numbers.TryGetValue(circuit, out var number))
                return;

            if (!byElement.TryGetValue(carrier.Value, out var list))
                byElement[carrier.Value] = list = new List<string>();

            if (!list.Contains(number))
                list.Add(number);
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

            if (Set(element, CablingParameters.CircuitRefs, Refs(pair.Value)))
                outcome.CarriersMarked++;
        }

        outcome.InLinks = inLinks;
    }

    /// <summary>
    /// Posts the four conditions the owner chose into the model's own warning list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All four are warnings, never errors.</b> An error rolls the transaction back, and each of
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
    /// own for every circuit it will ever describe.
    /// </para>
    /// <para>
    /// <b>What the specifics cost is nothing, because they were never only here.</b>
    /// <c>SetFailingElement</c> does compile, so each warning selects its own circuit or box in the
    /// model - which is the part somebody acts on. The number, the value that could not be read and
    /// the address the route stopped at are on the result screen and in the log, where they were
    /// before any of this was posted.
    /// </para>
    /// </remarks>
    private static void Warn(Document host, RouteRun run, CablingSnapshot snapshot, ApplyOutcome outcome)
    {
        foreach (var route in run.Blocked(RouteStatus.NoCarrierNear))
            Post(host, outcome, CablingFeature.NoCarrierNear, route.Circuit.Value);

        foreach (var route in run.Blocked(RouteStatus.NoConnectivity))
            Post(host, outcome, CablingFeature.NoConnectivity, route.Circuit.Value);

        foreach (var id in snapshot.Circuits.UnreadableConnectionIds)
            Post(host, outcome, CablingFeature.ConnectionUnreadable, id);

        foreach (var id in snapshot.BoxesUnconnectedIds)
            Post(host, outcome, CablingFeature.JunctionBoxJoinedToNothing, id);
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

    /// <summary>The circuits as one value, in the order they were first seen.</summary>
    private static string Refs(IEnumerable<CarrierId> circuits) =>
        string.Join("; ", circuits.Select(one => one.Value.ToString(CultureInfo.InvariantCulture)));

    private static string Refs(IEnumerable<string> numbers) => string.Join("; ", numbers);

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
}
