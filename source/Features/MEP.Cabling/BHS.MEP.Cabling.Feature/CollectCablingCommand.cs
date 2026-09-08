using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.MEP.Cabling.Revit;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// Reads the model's cable-bearing structure and says what it found.
/// </summary>
/// <remarks>
/// <para>
/// <b>The collect phase, alone, and deliberately shipped before the other two.</b> The snapshot
/// builder has never executed - it compiles on four target frameworks, which this repository has
/// twice learned says nothing about running. A command that only collects turns that into a fact
/// somebody can read, on a real model, without waiting for a search or a screen to exist.
/// </para>
/// <para>
/// It also puts the counts of what was skipped in front of a person for the first time. Those
/// numbers were written so that nothing is lost silently; until now nothing displayed them, which
/// is its own kind of silence.
/// </para>
/// <para>
/// <b>Synchronous, and that is measured rather than assumed.</b> Reads need no pump from inside a
/// command: it already stands on the API thread in a valid API context. The background thread and
/// the progress it makes possible belong to the compute phase, which is not here yet.
/// </para>
/// </remarks>
public sealed class CollectCablingCommand : IFeatureCommand
{
    /// <summary>
    /// Counts snapshots taken in this process, so that each carries a version later ones can beat.
    /// </summary>
    /// <remarks>
    /// <see cref="Routing.RouteNetwork.Version"/> is compared and never interpreted, so a counter is
    /// enough - and a counter is honest about what it is. A timestamp would look like it meant
    /// something. It resets with the process, which is correct: a result from a previous session is
    /// older than any snapshot taken in this one.
    /// </remarks>
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
        var version = Interlocked.Increment(ref _version);

        var snapshot = CablingSnapshot.Build(document, options, new CarrierCatalogue(), version);

        Report(services, snapshot);
        Show(snapshot);

        return Result.Succeeded;
    }

    /// <summary>
    /// Writes the whole finding to the log, where it survives the dialog being dismissed.
    /// </summary>
    /// <remarks>
    /// The dialog is for the person standing there; the log is for the person asked about it
    /// afterwards. This repository has already had one measurement that existed only in a terminal
    /// somebody had closed.
    /// </remarks>
    private static void Report(IUiFeatureServices services, CablingSnapshot snapshot)
    {
        var log = services.Log;

        log.Info(
            "cabling: {0} carriers, {1} circuits, from {2} link(s)",
            snapshot.Network.Count,
            snapshot.Circuits.Circuits.Count,
            snapshot.LinksRead);

        // Info rather than Warn: a reserved way in a panel schedule is somebody doing their job.
        if (snapshot.Circuits.SpareOrSpace > 0)
            log.Info("cabling: {0} circuit(s) are spare or space, and have nothing to route",
                snapshot.Circuits.SpareOrSpace);

        if (snapshot.CarriersSkipped > 0)
            log.Warn("cabling: {0} carrier(s) had no readable extent and are missing from the network",
                snapshot.CarriersSkipped);

        if (snapshot.LinksNotLoaded > 0)
            log.Warn("cabling: {0} link(s) are placed but not loaded, so their carriers are absent",
                snapshot.LinksNotLoaded);

        if (snapshot.NestedLinksIgnored > 0)
            log.Warn("cabling: {0} link(s) inside links were not followed", snapshot.NestedLinksIgnored);

        if (snapshot.Circuits.WithoutPanel > 0)
            log.Warn("cabling: {0} circuit(s) have no panel to start from", snapshot.Circuits.WithoutPanel);

        if (snapshot.Circuits.WithoutDevices > 0)
            log.Warn("cabling: {0} circuit(s) have no device with a reachable point",
                snapshot.Circuits.WithoutDevices);

        if (snapshot.Circuits.DevicesSkipped > 0)
            log.Warn("cabling: {0} device(s) were dropped from circuits that were otherwise described",
                snapshot.Circuits.DevicesSkipped);
    }

    /// <summary>
    /// Shows the finding, with everything that was left out named rather than summarised away.
    /// </summary>
    /// <remarks>
    /// <b>A <c>TaskDialog</c> on purpose, for now.</b> A window of our own is the next phase's work
    /// and carries the whole UI set with it; borrowing Revit's own dialog costs nothing, looks like
    /// Revit, and cannot quietly become the design. When the real screen arrives this goes.
    /// </remarks>
    private static void Show(CablingSnapshot snapshot)
    {
        // Spare and space circuits are named in the main body rather than under "details", because
        // they are part of the answer to "what is in this model" and not part of what went wrong.
        var spare = snapshot.Circuits.SpareOrSpace == 0
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                "\nSpare or space, nothing to route: {0}",
                snapshot.Circuits.SpareOrSpace);

        var found = string.Format(
            CultureInfo.CurrentCulture,
            "Carriers: {0}\nCircuits: {1}\nLinks read: {2}{3}",
            snapshot.Network.Count,
            snapshot.Circuits.Circuits.Count,
            snapshot.LinksRead,
            spare);

        var missing = Missing(snapshot);

        var dialog = new TaskDialog("Cable-bearing structure")
        {
            MainInstruction = snapshot.Network.Count == 0
                ? "No cable-bearing elements were found"
                : "The model has been read",
            MainContent = found,
            ExpandedContent = missing.Length == 0 ? null : missing,
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        dialog.Show();
    }

    private static string Missing(CablingSnapshot snapshot)
    {
        var lines = new List<string>();

        Add(lines, snapshot.CarriersSkipped, "carriers with no readable geometry");
        Add(lines, snapshot.LinksNotLoaded, "links placed but not loaded");
        Add(lines, snapshot.NestedLinksIgnored, "links inside links, not followed");
        Add(lines, snapshot.Circuits.WithoutPanel, "circuits with no panel");
        Add(lines, snapshot.Circuits.WithoutDevices, "circuits with no reachable device");
        Add(lines, snapshot.Circuits.DevicesSkipped, "devices dropped from circuits that were described");

        return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
    }

    private static void Add(List<string> lines, int count, string what)
    {
        if (count > 0)
            lines.Add(count.ToString(CultureInfo.CurrentCulture) + " " + what);
    }
}
