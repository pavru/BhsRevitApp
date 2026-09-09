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
/// Routes every circuit through the model's cable-bearing structure, and shows what came of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape is measured rather than chosen.</b> A modal window owns the API thread, which is
/// also Revit's UI thread, so work done on it freezes Revit and freezes our own window with it - a
/// progress bar that cannot repaint is worse than none. Measured on all four releases: the API still
/// answers reads from inside the window, an <c>await</c> taken there resumes on the API thread, and
/// work handed to the pump does not run until the window closes. So the command reads
/// synchronously, searches on a background thread with the bar alive, and will apply in the same
/// continuation - without closing the window first, which handing the write to the pump would
/// require.
/// </para>
/// <para>
/// <b>Nothing here writes.</b> Applying a route is a transaction, and this repository has not yet
/// measured what a transaction started after an <c>await</c> inside a modal window does. That
/// measurement travels with the phase that needs it.
/// </para>
/// </remarks>
public sealed class RouteCablingCommand : IFeatureCommand
{
    /// <inheritdoc cref="CollectCablingCommand"/>
    private static long _version;

    public Result Execute(IUiFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message)
    {
        var document = data?.Application?.ActiveUIDocument?.Document;

        if (document is null)
        {
            message = "Open a model: this command needs a document.";
            return Result.Failed;
        }

        var options = CablingOptions.Read(services.Settings);
        var log = services.Log;

        var model = new RoutingViewModel(
            (progress, token) => ComputeAsync(document, options, progress, token, log),
            CablingLength.Formatter(document));

        var window = new RoutingWindow(model);

        // Owned by Revit's main window, so it is modal to Revit rather than to nothing: a WPF dialog
        // with no owner is modal only to its own application, which inside Revit is nobody.
        window.OwnedBy(Process.GetCurrentProcess().MainWindowHandle);
        window.StartWhenShown();
        window.ShowDialog();

        Report(log, model);
        return Result.Succeeded;
    }

    /// <summary>
    /// Reads the model where that is legal, then searches where it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is exact and load-bearing: everything before the <c>Task.Run</c> touches Revit and
    /// runs on the API thread, and everything inside it touches only the snapshot. Nothing is fetched
    /// lazily across that line - a field left for later would be a Revit call from a background
    /// thread, which is the failure the whole arrangement exists to make impossible, and which the
    /// routing assembly enforces by not being able to see <c>Document</c> at all.
    /// </para>
    /// <para>
    /// <b>The read is not cancellable, and the bar says so by being indeterminate.</b> It is one pass
    /// over the model inside Revit's own collectors; there is no point in it where stopping is a
    /// thing we can ask for. Pretending otherwise would put a Stop button in front of somebody that
    /// does nothing for the half of the wait that is longest on a big model.
    /// </para>
    /// </remarks>
    private static Task<RouteRun> ComputeAsync(
        Document document,
        RoutingOptions options,
        IProgress<RoutingProgress> progress,
        CancellationToken token,
        ILog log)
    {
        progress.Report(new RoutingProgress("Reading the model", 0, 0));

        var version = Interlocked.Increment(ref _version);
        var read = Stopwatch.StartNew();
        var snapshot = CablingSnapshot.Build(document, options, new CarrierCatalogue(), version);
        read.Stop();

        log.Info(
            "cabling: read {0} carrier(s) and {1} circuit(s) in {2:F1} s",
            snapshot.Network.Count,
            snapshot.Circuits.Described.Count,
            read.Elapsed.TotalSeconds);

        return Task.Run(() => Search(snapshot, options, progress, token), token);
    }

    private static RouteRun Search(
        CablingSnapshot snapshot,
        RoutingOptions options,
        IProgress<RoutingProgress> progress,
        CancellationToken token)
    {
        var circuits = snapshot.Circuits.Described;
        var results = new List<RouteResult>(circuits.Count);
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < circuits.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var circuit = circuits[i];

            // The circuit's own number, not "34 of 55". The count says how long it will take and
            // nothing about where it is, and the one that hangs is the one worth naming.
            progress.Report(new RoutingProgress($"Routing {circuit.Number}", i + 1, circuits.Count));

            results.Add(Router.Route(snapshot.Network, circuit, options));
        }

