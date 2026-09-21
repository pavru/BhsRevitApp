using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.MEP.Cabling.Revit;
using BHS.MEP.Cabling.Routing;
using BHS.MEP.Cabling.Ui;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// Says which circuits carry a length that no longer describes the model.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the reader the route stamp was written for.</b> The stamp was declared in 2026-09-16
/// with the rule "a parameter is declared together with whatever reads it" half-kept: the reader was
/// named by the owner and planned rather than existing. It exists here.
/// </para>
/// <para>
/// <b>It reads the model and routes it again, and the stamp saves neither.</b> That was said before
/// the parameter was chosen, not discovered after: what the stamp buys is the ability to say
/// <i>which</i> carrier left the route, and to select the stored path by id. Which is why this command
/// costs the same as "Route cables" and shows the same progress.
/// </para>
/// <para>
/// <b>It writes nothing - the owner's decision of 2026-09-17.</b> Recomputing is what "Route cables"
/// is for; a second place that writes lengths would be a second place to keep in step with the first.
/// What this one can do about what it finds is select it, so the same circuits show up in a schedule
/// and on the plan.
/// </para>
/// </remarks>
public sealed class CheckLengthsCommand : IFeatureCommand
{
    /// <inheritdoc cref="CollectCablingCommand"/>
    private static long _version;

    public Result Execute(IUiFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message)
    {
        var view = data?.Application?.ActiveUIDocument;
        var document = view?.Document;

        if (view is null || document is null)
        {
            message = "Open a model: this command needs a document.";
            return Result.Failed;
        }

        // Refused rather than trusted to the greyed button, for the reason on CollectCablingCommand.
        if (document.IsFamilyDocument)
        {
            message = "Open a project: this command does not work in the family editor.";
            return Result.Failed;
        }

        var log = services.Log;

        // On the API thread, like the routing command and for the same reason: the model layer of the
        // settings lives in the document, and everything after the search begins may not ask Revit
        // anything.
        var project = CablingProjectSettings.Read(services.ModelSettings.For(document));

        if (project.Unreadable.Length > 0)
            log.Warn("cabling: a project setting could not be read and its default is used - {0}", project.Unreadable);

        // After the project, because the search's tree numbers are the project's to state.
        var options = CablingOptions.Read(services.Settings, project);

        var model = new ReviewViewModel(
            (progress, token) => CheckAsync(document, options, project, progress, token, log),
            CablingLength.Formatter(document),
            circuits => Select(view, circuits, log));

        var window = new ReviewWindow(model);

        window.OwnedBy(Process.GetCurrentProcess().MainWindowHandle);
        window.StartWhenShown();
        window.ShowDialog();

        Report(log, model);
        return Result.Succeeded;
    }

    /// <summary>
    /// Reads the model and what it stores where that is legal, then searches and compares where it is not.
    /// </summary>
    /// <remarks>
    /// <b>Both reads happen before the <c>Task.Run</c>, and the second is the one worth naming.</b>
    /// What each circuit carries is a parameter on an element, so it is a Revit call like any other -
    /// reading it lazily while comparing would be a Revit call from a background thread, the failure
    /// this whole arrangement exists to make impossible. The comparison itself touches neither: it is
    /// arithmetic over two records, and it lives in the routing assembly, which cannot see a
    /// <c>Document</c> at all.
    /// </remarks>
    private static async Task<LengthReview> CheckAsync(
        Document document,
        RoutingOptions options,
        CablingProjectSettings project,
        IProgress<RoutingProgress> progress,
        CancellationToken token,
        ILog log)
    {
        progress.Report(new RoutingProgress("Reading the model", 0, 0));

        var version = Interlocked.Increment(ref _version);
        var clock = Stopwatch.StartNew();
        var snapshot = CablingSnapshot.Build(
            document, options, new CarrierCatalogue(), version, project.Boxes, project.DefaultConnection);

        var circuits = snapshot.Circuits.Described;
        var stored = StoredRoutes.Read(document, circuits.Select(one => one.Id));
        clock.Stop();

        log.Info(
            "cabling: read {0} carrier(s) and {1} circuit(s) in {2:F1} s",
            snapshot.Network.Count,
            circuits.Count,
            clock.Elapsed.TotalSeconds);

        log.Info("cabling: {0} circuit(s) carry a stored route", stored.Values.Count(one => one.Written));

        var numbers = new Dictionary<CarrierId, string>();

        foreach (var circuit in circuits)
            numbers[circuit.Id] = circuit.Number;

        progress.Report(new RoutingProgress("Routing the circuits again", 0, 0));

        return await Task.Run(
                () =>
                {
                    var results = new List<RouteResult>(circuits.Count);

                    foreach (var circuit in circuits)
                    {
                        token.ThrowIfCancellationRequested();

                        results.Add(Router.Route(
                            snapshot.Network,
                            circuit,
                            options,
                            project.ExistingBoxesOnly ? snapshot.Boxes : null));
                    }

                    // Planned here too, and for the same reason the routing command plans: a stored
                    // length is stale when it differs from the length computed today, and that length
                    // includes slack counted per place the cable is cut - which the plan decides.
                    var plan = BoxPlanner.Plan(results, snapshot.Boxes, project.BoxRadius);
                    var run = new RouteRun(results, snapshot.Network.Version, TimeSpan.Zero, plan, project.Slack);

                    return LengthReview.Of(
                        run,
                        stored,
                        StoredRoutes.Tolerance,
                        id => numbers.TryGetValue(id, out var number) ? number : string.Empty);
                },
                token)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Selects the circuits the check found, so they can be seen where the numbers are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Circuits are host elements, so there is nothing here to skip for a link</b> - the reader
    /// takes them from the document that owns the panel. An id that no longer resolves is stepped over:
    /// between the read and this press somebody may have deleted the circuit, and a selection that
    /// throws is a worse answer than a selection that is one short and says so in the log.
    /// </para>
    /// <para>
    /// <b>What Revit does with a selection set from inside a modal dialog is not measured.</b> Reads
    /// answer from there on all four releases and a transaction after an await commits, both measured;
    /// a selection is neither, so the failure is caught and logged rather than thrown into a window
    /// whose whole subject is somebody else's numbers.
    /// </para>
    /// </remarks>
    private static void Select(UIDocument view, IReadOnlyList<long> circuits, ILog log)
    {
        try
        {
            var ids = new List<ElementId>(circuits.Count);

            foreach (var circuit in circuits)
            {
                var id = new ElementId(circuit);

                if (view.Document.GetElement(id) is not null)
                    ids.Add(id);
            }

            view.Selection.SetElementIds(ids);
            log.Info("cabling: selected {0} circuit(s) with a stale length", ids.Count);
        }
        catch (Exception error)
        {
            log.Warn(error, "cabling: the stale circuits could not be selected");
        }
    }

    /// <summary>One line in the log for one press, whatever came of it.</summary>
    private static void Report(ILog log, ReviewViewModel model)
    {
        if (model.HasFailure)
        {
            log.Warn("cabling: the length check did not finish - {0}", model.Failure);
            return;
        }

        if (model.Review is not { } review)
            return;

        log.Info(
            "cabling: {0} of {1} circuit(s) carry a stale length",
            review.Stale.Count,
            review.Examined);

        log.Info(
            "cabling: {0} circuit(s) are current and {1} routed but were never written",
            review.Current,
            review.NeverWritten);
    }
}
