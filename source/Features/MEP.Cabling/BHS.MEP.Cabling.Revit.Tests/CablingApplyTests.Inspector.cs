using System.Globalization;
using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>The inspector pane's reader, against what an apply wrote.</summary>
public sealed partial class CablingApplyTests
{
    /// <summary>
    /// After an apply, the inspector's reader returns what the apply wrote - on circuits, indicators and
    /// carriers alike - and reads a circuit the run did not route as never written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pane is WPF; what it shows is <see cref="CablingInspection"/>, and that is what this asserts.</b>
    /// Nothing the pane does is computing, so the only way it can be wrong about a value is to read the wrong
    /// one, or to read a written value as absent, or an absent one as zero - each asked here against the model
    /// the apply has just written, with the value read a second time by this suite's own code rather than by
    /// the reader.
    /// </para>
    /// <para>
    /// <b>"Never written" is asserted, not assumed.</b> The circuits that did not route are read before the
    /// apply, and the case stands down unless one of them carries no stored length at all - otherwise
    /// "never written" would be a claim about a value somebody's earlier run left there. The owner's decision
    /// for the pane is that such a circuit reads differently from a zero; this is where that is held.
    /// </para>
    /// <para>
    /// Built like the case for the length laid by where: every circuit cut in boxes, so that indicators are
    /// placed and carry a recommendation and a count; only the found routes handed to the apply, so that no
    /// warning needs an edition's failure definitions.
    /// </para>
    /// </remarks>
    private static void TheInspectorReadsWhatWasWritten(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new FixedSettings());
        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "inspector");

        var cut = CutEveryCircuitInBoxes(watch, document, application);
        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var results = snapshot.Circuits.Described.Select(circuit => Router.Route(snapshot.Network, circuit, Options)).ToList();
        var found = results.Where(one => one.Status == RouteStatus.Found).ToList();

        // A circuit the run did not route and nobody ever wrote: "never written" has to be true of it before
        // the apply for the reader's answer after the apply to mean anything.
        var blank = results
            .Where(one => one.Status != RouteStatus.Found)
            .Select(one => one.Circuit.Value)
            .Where(one => Stored(document, one).Replace("|", string.Empty).Trim().Length == 0)
            .ToList();

        Note(context, "inspector: circuits cut in boxes", cut);
        Note(context, "inspector: routes found", found.Count);
        Note(context, "inspector: circuits not routed and never written", blank.Count);

        Skip.When(found.Count == 0, "no circuit of this model routed, so the apply writes nothing to read back");
        Skip.When(blank.Count == 0, "every circuit of this model either routed or carries a stored length already, so \"never written\" cannot be asked");

        var run = new RouteRun(found, snapshot.Network.Version, TimeSpan.Zero)
        {
            Plan = BoxPlanner.Plan(results, snapshot.Boxes, project.BoxRadius),
        };

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "inspector", run, snapshot, project, catalogue);

        Expect.Same(found.Count, outcome.CircuitsWritten, "routes found, against circuits the apply reports telling their length");

        // Circuits that routed: every value the reader returns is the value standing on the element.
        foreach (var route in found)
        {
            var id = route.Circuit.Value;
            var element = document.GetElement(new ElementId(id));
            var read = CablingInspection.Read(document, id);
            var where = "circuit " + id.ToString(CultureInfo.InvariantCulture);

            Expect.That(read?.Kind == InspectedKind.Circuit, where + ": read as a circuit - read as " + (read?.Kind.ToString() ?? "nothing"));

            if (read is null)
                continue;

            SameLength(read.CableLength, element, CablingParameters.CableLength, where + ": the cable length");
            SameLength(read.LengthInTray, element, CablingParameters.LengthInTray, where + ": the length in trays");
            SameLength(read.LengthInConduit, element, CablingParameters.LengthInConduit, where + ": the length in conduits");
            SameLength(read.LengthFree, element, CablingParameters.LengthFree, where + ": the length in no carrier");
            SameLength(read.LengthOther, element, CablingParameters.LengthOther, where + ": the length in other carriers");
            SameLength(read.LengthSlack, element, CablingParameters.LengthSlack, where + ": the slack");

            Expect.That(
                read.CableLength.Written && Math.Abs(read.CableLength.Value - route.TotalLength) < 1e-9,
                where + ": the length the reader returns against the one the run computed - read "
                + read.CableLength.Value.ToString("F6", CultureInfo.InvariantCulture) + ", computed "
                + route.TotalLength.ToString("F6", CultureInfo.InvariantCulture) + " ft");

            SameText(read.RouteConnection, element, CablingParameters.RouteConnection, where + ": the connection it was routed with");
            SameText(read.RouteStamp, element, CablingParameters.RouteStamp, where + ": the carriers it was measured along");
        }

        // Circuits that did not route: bound, and never written - not zero, not empty text.
        foreach (var id in blank)
        {
            var read = CablingInspection.Read(document, id);
            var where = "circuit " + id.ToString(CultureInfo.InvariantCulture) + ", which did not route";

            Expect.That(read?.Kind == InspectedKind.Circuit, where + ": read as a circuit - read as " + (read?.Kind.ToString() ?? "nothing"));

            if (read is null)
                continue;

            var states = new[]
            {
                ("length", read.CableLength.State), ("in trays", read.LengthInTray.State), ("in conduits", read.LengthInConduit.State),
                ("in no carrier", read.LengthFree.State), ("in other carriers", read.LengthOther.State), ("slack", read.LengthSlack.State),
                ("connection routed with", read.RouteConnection.State), ("carriers", read.RouteStamp.State),
            };

            var wrong = states.Where(one => one.Item2 != StoredState.NeverWritten).Select(one => one.Item1 + " " + one.Item2).ToList();

            Expect.That(wrong.Count == 0, where + ": read as never written, every value of it - but " + string.Join(", ", wrong));
        }

        // Indicators the apply placed: what they recommend, how many cables enter, the circuits through them.
        var indicators = IndicatorsOf(document, symbol).Where(RecognisedAsOurs).ToList();

        Note(context, "inspector: indicators of ours after the apply", indicators.Count);

        foreach (var indicator in indicators)
        {
            var read = CablingInspection.Read(document, indicator.Id.Value);
            var where = "indicator " + indicator.Id.Value.ToString(CultureInfo.InvariantCulture);

            Expect.That(read?.Kind == InspectedKind.Indicator, where + ": read as an indicator of ours - read as " + (read?.Kind.ToString() ?? "nothing"));

            if (read is null)
                continue;

            SameText(read.Recommendation, indicator, CablingParameters.Recommendation, where + ": the recommendation");
            SameText(read.CircuitRefs, indicator, CablingParameters.CircuitRefs, where + ": the circuits through it");

            var entries = Value(indicator, CablingParameters.TapCount);

            Expect.That(
                read.TapCount.Written && read.TapCount.Value.ToString(CultureInfo.InvariantCulture) == entries,
                where + ": the cable entries read against the ones standing - read "
                + (read.TapCount.Written ? read.TapCount.Value.ToString(CultureInfo.InvariantCulture) : read.TapCount.State.ToString())
                + ", standing '" + entries + "'");
        }

        // A host carrier on a found route: the circuits through it.
        var carrier = found.SelectMany(one => one.Path).FirstOrDefault(one => !one.IsLinked);

        Note(context, "inspector: a host carrier on a found route", carrier.Value);

        if (carrier.Value > 0)
        {
            var element = document.GetElement(new ElementId(carrier.Value));
            var read = CablingInspection.Read(document, carrier.Value);
            var where = "carrier " + carrier.Value.ToString(CultureInfo.InvariantCulture);

            Expect.That(
                read?.Kind is InspectedKind.Carrier or InspectedKind.JunctionBox,
                where + ": read as a carrier or a junction box - read as " + (read?.Kind.ToString() ?? "nothing"));

            if (read is not null)
                SameText(read.CircuitRefs, element, CablingParameters.CircuitRefs, where + ": the circuits through it");
        }
    });

    private static void SameLength(Stored<double> read, Element? element, Guid parameter, string what)
    {
        var standing = element?.get_Parameter(parameter);
        var written = standing is { HasValue: true };

        Expect.That(
            read.Written == written && (!written || read.Value.Equals(standing!.AsDouble())),
            what + ": read " + (read.Written ? read.Value.ToString("R", CultureInfo.InvariantCulture) : read.State.ToString())
            + ", standing " + (written ? standing!.AsDouble().ToString("R", CultureInfo.InvariantCulture) : "nothing"));

        Expect.That(written, what + ": written by the apply");
    }

    private static void SameText(Stored<string> read, Element? element, Guid parameter, string what)
    {
        var standing = Value(element, parameter);

        Expect.That(
            read.Written && string.Equals(read.Value, standing, StringComparison.Ordinal),
            what + ": read '" + (read.Written ? read.Value : read.State.ToString()) + "', standing '" + standing + "'");
    }
}
