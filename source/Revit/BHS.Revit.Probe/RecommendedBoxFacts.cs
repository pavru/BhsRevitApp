using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BHS.Revit.Abstractions;
using BHS.Revit.Common.Parameters;

namespace BHS.Revit.Probe;

/// <summary>
/// A throwaway text parameter, for measuring how much text a shared parameter will hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own scheme rather than a third parameter on <see cref="ProbeParameters"/>.</b> That one is
/// bound to generic models, and a model without a single generic model instance would leave nothing
/// to write on.
/// </para>
/// <para>
/// <b>And a shared parameter rather than a built-in one</b>, because a shared parameter is what the
/// calculation will actually write the circuit list into. A limit measured on
/// <c>PROJECT_STATUS</c> would be a fact about a different parameter, offered as if it were about
/// ours.
/// </para>
/// <para>
/// <b>Two categories, because the first attempt bound neither and could not say why.</b> Project
/// information looked like the obvious target - every document has exactly one - and the run
/// reported <c>bound: 0</c> with the parameter nowhere to be found. Levels are the second target for
/// the property that matters here and nothing else: a project document always has at least one.
/// </para>
/// </remarks>
internal sealed class ProbeTextParameters : SharedParameterScheme
{
    internal static readonly Guid Text = new("496dc059-6259-4dc6-80cd-bcde14d34fe1");

    internal static readonly BuiltInCategory[] Targets =
    {
        BuiltInCategory.OST_ProjectInformation,
        BuiltInCategory.OST_Levels,
    };

    protected override string FileBaseName => "BHS.ProbeTextLimit";

    protected override string GroupName(ParameterLanguage language) => language switch
    {
        ParameterLanguage.Russian => "BHS Проба",
        _ => "BHS Probe",
    };

    protected override IReadOnlyList<SharedParameter> Parameters { get; } = new[]
    {
        new SharedParameter(
            Text,
            SpecTypeId.String.Text,
            GroupTypeId.Data,
            instance: true,
            Targets,
            english: new ParameterText("BHS_Prb_TextLimit", "A throwaway, written by the sweep."),
            russian: new ParameterText("BHS_Prb_ПределТекста", "Черновой, пишется прогоном.")),
    };
}

/// <summary>
/// What a real model says about the family chosen as a recommended-box indicator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported as measurements, not as checks.</b> Nobody here knows the answers yet, and a check
/// written before its answer is a check that agrees with whoever wrote it. They become assertions in
/// the commit that reads them - the same order that turned <c>IsSessionReady</c> into
/// <c>IsInitialized</c>.
/// </para>
/// <para>
/// <b>Only families whose name starts with <c>BHS_</c> are examined, and that is not a filter but a
/// boundary.</b> These are somebody's real project files and this repository is public: family and
/// type names would identify the building. Names beginning with our own prefix are ours by
/// construction, so the survey refuses everything else at the source rather than trimming the
/// answer at the end.
/// </para>
/// </remarks>
internal static class RecommendedBoxFacts
{
    /// <summary>The categories <c>CarrierCatalogue</c> collects by default.</summary>
    /// <remarks>
    /// Repeated here rather than referenced. The probe does not depend on the cabling feature, and
    /// making it do so would put the feature's assemblies into every sweep - the one thing the
    /// separate feature assembly exists to avoid.
    /// </remarks>
    private static readonly BuiltInCategory[] Carriers =
    {
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting,
    };

    /// <summary>Lengths to try, each a long step past the last, stopping at the first refusal.</summary>
    /// <remarks>
    /// A circuit list on a trunk crossed by thirty circuits is three hundred-odd characters, so the
    /// interesting region starts an order of magnitude above that and the steps are coarse. Finding
    /// the exact boundary would cost dozens of transactions to answer a question nobody asked: what
    /// matters is whether the limit is near what we will write or nowhere near it.
    /// </remarks>
    private static readonly int[] Lengths = { 256, 1024, 4096, 16384, 65536, 262144 };

    public static IReadOnlyDictionary<string, string> Measure(IRevitSession session)
    {
        var answer = new Dictionary<string, string>(StringComparer.Ordinal);
        var document = session.Application.ActiveUIDocument?.Document;

        if (document is null)
        {
            // Said out loud. A survey that quietly does nothing on a run without a model reads as a
            // survey that found nothing.
            answer["box:documentSkipped"] = "True";
            return answer;
        }

        answer["box:documentSkipped"] = "False";

        var symbols = Symbols(document, answer);

        Placing(document, symbols, answer);
        TextLimit(session, document, answer);

        return answer;
    }

