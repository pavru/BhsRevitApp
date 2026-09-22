using System.Globalization;
using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>The length laid by the project's installation methods, into six fixed slots.</summary>
public sealed partial class CablingApplyTests
{
    /// <summary>The method this case gives slot 1 and writes on half the tray types.</summary>
    private const string SlottedTray = "BHS-TEST-METHOD-TRAY";

    /// <summary>The method this case gives slot 2 and writes on every conduit type.</summary>
    private const string SlottedConduit = "BHS-TEST-METHOD-CONDUIT";

    /// <summary>A method written on the other half of the tray types and given no slot.</summary>
    private const string Unslotted = "BHS-TEST-METHOD-NO-SLOT";

    /// <summary>A method slot 6 names and no carrier is laid by; the slot is filled beforehand to be emptied.</summary>
    private const string Unwalked = "BHS-TEST-METHOD-UNWALKED";

    /// <summary>
    /// The case for the length laid by method: every found route's six slots, the other length, and
    /// the sum, read back out of the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's nine answers of 2026-09-22, each where it can turn red.</b> A slot holds its
    /// method beside a length above zero, or nothing at all - never a label over an empty length and
    /// never a stale length: slot 6 is filled on every circuit before the apply, names a method no
    /// carrier is laid by, and has to be empty afterwards. A method with no slot is laid into the
    /// other length. The slots, the free length and the slack add up to the total, and the class view
    /// - trays and conduits - is written exactly as it was.
    /// </para>
    /// <para>
    /// <b>The methods are read from the type, by a parameter this case names the way the project
    /// would</b> - asked of Revit rather than written down, as the catalogue case does, so an English
    /// and a Russian Revit ask the same question. Half the tray types get the slotted method and half
    /// the unslotted one, because a case that gave every tray one method could not tell a method view
    /// from the class view it sits beside.
    /// </para>
    /// <para>
    /// <b>The other length is computed here by subtraction, and the apply sums it.</b> Two ways of
    /// getting one number, so that a method dropped from the sum or counted twice shows.
    /// </para>
    /// </remarks>
    private static void CircuitsAreToldHowTheirLengthIsLaid(RevitTestContext context) => Watched(context, watch =>
    {
        var document = context.Document!;
        var application = context.Application.Application;

        var trayTypes = TypesOf(document, BuiltInCategory.OST_CableTray);
        var conduitTypes = TypesOf(document, BuiltInCategory.OST_Conduit);

        Note(context, "methods: cable tray types in the host", trayTypes.Count);
        Note(context, "methods: conduit types in the host", conduitTypes.Count);

        Skip.When(
            trayTypes.Count < 2,
            "the host of the model this sweep opened has fewer than two cable tray types, so a slotted method cannot be told from an unslotted one");

        var sample = document.GetElement(new ElementId(trayTypes[0]))?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);

        Skip.When(
            sample is null || sample.IsReadOnly || sample.StorageType != StorageType.String,
            "the cable tray types of this model have no writable text parameter to state a method in");

        var parameter = sample!.Definition.Name;

        context.Note("methods: the type parameter this case states methods in", parameter);

        var slotted = trayTypes.Take(trayTypes.Count / 2).ToHashSet();
        var said = new Dictionary<long, string>();

        foreach (var type in trayTypes)
            said[type] = slotted.Contains(type) ? SlottedTray : Unslotted;

        foreach (var type in conduitTypes)
            said[type] = SlottedConduit;

        var mark = watch.Mark;
        TransactionStatus status;

        using (var transaction = new Transaction(document, "BHS test: carrier types state how they are laid"))
        {
            transaction.Start();

            foreach (var pair in said)
                document.GetElement(new ElementId(pair.Key))?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set(pair.Value);

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "stating a method on the carrier types");

        var settings = new FixedSettings
        {
            [InstallationMethods.MethodsKey + ":" + InstallationMethods.ParameterField] = parameter,
            [InstallationMethods.MethodsKey + ":1"] = SlottedTray,
            [InstallationMethods.MethodsKey + ":2"] = SlottedConduit,
            [InstallationMethods.MethodsKey + ":6"] = Unwalked,
        };

        var project = CablingProjectSettings.Read(settings);
        var catalogue = project.Carriers;

        Expect.That(catalogue.Methods.IsOn, "a project naming the method parameter reads with methods off");
        Expect.Same(1, catalogue.Methods.SlotOf(SlottedTray.ToLowerInvariant() + " "), "the slot of the tray method, typed in another case with a space");
        Expect.Same(0, catalogue.Methods.SlotOf(Unslotted), "the slot of a method the project named in none");

        var symbol = NeedsIndicatorFamily(document, project);

        NeedsNoIndicatorsOfOurs(context, document, symbol, "methods");

        var cut = CutEveryCircuitInBoxes(watch, document, application);
        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        // The reader first, and against what this case wrote rather than what the route reports: a
        // reader that read the class, or nothing, would otherwise agree with the apply through the
        // one value both of them take from it.
        var readBack = 0;

        foreach (var node in snapshot.Carriers.Where(one => !one.Id.IsLinked))
        {
            var element = document.GetElement(new ElementId(node.Id.Value));

            if (element is null || !said.TryGetValue(element.GetTypeId().Value, out var expected))
                continue;

            readBack++;

            Expect.That(
                string.Equals(node.Method, expected, StringComparison.Ordinal),
                "carrier " + node.Id.Value + ", whose type says '" + expected + "', read back laid by '" + node.Method + "'");
        }

        Note(context, "methods: host carriers read back with the method their type states", readBack);

        var results = snapshot.Circuits.Described.Select(circuit => Router.Route(snapshot.Network, circuit, Options)).ToList();
        var found = results.Where(one => one.Status == RouteStatus.Found).ToList();

        Note(context, "methods: circuits cut in boxes", cut);
        Note(context, "methods: routes found", found.Count);

        Skip.When(found.Count == 0, "no circuit of this model routed, so there is no length to divide");

        // Slot 6 filled on every circuit that will be written, so that "empty afterwards" is a claim
        // about clearing rather than about a parameter nobody ever set.
        mark = watch.Mark;

        using (var transaction = new Transaction(document, "BHS test: a stale method slot on every circuit"))
        {
            transaction.Start();

            foreach (var route in found)
            {
                var circuit = document.GetElement(new ElementId(route.Circuit.Value));

                SetText(circuit, CablingParameters.MethodLabel[5], Unwalked, "method slot 6");

                var length = circuit?.get_Parameter(CablingParameters.MethodLength[5]);

                Expect.That(
                    length is { IsReadOnly: false } && length.Set(1.0),
                    "circuit " + route.Circuit.Value + " refused a length in method slot 6");
            }

            status = transaction.Commit();
        }

        Committed(watch, mark, status, "filling method slot 6 on every circuit");

        var plan = BoxPlanner.Plan(results, snapshot.Boxes, project.BoxRadius);
        var run = new RouteRun(found, snapshot.Network.Version, TimeSpan.Zero, plan, project.Slack);

        NeedsDefinitionsFor(document, symbol, run, snapshot);

        var outcome = ApplyWatched(context, watch, "methods", run, snapshot, project, catalogue);

        Expect.Same(found.Count, outcome.CircuitsWritten, "routes found, against circuits the apply reports telling their length");

        Note(context, "methods: routes with a length in the slotted tray method", found.Count(one => one.AlongMethod(SlottedTray) > 0));
        Note(context, "methods: routes with a length in the slotted conduit method", found.Count(one => one.AlongMethod(SlottedConduit) > 0));
        Note(context, "methods: routes with a length in the unslotted method", found.Count(one => one.AlongMethod(Unslotted) > 0));
        Note(context, "methods: routes with a length in carriers stating no method", found.Count(one => one.AlongMethod(string.Empty) > 0));

        var slots = new[] { SlottedTray, SlottedConduit, string.Empty, string.Empty, string.Empty, Unwalked };

        foreach (var route in found)
        {
            var circuit = document.GetElement(new ElementId(route.Circuit.Value));
            var where = "circuit " + route.Circuit.Value.ToString(CultureInfo.InvariantCulture);
            var inSlots = 0.0;

            for (var i = 0; i < slots.Length; i++)
            {
                var laid = slots[i].Length == 0 ? 0 : route.AlongMethod(slots[i]);
                var label = circuit?.get_Parameter(CablingParameters.MethodLabel[i]);
                var length = circuit?.get_Parameter(CablingParameters.MethodLength[i]);
                var slot = where + ", method slot " + (i + 1);

                if (laid > 0)
                {
                    inSlots += laid;

                    Expect.That(
                        label is { HasValue: true } && string.Equals(label.AsString(), slots[i], StringComparison.Ordinal),
                        slot + ": the label stored is '" + label?.AsString() + "', the method laid is '" + slots[i] + "'");

                    StoredLength(circuit, CablingParameters.MethodLength[i], laid, slot + ": the length");
                }
                else
                {
                    Expect.That(
                        label is not { HasValue: true } || string.IsNullOrEmpty(label.AsString()),
                        slot + ": nothing was laid by '" + slots[i] + "', and the label reads '" + label?.AsString() + "'");

                    Expect.That(
                        length is not { HasValue: true },
                        slot + ": nothing was laid by '" + slots[i] + "', and the length holds "
                        + (length is { HasValue: true } ? length.AsDouble().ToString("F6", CultureInfo.InvariantCulture) : "nothing") + " ft");
                }
            }

            StoredLength(circuit, CablingParameters.LengthOther, route.AlongCarriers - inSlots, where + ": the length laid by no slotted method");
            StoredLength(circuit, CablingParameters.LengthInTray, route.AlongClass("tray"), where + ": the length in trays, beside the methods");
            StoredLength(circuit, CablingParameters.LengthInConduit, route.AlongClass("conduit"), where + ": the length in conduits, beside the methods");

            var parts = CablingParameters.MethodLength
                .Concat(new[] { CablingParameters.LengthOther, CablingParameters.LengthFree, CablingParameters.LengthSlack })
                .Sum(one => circuit?.get_Parameter(one) is { HasValue: true } stored ? stored.AsDouble() : 0);

            var total = circuit?.get_Parameter(CablingParameters.CableLength) is { HasValue: true } stamped ? stamped.AsDouble() : double.NaN;

            Expect.That(
                Math.Abs(parts - total) < 1e-9,
                where + ": the slots, the other length, the free length and the slack add up to the length stored - parts "
                + parts.ToString("F6", CultureInfo.InvariantCulture) + ", length " + total.ToString("F6", CultureInfo.InvariantCulture) + " ft");
        }

        // The screen's half of the fourth answer: a method with no slot is named, and a slotted one never is.
        var line = MethodReport.Describe(run, catalogue.Methods, one => one.ToString("F3", CultureInfo.InvariantCulture));
        var unslottedLaid = found.Any(one => one.AlongMethod(Unslotted) > 0);

        context.Note("methods: what the run screen says", line.Length == 0 ? "(nothing)" : "names " + (unslottedLaid ? "the unslotted method" : "no test method"));

        Expect.That(
            line.Contains(Unslotted) == unslottedLaid,
            "the run screen " + (unslottedLaid ? "does not name" : "names") + " the method given no slot, which "
            + (unslottedLaid ? "was" : "was not") + " laid");

        Expect.That(
            !line.Contains(SlottedTray) && !line.Contains(SlottedConduit),
            "the run screen names a method that has a slot");
    });

    /// <summary>The types of a category's instances in the host, in ascending id order.</summary>
    private static List<long> TypesOf(Document document, BuiltInCategory category) =>
        new FilteredElementCollector(document)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .Select(one => one.GetTypeId())
            .Where(one => one != ElementId.InvalidElementId)
            .Select(one => one.Value)
            .Distinct()
            .OrderBy(one => one)
            .ToList();
}