        clock.Stop();

        var shape = snapshot.Network.Shape();
        var failedToCross = results.Any(one => one.Status == RouteStatus.NoConnectivity);

        // Measured on every run for now, because it is cheap against the read and because the
        // question it answers is open. It goes when the answer is in.
        token.ThrowIfCancellationRequested();
        var approach = ApproachStudy.Compare(snapshot.Network, circuits, options);

        return new RouteRun(results, snapshot.Network.Version, clock.Elapsed)
        {
            Approach = approach,
            Shape = shape,
            Reading = CablingGaps.Describe(snapshot),
            Tolerances = failedToCross ? Tolerances(snapshot, options, token) : Array.Empty<ToleranceReading>(),
        };
    }

    /// <summary>
    /// What other join tolerances would have made of the same carriers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured because guessing costs a press of the button each time.</b> A real model came
    /// back with twenty-six circuits of fifty-five unable to cross a structure that read as
    /// forty-four groups - which says the tolerance is a suspect and says nothing about what value
    /// would do. One rebuild per candidate answers that: it is a pass over a few hundred elements,
    /// against a model read measured in seconds.
    /// </para>
    /// <para>
    /// <b>It is evidence, not a recommendation, and the difference matters.</b> A wider tolerance
    /// joins runs a person reads as joined - and joins two that merely pass near each other, which
    /// produces routes nobody can build. The table says what each value does; choosing is a
    /// judgement about a particular model.
    /// </para>
    /// <para>
    /// Only larger candidates are tried. A smaller one cannot join what the current value did not,
    /// so a row for it is a line that cannot change the answer.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ToleranceReading> Tolerances(
        CablingSnapshot snapshot,
        RoutingOptions options,
        CancellationToken token)
    {
        var readings = new List<ToleranceReading>();

        foreach (var millimetres in CablingOptions.ToleranceLadder)
        {
            token.ThrowIfCancellationRequested();

            var candidate = CablingOptions.ToFeet(millimetres);

            if (candidate <= options.JoinTolerance)
                continue;

            var network = NetworkBuilder.Build(
                snapshot.Network.Version,
                snapshot.Carriers,
                new RoutingOptions
                {
                    JoinTolerance = candidate,
                    MaxApproach = options.MaxApproach,
                });

            readings.Add(new ToleranceReading(candidate, network.Shape()));
        }

        return readings;
    }

    /// <summary>
    /// Writes the run to the log, whatever became of it.
    /// </summary>
    /// <remarks>
    /// Including the runs that did not finish. A cancelled or failed run leaves nothing on screen
    /// once the window is closed, and "I pressed it and it did nothing" is a report nobody can act
    /// on; the file is where that answer has to be.
    /// </remarks>
    private static void Report(ILog log, RoutingViewModel model)
    {
        if (model.Run is not { } run)
        {
            log.Info("cabling: the run ended as {0} - {1}", model.Phase, model.What);

            if (model.Failure.Length > 0)
                log.Error("cabling: {0}", model.Failure);

            return;
        }

        log.Info(
            "cabling: {0} of {1} circuit(s) routed in {2:F1} s",
            run.Found,
            run.Results.Count,
            run.Took.TotalSeconds);

        foreach (var cause in run.Causes)
            log.Warn("cabling: {0} circuit(s) blocked - {1}", run.Count(cause), cause);

        // Only alongside the failure it explains. A structure line after a run where everything
        // routed is a true sentence in a file people read to find out what went wrong.
        if (run.Count(RouteStatus.NoConnectivity) > 0)
        {
            log.Warn(
                "cabling: the structure is {0} connected group(s) over {1} carrier(s), largest {2}",
                run.Shape.Groups,
                run.Shape.Carriers,
                run.Shape.Largest);
        }
    }
}