    private static IReadOnlyList<FamilySymbol> Symbols(Document document, IDictionary<string, string> answer)
    {
        var symbols = new FilteredElementCollector(document)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(one => one.Family is { } family
                && family.Name.StartsWith("BHS_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(one => one.Family.Name, StringComparer.Ordinal)
            .ThenBy(one => one.Name, StringComparer.Ordinal)
            .ToList();

        answer["box:ourSymbols"] = symbols.Count.ToString(CultureInfo.InvariantCulture);

        var index = 0;

        foreach (var symbol in symbols)
        {
            index++;
            var prefix = "box" + index.ToString(CultureInfo.InvariantCulture) + ":";

            answer[prefix + "family"] = symbol.Family.Name;
            answer[prefix + "type"] = symbol.Name;

            // The category by name and by identity, not by number. ElementId's numeric member
            // changed shape at 2025, and the question here is "is this one of the four" - which
            // comparing the resolved categories answers without touching either form.
            var category = symbol.Category;
            answer[prefix + "category"] = category?.Name ?? "(none)";
            answer[prefix + "isCarrierCategory"] = IsCarrier(document, category) ? "True" : "False";

            // Whether it is placed with a point or wants a host. The indicator goes at a computed
            // coordinate, so anything host-based is unusable however good it looks.
            answer[prefix + "placement"] = symbol.Family.FamilyPlacementType.ToString();

            var count = new FilteredElementCollector(document)
                .WherePasses(new FamilyInstanceFilter(document, symbol.Id))
                .WhereElementIsNotElementType()
                .GetElementCount();

            answer[prefix + "instances"] = count.ToString(CultureInfo.InvariantCulture);

            if (count == 0)
                continue;

            // A fresh collector: one that has been counted cannot be enumerated again.
            var instance = new FilteredElementCollector(document)
                .WherePasses(new FamilyInstanceFilter(document, symbol.Id))
                .WhereElementIsNotElementType()
                .FirstElement() as FamilyInstance;

            Connectors(instance, prefix, answer);
        }

        return symbols;
    }

    /// <summary>
    /// Places one indicator the way the apply phase would, and asks what it did to the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placing it ourselves rather than waiting for one to be placed.</b> The first run found the
    /// family loaded and zero instances, so every question that needs an instance - has it
    /// connectors, is it connected, does a carrier collector pick it up - went unanswered. An
    /// instance we place is also the better measurement: it exercises
    /// <c>Activate</c> plus <c>NewFamilyInstance</c>, which is the production path the apply phase
    /// will take, rather than inspecting something a person placed by hand.
    /// </para>
    /// <para>
    /// <b>The carrier count before and against after is the hazard measured rather than argued.</b>
    /// <c>CarrierCatalogue</c> collects by category alone, and the owner's family is a cable tray
    /// fitting; whether a free-standing instance of it joins the collection is the whole question,
    /// and counting answers it in one line.
    /// </para>
    /// <para>
    /// Rolled back, like every other write here: an instance left behind makes the document modified
    /// and Revit asks about saving on the way out - a modal dialog in a sweep nobody is watching.
    /// </para>
    /// </remarks>
    private static void Placing(
        Document document,
        IReadOnlyList<FamilySymbol> symbols,
        IDictionary<string, string> answer)
    {
        var symbol = symbols.FirstOrDefault(one => IsCarrier(document, one.Category)) ?? symbols.FirstOrDefault();

        if (symbol is null)
        {
            answer["place:symbol"] = "(no BHS_ family in this model)";
            return;
        }

        var level = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(one => one.Elevation)
            .FirstOrDefault();

        if (level is null)
        {
            answer["place:level"] = "(none)";
            return;
        }

        answer["place:type"] = symbol.Name;
        answer["place:before"] = CarrierCount(document).ToString(CultureInfo.InvariantCulture);

        using var group = new TransactionGroup(document, "BHS probe: place an indicator");
        group.Start();

        try
        {
            using (var transaction = new Transaction(document, "BHS probe: place"))
            {
                transaction.Start();

                try
                {
                    // A symbol that is not active cannot be placed, and the failure says nothing
                    // about why. Autodesk's own samples do exactly this before every placement.
                    if (!symbol.IsActive)
                        symbol.Activate();

                    var placed = document.Create.NewFamilyInstance(
                        XYZ.Zero, symbol, level, StructuralType.NonStructural);

                    transaction.Commit();

                    answer["place:placed"] = "True";
                    answer["place:after"] = CarrierCount(document).ToString(CultureInfo.InvariantCulture);

                    Connectors(placed, "place:", answer);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException error)
                {
                    // Narrowly: everything Revit throws descends from this, and anything that does
                    // not is a defect of ours rather than an answer about placement.
                    answer["place:placed"] = "refused: " + error.GetType().Name;
                    transaction.RollBack();
                }
            }
        }
        finally
        {
            group.RollBack();
        }

        answer["place:afterRollback"] = CarrierCount(document).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>How many elements a carrier collector would take, counted the way it counts.</summary>
    private static int CarrierCount(Document document)
    {
        var total = 0;

        foreach (var category in Carriers)
        {
            total += new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .GetElementCount();
        }

        return total;
    }

    private static void Connectors(FamilyInstance? instance, string prefix, IDictionary<string, string> answer)
    {
        var manager = instance?.MEPModel?.ConnectorManager;

        if (manager is null)
        {
            // Not a defect and worth saying: a family with no MEP model has no connectors at all,
            // which is the strongest possible answer to "could this join the carrier network".
            answer[prefix + "connectors"] = "(no MEP model)";
            return;
        }

        var total = 0;
        var connected = 0;

        foreach (Connector connector in manager.Connectors)
        {
            total++;

            if (connector.IsConnected)
                connected++;
        }

        answer[prefix + "connectors"] = total.ToString(CultureInfo.InvariantCulture);
        answer[prefix + "connected"] = connected.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsCarrier(Document document, Category? category)
    {
        if (category is null)
            return false;

        foreach (var carrier in Carriers)
        {
            if (Category.GetCategory(document, carrier) is { } found && found.Id == category.Id)
                return true;
        }

        return false;
    }

    /// <summary>How much text a shared text parameter holds before it refuses or truncates.</summary>
    /// <remarks>
    /// <b>Everything written is rolled back</b>, the same discipline as the schema and parameter
    /// checks: a binding and a value both make the document modified, and a modified document makes
    /// Revit ask about saving on the way out.
    /// </remarks>
    private static void TextLimit(IRevitSession session, Document document, IDictionary<string, string> answer)
    {
        var scheme = new ProbeTextParameters();

        // Reported rather than inferred, and this is the lesson of the first run applied a second
        // time: "nothing was bound" has more than one cause, and an answer that cannot tell them
        // apart costs a whole Revit to ask again.
        foreach (var category in ProbeTextParameters.Targets)
        {
            answer["text:resolved:" + category] =
                Category.GetCategory(document, category) is not null ? "True" : "False";
        }

        // Written before it is bound, and this is the whole of the second run's failure. Install
        // swaps SharedParametersFilename to our path and calls OpenSharedParameterFile, which
        // returns null for a file that is not there - so nothing is found, nothing is bound, and
        // the report says "bound to nothing this document has" while both categories resolved.
        // Export is what creates the file; the scheme next door calls it first and this did not.
        answer["text:filesWritten"] = scheme.Export(session.Application.Application).Count
            .ToString(CultureInfo.InvariantCulture);

        using var group = new TransactionGroup(document, "BHS probe: text limit");
        group.Start();

        try
        {
            var bound = scheme.Install(document, session.Application.Application);
            answer["text:bound"] = bound.Count.ToString(CultureInfo.InvariantCulture);

            if (Target(document) is not { } parameter)
            {
                // The one outcome that leaves the question open, so it says so rather than
                // reporting nothing and reading as "no limit found".
                answer["text:parameter"] = "(bound to nothing this document has)";
                return;
            }

            answer["text:parameter"] = "found";

            foreach (var length in Lengths)
            {
                var outcome = Write(document, parameter, length);
                answer["text:" + length.ToString(CultureInfo.InvariantCulture)] = outcome;

                if (outcome != "kept")
                    break;
            }
        }
        finally
        {
            group.RollBack();
        }
    }

    /// <summary>The first element this document has that carries the throwaway parameter.</summary>
    private static Parameter? Target(Document document)
    {
        if (document.ProjectInformation?.get_Parameter(ProbeTextParameters.Text) is { } information)
            return information;

        var level = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(one => one.Elevation)
            .FirstOrDefault();

        return level?.get_Parameter(ProbeTextParameters.Text);
    }

    private static string Write(Document document, Parameter parameter, int length)
    {
        var value = new string('x', length);
        var set = false;
        var failure = string.Empty;

        using (var transaction = new Transaction(document, "BHS probe: text " + length.ToString(CultureInfo.InvariantCulture)))
        {
            transaction.Start();

            try
            {
                set = parameter.Set(value);
                transaction.Commit();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException error)
            {
                failure = error.GetType().Name;
                transaction.RollBack();
            }
        }

        if (!set)
            return failure.Length > 0 ? "refused: " + failure : "refused";

        var back = parameter.AsString() ?? string.Empty;

        return back.Length == length
            ? "kept"
            : "truncated to " + back.Length.ToString(CultureInfo.InvariantCulture);
    }
}
