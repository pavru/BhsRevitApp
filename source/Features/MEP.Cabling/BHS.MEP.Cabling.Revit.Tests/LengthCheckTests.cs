using System.Globalization;
using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// What the check for stale lengths makes of a model whose circuits carry an earlier answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The comparison itself is proved on paper, in the routing probe, and these cases do not repeat
/// it.</b> What only a live Revit can answer is the half that reads: that the three values an apply
/// writes come back out of the model as they went in, that a value nobody ever wrote reads as absent
/// rather than as zero, and that a stale one is told from a current one on real parameters.
/// </para>
/// <para>
/// <b>The values are written by the case, not by the apply, and that is deliberate.</b> The apply
/// writes what the search just found, so a model it has touched carries nothing stale by construction;
/// every reason this check exists for is a value that stopped being true, which only a case that puts
/// one there can produce. They are written through the same parameters by their own GUIDs, and the
/// harness rolls the whole of it back, binding included.
/// </para>
/// </remarks>
public sealed class LengthCheckTests : IRevitTestSuite
{
    public string Name => "Cabling lengths";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "what the search found, written onto its circuits, reads back as a current answer",
            WhatWasWrittenReadsBackCurrent,
            writes: true),

        new RevitTestCase(
            "a length, a stamp or a connection that no longer matches is reported, and so is one stored where nothing routes",
            EachReasonIsReported,
            writes: true),

        new RevitTestCase(
            "the circuits a check found can be selected by id, and the selection reads back",
            StaleCircuitsCanBeSelected,
            writes: false),
    };

    /// <summary>The routing options these cases search with - the suite's own, as the others use.</summary>
    /// <remarks>
    /// Wider than the command's, like <c>CablingApplyTests</c>: a joint of half a foot and a reach of ten.
    /// Changing them is the owner's decision; they are here so that a model which routes for the apply
    /// cases routes for these.
    /// </remarks>
    private static RoutingOptions Options { get; } = new()
    {
        JoinTolerance = 0.5,
        MaxApproach = 10,
        AxisAlignedApproach = true,
    };

    private static void WhatWasWrittenReadsBackCurrent(RevitTestContext context)
    {
        var document = context.Document!;
        var plan = Route(context, document, "current");

        Skip.When(plan.Found.Count == 0, "no circuit of the model this sweep opened routed, so there is nothing to store");

        Bind(context, document);
        Write(document, plan.Found, "storing what the search found");

        var stored = StoredRoutes.Read(document, plan.Results.Select(one => one.Circuit));
        var review = LengthReview.Of(plan.Results, stored, StoredRoutes.Tolerance);

        context.Note("current: circuits with a stored answer", Count(stored.Values.Count(one => one.Written)));
        context.Note("current: circuits reported stale", Count(review.Stale.Count));

        Expect.That(
            !review.Any,
            "circuits reported stale right after their own answer was stored: "
            + string.Join(", ", review.Stale.Select(one => Id(one.Circuit) + " " + one.Reasons)));

        Expect.Same(plan.Found.Count, review.Current, "routes found, against circuits whose stored answer still holds");

        Expect.Same(
            0,
            review.NeverWritten,
            "circuits that routed and carry nothing, after every found route was written");

        // The other half of "absent is not zero": the circuits that did not route were never written,
        // and the check must not invent an answer for them.
        var blank = plan.Results
            .Where(one => one.Status != RouteStatus.Found)
            .Count(one => stored.TryGetValue(one.Circuit, out var was) && was.Written);

        Expect.Same(0, blank, "circuits that did not route and were nonetheless read as carrying a stored answer");
    }

    private static void EachReasonIsReported(RevitTestContext context)
    {
        var document = context.Document!;
        var plan = Route(context, document, "stale");

        Skip.When(plan.Found.Count < 3, "the model this sweep opened gives fewer than three found routes, which this case needs");

        var blocked = plan.Results.FirstOrDefault(one => one.Status != RouteStatus.Found);

        Skip.When(blocked is null, "every circuit of this model routes, so nothing can be stored where no route is found");

        Bind(context, document);
        Write(document, plan.Found, "storing what the search found");

        var longer = plan.Found[0];
        var elsewhere = plan.Found[1];
        var otherWay = plan.Found[2];

        // One transaction for the four edits, so that a failure leaves the model in one state rather
        // than in four. Each is a value somebody's model could hold after a change nobody told us about.
        Change(document, "making four stored answers stale", () =>
        {
            Set(document, longer.Circuit, CablingParameters.CableLength, longer.TotalLength + 1);
            Set(document, elsewhere.Circuit, CablingParameters.RouteStamp, Stamp(elsewhere.Path) + "; 999999999");
            Set(document, otherWay.Circuit, CablingParameters.RouteConnection, Other(otherWay.Connection));

            Set(document, blocked!.Circuit, CablingParameters.CableLength, 12.34);
            Set(document, blocked.Circuit, CablingParameters.RouteConnection, Word(CircuitConnection.AtTerminal));
            Set(document, blocked.Circuit, CablingParameters.RouteStamp, Stamp(longer.Path));
        });

        var stored = StoredRoutes.Read(document, plan.Results.Select(one => one.Circuit));
        var review = LengthReview.Of(plan.Results, stored, StoredRoutes.Tolerance);
        var reasons = review.Stale.ToDictionary(one => one.Circuit, one => one);

        context.Note("stale: circuits reported", Count(review.Stale.Count));
        context.Note("stale: circuits still current", Count(review.Current));

        Expect.Same(4, review.Stale.Count, "circuits made stale by this case, against circuits the check reports");

        Expect.That(
            Only(reasons, longer.Circuit, StaleReason.LengthDiffers),
            Id(longer.Circuit) + ": a foot added to the stored length is reported as a length that no longer matches, "
            + "and as nothing else - " + Told(reasons, longer.Circuit));

        Expect.That(
            Only(reasons, elsewhere.Circuit, StaleReason.CarriersDiffer),
            Id(elsewhere.Circuit) + ": a carrier added to the stored stamp is reported as other carriers, and as "
            + "nothing else - " + Told(reasons, elsewhere.Circuit));

        Expect.That(
            reasons.TryGetValue(elsewhere.Circuit, out var named)
            && named.Left.Any(one => one.Value == 999999999)
            && named.Arrived.Count == 0,
            Id(elsewhere.Circuit) + ": the carrier the stamp names and the route no longer walks is named as having left");

        Expect.That(
            Only(reasons, otherWay.Circuit, StaleReason.ConnectionDiffers),
            Id(otherWay.Circuit) + ": the other connection stored is reported as the other connection, and as nothing "
            + "else - " + Told(reasons, otherWay.Circuit));

        Expect.That(
            Only(reasons, blocked!.Circuit, StaleReason.NoRouteNow),
            Id(blocked.Circuit) + ": a length stored where the search now finds no route is reported as that, and as "
            + "nothing else - " + Told(reasons, blocked.Circuit));

        Expect.That(
            reasons.TryGetValue(blocked.Circuit, out var gone) && gone.Left.Count > 0,
            Id(blocked.Circuit) + ": the carriers the stored length was measured along are still named");
    }

    /// <summary>
    /// What the window's button does, asked where it can be measured: from inside a pump post.
    /// </summary>
    /// <remarks>
    /// <b>This is not the window, and the case says so rather than implying otherwise.</b> The command
    /// selects from inside a modal dialog, and what Revit does with a selection set from there is not
    /// measured - reads answer and a transaction commits, both measured, a selection neither. What is
    /// measured here is the call itself against this Revit: the ids go in and come back out.
    /// </remarks>
    private static void StaleCircuitsCanBeSelected(RevitTestContext context)
    {
        var document = context.Document!;
        var view = context.Application.ActiveUIDocument;

        Skip.When(view is null, "this sweep has no active view, so there is no selection to set");

        var circuits = new CircuitReader().Read(document).Described.Take(2).Select(one => one.Id.Value).ToList();

        Skip.When(circuits.Count == 0, "the model this sweep opened describes no circuit to select");

        var before = view!.Selection.GetElementIds().ToList();

        try
        {
            var ids = circuits.Select(one => new ElementId(one)).ToList();

            view.Selection.SetElementIds(ids);

            var selected = view.Selection.GetElementIds().Select(one => one.Value).OrderBy(one => one).ToList();

            context.Note("selection: circuits asked for", Count(ids.Count));
            context.Note("selection: elements selected", Count(selected.Count));

            Expect.That(
                selected.SequenceEqual(circuits.OrderBy(one => one)),
                "the circuits asked for against the ones selected - asked "
                + string.Join(", ", circuits.OrderBy(one => one))
                + ", selected " + string.Join(", ", selected));
        }
        finally
        {
            // Restored because a selection is the person's, not ours: the sweep runs against a model
            // somebody may be looking at, and the harness rolls back documents rather than selections.
            view.Selection.SetElementIds(before);
        }
    }

    /// <summary>What the search finds on this model, with the circuits it describes.</summary>
    private static (IReadOnlyList<RouteResult> Results, IReadOnlyList<RouteResult> Found) Route(
        RevitTestContext context,
        Document document,
        string where)
    {
        var project = CablingProjectSettings.Read(new FixedSettings());
        var snapshot = CablingSnapshot.Build(
            document, Options, new CarrierCatalogue(), version: 1, project.Boxes, project.DefaultConnection);

        var results = snapshot.Circuits.Described
            .Select(circuit => Router.Route(snapshot.Network, circuit, Options))
            .ToList();

        var found = results.Where(one => one.Status == RouteStatus.Found).ToList();

        context.Note(where + ": circuits described", Count(results.Count));
        context.Note(where + ": routes found", Count(found.Count));

        return (results, found);
    }

    private static void Bind(RevitTestContext context, Document document)
    {
        var scheme = new CablingParameters();

        scheme.Export(context.Application.Application);
        scheme.Install(document, context.Application.Application);

        var missing = scheme.Missing(document)
            .Where(one => one.Id == CablingParameters.CableLength
                || one.Id == CablingParameters.RouteConnection
                || one.Id == CablingParameters.RouteStamp)
            .ToList();

        Expect.That(
            missing.Count == 0,
            "the three parameters this case reads, after binding them: "
            + string.Join(", ", missing.Select(one => one.Id.ToString())) + " are not bound");
    }

    /// <summary>Stores what the search found, the way an apply would.</summary>
    /// <remarks>
    /// The stamp is spelled here rather than taken from <c>RouteStamp</c>: this case exists to show that
    /// a stored answer reads back as current, and taking the spelling from the code that also reads it
    /// would make the two agree through the one thing being checked.
    /// </remarks>
    private static void Write(Document document, IReadOnlyList<RouteResult> found, string what) =>
        Change(document, what, () =>
        {
            foreach (var route in found)
            {
                Set(document, route.Circuit, CablingParameters.CableLength, route.TotalLength);
                Set(document, route.Circuit, CablingParameters.RouteConnection, Word(route.Connection));
                Set(document, route.Circuit, CablingParameters.RouteStamp, Stamp(route.Path));
            }
        });

    private static void Change(Document document, string what, Action write)
    {
        using var transaction = new Transaction(document, "BHS test: " + what);

        var started = transaction.Start();

        Expect.That(started == TransactionStatus.Started, "starting the transaction for " + what + ": Revit returned " + started);

        write();

        var committed = transaction.Commit();

        Expect.That(
            committed == TransactionStatus.Committed,
            "committing the transaction for " + what + ": Revit returned " + committed);
    }

    private static void Set(Document document, CarrierId circuit, Guid parameter, double value) =>
        Parameter(document, circuit, parameter)?.Set(value);

    private static void Set(Document document, CarrierId circuit, Guid parameter, string value) =>
        Parameter(document, circuit, parameter)?.Set(value);

    private static Parameter? Parameter(Document document, CarrierId circuit, Guid parameter)
    {
        var found = document.GetElement(new ElementId(circuit.Value))?.get_Parameter(parameter);

        Expect.That(
            found is { IsReadOnly: false },
            Id(circuit) + ": the parameter " + parameter + " is there to write");

        return found;
    }

    /// <summary>The carriers of a route, spelled the way the stamp has to spell them.</summary>
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

    private static string Word(CircuitConnection connection) =>
        connection == CircuitConnection.AtJunctionBox
            ? CablingParameters.ConnectionAtJunctionBox
            : CablingParameters.ConnectionAtTerminal;

    private static string Other(CircuitConnection connection) =>
        connection == CircuitConnection.AtJunctionBox
            ? CablingParameters.ConnectionAtTerminal
            : CablingParameters.ConnectionAtJunctionBox;

    private static bool Only(IReadOnlyDictionary<CarrierId, StaleCircuit> reasons, CarrierId circuit, StaleReason reason) =>
        reasons.TryGetValue(circuit, out var stale) && stale.Reasons == reason;

    private static string Told(IReadOnlyDictionary<CarrierId, StaleCircuit> reasons, CarrierId circuit) =>
        reasons.TryGetValue(circuit, out var stale) ? "told " + stale.Reasons : "not reported at all";

    private static string Id(CarrierId circuit) =>
        "circuit " + circuit.Value.ToString(CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
