using System.Globalization;
using Autodesk.Revit.DB;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>What a project's own carrier catalogue has to be true of, whatever model it is.</summary>
public sealed partial class CablingReadingTests
{
    /// <summary>A value no model holds by accident, written on half the types this case marks.</summary>
    private const string TestMark = "BHS-TEST-CARRIER";

    /// <summary>
    /// A catalogue the project states replaces the shipped one, and its categories are the only ones read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's answer of 2026-09-22 was "replaces", and that is the half worth guarding.</b>
    /// Adding would have been safe and unshortenable; replacing lets a project stop reading a
    /// category, and the price is that a project which declares ducts and forgets trays gets no
    /// trays. An equality rather than a class check: counted against the same model read with the
    /// shipped catalogue, so a rule that quietly kept collecting the undeclared categories would
    /// show up as a larger number rather than as nothing at all.
    /// </para>
    /// <para>
    /// The control comes first: a project that stated nothing has to read exactly as it always did,
    /// with <see cref="CarrierCatalogue.Declared"/> false, or every model in existence changes on
    /// the day this ships.
    /// </para>
    /// </remarks>
    private static void ADeclaredCatalogueReplacesTheShippedOne(RevitTestContext context)
    {
        var shipped = CarrierCatalogue.Read(new FixedSettings());

        Expect.That(
            !shipped.Declared,
            "a project that states no catalogue reads as having declared one");

        Expect.Same(
            CarrierCatalogue.Defaults.Count,
            shipped.Categories.Count(),
            "categories a project that stated nothing collects");

        var settings = new FixedSettings();

        foreach (var category in new[] { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting })
            settings[CarrierCatalogue.CarriersKey + ":" + category + ":" + CarrierCatalogue.ClassField] = CarrierCatalogue.Tray;

        var declared = CarrierCatalogue.Read(settings);

        Expect.That(declared.Declared, "a project that stated two categories does not read as having declared any");
        Expect.Same(2, declared.Categories.Count(), "categories a project that stated two collects");

        var all = Read(context, shipped, boxes: null);
        var trays = all.Carriers.Count(one => string.Equals(one.Class, CarrierCatalogue.Tray, StringComparison.Ordinal));

        context.Note("catalogue: carriers with the shipped set", all.Carriers.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("catalogue: of them trays", trays.ToString(CultureInfo.InvariantCulture));

        Skip.When(
            trays == 0 || trays == all.Carriers.Count,
            "the model this sweep opened holds either no trays or nothing but trays, so a catalogue naming only trays cannot be told from the shipped one");

        var only = Read(context, declared, boxes: null);

        Expect.Same(
            trays,
            only.Carriers.Count,
            "carriers read by a catalogue naming only the two tray categories");

        Expect.That(
            only.Carriers.All(one => string.Equals(one.Class, CarrierCatalogue.Tray, StringComparison.Ordinal)),
            "a carrier of a class the project never declared was read all the same");
    }

    /// <summary>
    /// An element counts as a carrier only when its type says what the project asked it to say, and
    /// a category the project asked nothing of still counts whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The user's own parameter, found by name - the owner's answer of 2026-09-22.</b> So this
    /// case has to name one too, and it takes the name from Revit rather than writing it down: a
    /// literal "Type Comments" would assert on an English Revit and skip on a Russian one, which is
    /// the same defect the rule itself is built to avoid.
    /// </para>
    /// <para>
    /// <b>Half the types, never all of them.</b> Marking everything would make "and an unmarked type
    /// stops being a carrier" unaskable, and that half is what catches a filter that admits
    /// everything. The fitting category is left unfiltered in the same catalogue, which is the
    /// owner's answer that a filter applies only where the project asked for one.
    /// </para>
    /// <para>
    /// <b>And the window's promise is asserted here too:</b> the count it shows beside a rule is
    /// produced by a walk of its own, so "8 of 412" predicting the run is a claim about two separate
    /// passes agreeing - which is exactly the kind of thing that stops being true quietly.
    /// </para>
    /// </remarks>
    private static void OnlyTypesThatSaySoCountAsCarriers(RevitTestContext context)
    {
        var document = context.Document!;
        var shipped = CarrierCatalogue.Read(new FixedSettings());
        var before = Read(context, shipped, boxes: null);

        var runs = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_CableTray)
            .WhereElementIsNotElementType()
            .ToList();

        var types = runs
            .Select(one => one.GetTypeId())
            .Where(one => one != ElementId.InvalidElementId)
            .Select(one => one.Value)
            .Distinct()
            .OrderBy(one => one)
            .ToList();

        context.Note("catalogue: cable trays in the host", runs.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("catalogue: their types", types.Count.ToString(CultureInfo.InvariantCulture));

        Skip.When(
            types.Count < 2,
            "the host of the model this sweep opened has fewer than two cable tray types, so a marked type cannot be told from an unmarked one");

        // The name this Revit gives the parameter, asked of the model rather than written down.
        var sample = document.GetElement(new ElementId(types[0]));
        var named = sample?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);

        Skip.When(
            named is null || named.IsReadOnly || named.StorageType != StorageType.String,
            "the cable tray types of this model have no writable text parameter to mark them with");

        var parameter = named!.Definition.Name;

        context.Note("catalogue: the parameter this case marks", parameter);

        var marked = types.Take(types.Count / 2).ToHashSet();

        using (var transaction = new Transaction(document, "BHS test: some tray types say they carry cable"))
        {
            transaction.Start();

            foreach (var type in marked)
                document.GetElement(new ElementId(type))?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set(TestMark);

            Expect.That(
                transaction.Commit() == TransactionStatus.Committed,
                "Revit did not keep the marks this case writes on the tray types");
        }

        var expected = runs.Count(one => marked.Contains(one.GetTypeId().Value));

        context.Note("catalogue: tray types marked", marked.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("catalogue: trays their marks admit", expected.ToString(CultureInfo.InvariantCulture));

        Skip.When(
            expected == 0 || expected == runs.Count,
            "marking half the tray types of this model admits either none of its trays or all of them, so one half of the question cannot be asked");

        var settings = new FixedSettings();
        var at = CarrierCatalogue.CarriersKey + ":" + BuiltInCategory.OST_CableTray + ":";

        settings[at + CarrierCatalogue.ClassField] = CarrierCatalogue.Tray;
        settings[at + CarrierCatalogue.ParameterField] = parameter;
        settings[at + CarrierCatalogue.ValueField] = TestMark;

        // The fittings are declared with nothing asked of them, in the same catalogue: a filter is
        // the category's, not the catalogue's.
        settings[CarrierCatalogue.CarriersKey + ":" + BuiltInCategory.OST_CableTrayFitting + ":" + CarrierCatalogue.ClassField] =
            CarrierCatalogue.Tray;

        var catalogue = CarrierCatalogue.Read(settings);
        var after = Read(context, catalogue, boxes: null);

        var admitted = after.Tallies.Single(one => one.Category == BuiltInCategory.OST_CableTray);
        var whole = after.Tallies.Single(one => one.Category == BuiltInCategory.OST_CableTrayFitting);
        var fittingsBefore = before.Tallies.Single(one => one.Category == BuiltInCategory.OST_CableTrayFitting);

        Expect.Same(
            expected,
            admitted.Counted,
            "cable trays admitted by a rule that only marked types satisfy");

        Expect.That(
            admitted.Counted < admitted.Seen,
            "a rule half the types fail admitted every element the category holds");

        Expect.Same(
            fittingsBefore.Counted,
            whole.Counted,
            "cable tray fittings counted by a catalogue that asked nothing of them");

        // The screen's promise: the number shown beside a rule while it is written is produced by a
        // walk of its own, and it has to predict the read.
        var preview = CarrierCount.Of(
            document,
            CablingProjectSettings.Read(new FixedSettings()).Boxes,
            BuiltInCategory.OST_CableTray,
            CarrierFilter.Of(parameter, TestMark));

        Expect.Same(admitted.Counted, preview.Counted, "cable trays the catalogue window would say the rule admits");
        Expect.Same(admitted.Seen, preview.Seen, "cable trays the catalogue window would say the rule sees");
    }

    /// <summary>
    /// A carrier rule that admits nothing is named on the screen, and a shipped catalogue says nothing.
    /// </summary>
    /// <remarks>
    /// <b>The owner's answer of 2026-09-22 to what happens when a filter matches nothing.</b> Revit's
    /// own warning cannot carry it - the registered text is all Revit shows, and there is no element
    /// to point it at - so the report is the whole of it, and a report that stayed silent would leave
    /// a mistyped rule looking exactly like a model with no structure in it. The silence on a shipped
    /// catalogue is the other half: a line printed after every run is one nobody reads.
    /// </remarks>
    private static void ARuleThatAdmitsNothingIsNamed(RevitTestContext context)
    {
        var document = context.Document!;
        var shipped = CarrierCatalogue.Read(new FixedSettings());
        var plain = Read(context, shipped, boxes: null);

        Expect.Same(
            0,
            CarrierReport.Describe(document, plain.Tallies, shipped.Declared).Length,
            "characters the screen says about a catalogue nobody stated and every category of which counted");

        var settings = new FixedSettings();
        var at = CarrierCatalogue.CarriersKey + ":" + BuiltInCategory.OST_CableTray + ":";

        settings[at + CarrierCatalogue.ClassField] = CarrierCatalogue.Tray;
        settings[at + CarrierCatalogue.ParameterField] = "BHS-TEST-NO-SUCH-PARAMETER";
        settings[at + CarrierCatalogue.ValueField] = TestMark;

        var catalogue = CarrierCatalogue.Read(settings);
        var read = Read(context, catalogue, boxes: null);
        var tally = read.Tallies.Single(one => one.Category == BuiltInCategory.OST_CableTray);

        Skip.When(
            tally.Seen == 0,
            "the model this sweep opened holds no cable trays, so a rule that admits none of them cannot be told from a category that is empty");

        Expect.Same(0, tally.Counted, "cable trays admitted by a rule naming a parameter no type has");

        var said = CarrierReport.Describe(document, read.Tallies, catalogue.Declared);

        context.Note("catalogue: what the screen says", said);

        // Asked again as though the project had stated nothing, and that is the whole force of this
        // case. A stated catalogue is listed whatever its rules admit, and that listing names every
        // category and every count - so asserting against it would pass with the rule about empty
        // rules deleted. With the listing suppressed, the only reason left to speak is the finding.
        var finding = CarrierReport.Describe(document, read.Tallies, declared: false);

        Expect.That(
            finding.Length != 0,
            "the screen says nothing about a rule that turned away every element of its category");

        Expect.That(
            finding.IndexOf(Category.GetCategory(document, BuiltInCategory.OST_CableTray)?.Name ?? "OST_CableTray", StringComparison.Ordinal) >= 0,
            "the screen does not name the category whose rule admitted nothing");

        Expect.That(
            finding.IndexOf(tally.Seen.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0,
            "the screen does not say how many elements the rule turned away, which is what tells a bad rule from an empty category");

        Expect.That(
            said.Length != 0,
            "the screen says nothing at all about a catalogue this project stated itself");
    }
}
