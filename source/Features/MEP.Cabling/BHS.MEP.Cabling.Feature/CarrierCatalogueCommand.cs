using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BHS.Logging;
using BHS.MEP.Cabling.Revit;
using BHS.MEP.Cabling.Ui;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// Says which of a project's elements carry cable, and writes the answer into the model.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because the catalogue lives in the model and nothing else can write there</b> - the
/// owner's answers of 2026-09-22 on where the rules are kept and on whether a person must be able to
/// change them. A model's settings are a flat store inside the document with no editor of any kind,
/// so without this command the shipped four categories would be the only ones any project could
/// ever have.
/// </para>
/// <para>
/// <b>It writes settings and nothing else.</b> No element is placed, moved or altered; the whole
/// transaction is the model's own settings layer. That is what makes one transaction the right
/// shape: a half-written catalogue - a category whose class arrived and whose filter did not - is a
/// rule that means something nobody chose.
/// </para>
/// <para>
/// <b>Everything it does is on the API thread, and nothing it does is long.</b> Counting a category
/// is one collector per document and a type lookup per element, with the answers cached by type; it
/// happens inside the modal dialog, where reads answer on all four releases - measured for the
/// routing window and relied on here.
/// </para>
/// </remarks>
public sealed class CarrierCatalogueCommand : IFeatureCommand
{
    public Result Execute(IUiFeatureServices services, ExternalCommandData data, ElementSet elements, ref string message)
    {
        var document = data?.Application?.ActiveUIDocument?.Document;

        if (document is null)
        {
            message = "Open a model: this command needs a document.";
            return Result.Failed;
        }

        // Refused rather than trusted to the greyed button, for the reason on CollectCablingCommand:
        // availability is advice, and whether Revit asks it for a command reached through the quick
        // access toolbar or a shortcut has not been measured.
        if (document.IsFamilyDocument)
        {
            message = "Open a project: this command does not work in the family editor.";
            return Result.Failed;
        }

        var log = services.Log;
        var settings = services.ModelSettings.For(document);
        var project = CablingProjectSettings.Read(settings);
        var catalogue = project.Carriers;

        if (catalogue.Unreadable.Length > 0)
            log.Warn("cabling: a carrier rule could not be read and is left out of the table - {0}", catalogue.Unreadable);

        var model = new CatalogueViewModel(
            Rules(document, catalogue),
            Offered(document),
            Shipped(document),
            rule => Count(document, project, rule),
            draft => Save(document, services, draft, log),
            catalogue.Methods.Parameter,
            Enumerable.Range(1, InstallationMethods.Slots).Select(slot => catalogue.Methods[slot]).ToList());

        var window = new CatalogueWindow(model);

        // Owned by Revit's main window, so it is modal to Revit rather than to nothing - a WPF
        // dialog with no owner is modal only to its own application, which inside Revit is nobody.
        window.OwnedBy(Process.GetCurrentProcess().MainWindowHandle);
        window.ShowDialog();

        return Result.Succeeded;
    }

