using System.Globalization;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Common.Parameters;
using BHS.Revit.Testing;
using BHS.Settings;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// That writing a run into the model binds what it is going to write to, and refuses out loud when
/// it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>These two guard the failures that are silent by construction, not the ones that are loud.</b>
/// Two of the three parameters the apply phase writes declare no category at compile time - the
/// indicator's family is the project's to choose - so a caller who forgot the runtime category would
/// bind none of them and nothing would say so. And a project whose indicator family has been renamed
/// would place nothing, which from outside is indistinguishable from a run with nothing to place.
/// </para>
/// <para>
/// <b>Neither needs a routed circuit, and that is deliberate.</b> Both conditions are about the
/// model and the project rather than about a route, so an empty run exercises them exactly. A case
/// that first had to produce boxes would be testing the router in order to reach the binding.
/// </para>
/// </remarks>
public sealed class CablingApplyTests : IRevitTestSuite
{
    public string Name => "Cabling apply";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "applying binds every parameter it is about to write, including the runtime categories",
            BindsWhatItWrites,
            needsDocument: true,
            writes: true),

        new RevitTestCase(
            "an indicator family this model does not have is refused by name, not passed over",
            RefusesAMissingFamily,
            needsDocument: true),
    };

    private static void BindsWhatItWrites(RevitTestContext context)
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();
        var project = CablingProjectSettings.Read(new Fixed());

        var symbol = project.Boxes.In(document);

        Skip.When(
            symbol is null,
            "the model this sweep opened does not hold the indicator family, which the apply phase places");

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        // An empty run: nothing to place, so what is left is exactly the part under test - the
        // parameters being bound before anything is written to them.
        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);
        var outcome = CablingApply.Apply(document, application, run, snapshot, project, catalogue);

        Expect.That(
            !outcome.Refused,
            "applying refused on a model that has the indicator family: " + string.Join(" ", outcome.Refusals));

        // Asked of the document afterwards rather than taken from what Install reported, because the
        // question is what is bound now. The same runtime set the apply path composes: the
        // indicator's own category for all three, and every carrier category for the references.
        var runtime = new RuntimeCategories()
            .Add(CablingParameters.Recommendation, Category(symbol!))
            .Add(CablingParameters.TapCount, Category(symbol!))
            .Add(CablingParameters.CircuitRefs, Category(symbol!));

        foreach (var category in catalogue.Categories)
            runtime.Add(CablingParameters.CircuitRefs, category);

        var missing = new CablingParameters()
            .Missing(document, runtime)
            .Where(one => one.Id == CablingParameters.Recommendation
                          || one.Id == CablingParameters.CircuitRefs
                          || one.Id == CablingParameters.TapCount)
            .Select(one => string.Join(" / ", one.Names()))
            .ToList();

        Expect.Same(
            0,
            missing.Count,
            "parameters still unbound after applying, which means the run had nowhere to write: "
            + string.Join(", ", missing));

        context.Note("indicator category", Category(symbol!).ToString());
        context.Note("carrier categories", catalogue.Categories.Count().ToString(CultureInfo.InvariantCulture));
    }

    private static void RefusesAMissingFamily(RevitTestContext context)
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();

        // A name no model has, and said so in the name itself: if this ever matched something, the
        // case would be passing for the wrong reason.
        const string Absent = "BHS_CBL_NoSuchFamily_ForTheSweepOnly";

        var project = CablingProjectSettings.Read(new Fixed
        {
            [RecommendedBoxes.FamilyKey] = Absent,
            [RecommendedBoxes.TypeKey] = "NoSuchType",
        });

        var snapshot = CablingSnapshot.Build(
            document, Options, catalogue, version: 1, project.Boxes, project.DefaultConnection);

        var run = new RouteRun(Array.Empty<RouteResult>(), snapshot.Network.Version, TimeSpan.Zero);
        var outcome = CablingApply.Apply(document, application, run, snapshot, project, catalogue);

        Expect.That(outcome.Refused, "applying accepted an indicator family this model does not have");

        // Named, not merely refused. "Nothing was placed" sends somebody looking through their model;
        // the family name sends them to the setting or to the rename that caused it.
        Expect.That(
            // IndexOf rather than Contains with a comparison: that overload of Contains arrived with
            // .NET Core and does not exist on net48, which is half the supported Revit range.
            outcome.Refusals.Any(one => one.IndexOf(Absent, StringComparison.Ordinal) >= 0),
            "the refusal does not name the family that is missing: " + string.Join(" ", outcome.Refusals));
    }

    private static Autodesk.Revit.DB.BuiltInCategory Category(Autodesk.Revit.DB.FamilySymbol symbol) =>
        (Autodesk.Revit.DB.BuiltInCategory)symbol.Category.Id.Value;

    /// <summary>
    /// Settings that hold exactly what a case puts in them.
    /// </summary>
    /// <remarks>
    /// <b>Fifteen lines instead of loosening the production type, which is the better trade.</b>
    /// <c>CablingProjectSettings</c> is constructed only through <c>Read(ISettings)</c>, and the
    /// first thought was to add a constructor for tests - a change to shipped code so that a test
    /// could reach it. <c>ISettings</c> turns out to be an indexer, a key list and a section view, so
    /// the seam that already exists is enough, and nothing a customer receives changes shape to be
    /// testable.
    /// </remarks>
    private sealed class Fixed : ISettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get => _values.TryGetValue(key, out var found) ? found : null;
            set => _values[key] = value ?? string.Empty;
        }

        public IEnumerable<string> Keys => _values.Keys;

        public ISettings Section(string name)
        {
            var prefix = name + ":";
            var section = new Fixed();

            foreach (var pair in _values)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    section[pair.Key.Substring(prefix.Length)] = pair.Value;
            }

            return section;
        }
    }

    private static RoutingOptions Options { get; } = new()
    {
        JoinTolerance = 0.5,
        MaxApproach = 10,
        AxisAlignedApproach = true,
    };
}