    /// <summary>What the project counts today, one row per declared category.</summary>
    private static IReadOnlyList<CatalogueRule> Rules(Document document, CarrierCatalogue catalogue) =>
        catalogue.Categories
            .Select(category => new CatalogueRule(category.ToString(), Name(document, category))
            {
                Class = catalogue.ClassOf(category),
                Parameter = catalogue.FilterOf(category).Parameter,
                Value = catalogue.FilterOf(category).Value,
            })
            .OrderBy(one => one.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>The shipped set, for the button that puts it back.</summary>
    private static IReadOnlyList<CatalogueRule> Shipped(Document document) =>
        CarrierCatalogue.Defaults
            .Select(pair => new CatalogueRule(pair.Key.ToString(), Name(document, pair.Key)) { Class = pair.Value })
            .OrderBy(one => one.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>
    /// Every model category this Revit offers, by the name it gives them.
    /// </summary>
    /// <remarks>
    /// <b>All of them, not a shortlist of the plausible ones.</b> Which categories a project draws
    /// its cable structure in is exactly the question this screen exists to let somebody answer, and
    /// a list narrowed by our idea of what carries cable would be the hard-coded switch the catalogue
    /// replaced, moved into the picker. A category that holds nothing says so in its own row the
    /// moment it is added, which is cheaper than being right in advance.
    /// </remarks>
    private static IReadOnlyList<CatalogueCategory> Offered(Document document)
    {
        var offered = new List<CatalogueCategory>();

        foreach (Category category in document.Settings.Categories)
        {
            if (category.CategoryType != CategoryType.Model)
                continue;

            var id = (BuiltInCategory)category.Id.Value;

            // A category with no identifier of its own cannot be written into a setting, and there
            // is nothing useful to say about it here: it would be a row nobody could save.
            if (!Enum.IsDefined(typeof(BuiltInCategory), id))
                continue;

            offered.Add(new CatalogueCategory(id.ToString(), category.Name));
        }

        return offered.OrderBy(one => one.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>How many elements a rule as written sees, and how many it admits.</summary>
    private static (int Seen, int Counted) Count(Document document, CablingProjectSettings project, CatalogueRule rule)
    {
        if (!Enum.TryParse<BuiltInCategory>(rule.Category, out var category))
            return (0, 0);

        return CarrierCount.Of(document, project.Boxes, category, CarrierFilter.Of(rule.Parameter, rule.Value));
    }

    /// <summary>What this Revit calls the category, or its identifier when the model has no such.</summary>
    private static string Name(Document document, BuiltInCategory category) =>
        Category.GetCategory(document, category)?.Name ?? category.ToString();

    /// <summary>
    /// Writes the table into the model's own settings layer, in one transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Categories no longer in the table are cleared rather than left</b>, and cleared by name
    /// taken from the settings rather than remembered here: a row removed and a row never present
    /// have to end up the same, or the catalogue would only ever grow.
    /// </para>
    /// <para>
    /// <b>A key cleared in the model does not hide one stated in a file.</b> Clearing writes nothing
    /// to the model's layer, so a category a machine file declares shows through again - which is
    /// the layering working as designed and not a defect: the model stopped saying anything, and the
    /// machine still does. The table shows the effective answer, so it will say so on the next open.
    /// </para>
    /// </remarks>
    private static string Save(
        Document document,
        IUiFeatureServices services,
        CatalogueDraft draft,
        ILog log)
    {
        var rules = draft.Rules;

        foreach (var rule in rules)
        {
            if (rule.Class.Trim().Length == 0)
                return "Every category needs to say what its elements count as: " + rule.Name + " says nothing.";

            // Refused rather than ignored. A value with no parameter reads as a rule and is none,
            // and the run would count the whole category while the table showed a condition.
            if (rule.Parameter.Trim().Length == 0 && rule.Value.Trim().Length != 0)
                return "A value with no parameter is not a rule: " + rule.Name + " names a value and no parameter.";
        }

        // The same refusal the window shows, repeated here because this is what writes: a method in two
        // slots would split its length between two columns of every schedule.
        var twice = draft.Methods
            .Where(one => one.Length != 0)
            .GroupBy(one => one, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (twice is not null)
            return "A method is named in more than one slot: '" + twice.Key + "'. Name it once.";

        var kept = rules.Select(one => one.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = new List<string>();

        foreach (var key in services.ModelSettings.For(document).Section(CarrierCatalogue.CarriersKey).Keys)
        {
            var cut = key.IndexOf(':');
            var name = (cut < 0 ? key : key.Substring(0, cut)).Trim();

            if (name.Length != 0 && !present.Contains(name, StringComparer.OrdinalIgnoreCase))
                present.Add(name);
        }

        using var transaction = new Transaction(document, "BHS: carrier catalogue");

        if (transaction.Start() != TransactionStatus.Started)
            return "Revit would not start a transaction, so nothing was written.";

        foreach (var name in present.Where(one => !kept.Contains(one)))
        {
            foreach (var field in new[] { CarrierCatalogue.ClassField, CarrierCatalogue.ParameterField, CarrierCatalogue.ValueField })
                services.ModelSettings.Set(document, CarrierCatalogue.CarriersKey + ":" + name + ":" + field, null);
        }

        foreach (var rule in rules)
        {
            var at = CarrierCatalogue.CarriersKey + ":" + rule.Category + ":";

            services.ModelSettings.Set(document, at + CarrierCatalogue.ClassField, rule.Class.Trim());

            // Cleared rather than written empty, so that a rule somebody removed the parameter from
            // becomes a category counted whole rather than one filtered by nothing.
            services.ModelSettings.Set(document, at + CarrierCatalogue.ParameterField, Stated(rule.Parameter));
            services.ModelSettings.Set(document, at + CarrierCatalogue.ValueField, Stated(rule.Value));
        }

        // All six slots written, the empty ones as an empty string rather than cleared: once a project
        // has saved its slots they are its own, and a cleared key would let a machine file's default show
        // through - an office that later reorders its methods would move this project's columns.
        services.ModelSettings.Set(
            document, InstallationMethods.MethodsKey + ":" + InstallationMethods.ParameterField, draft.MethodParameter);

        for (var slot = 1; slot <= InstallationMethods.Slots; slot++)
        {
            var name = slot <= draft.Methods.Count ? draft.Methods[slot - 1] : string.Empty;

            services.ModelSettings.Set(
                document, InstallationMethods.MethodsKey + ":" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
        }

        var status = transaction.Commit();

        if (status != TransactionStatus.Committed)
        {
            log.Error("cabling: the carrier catalogue was not written - committing returned {0}", status);
            return "Revit did not keep the changes: committing them returned " + status + ", so nothing was written.";
        }

        log.Info("cabling: the carrier catalogue now names {0} category(ies)", rules.Count);
        return string.Empty;
    }

    /// <summary>A value that was typed, or null for one that was not - which clears the key.</summary>
    private static string? Stated(string value)
    {
        var said = (value ?? string.Empty).Trim();

        return said.Length == 0 ? null : said;
    }
}
