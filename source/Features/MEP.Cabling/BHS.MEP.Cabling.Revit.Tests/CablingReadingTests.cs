using System.Globalization;
using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// What reading a model for cabling has to be true of, whatever model it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariants, never counts.</b> These run against whichever model the sweep opened - somebody's
/// real building - so an assertion of the form "there are 358 carriers" is an assertion about that
/// week rather than about the code, and it fails the day a wall moves. Every case here asserts a
/// relation that must hold for any model at all, and reports the counts as notes so the numbers are
/// still on the record.
/// </para>
/// <para>
/// <b>This is the first automated check the feature code has ever had.</b> Until now the sweep drove
/// the probe and nothing else: three defects in this assembly were found by a person installing the
/// edition and pressing the button, in three separate goes. See CLAUDE.md, on why the runner grew
/// into a test runner rather than a headless engine being lifted into a process of ours.
/// </para>
/// </remarks>
public sealed class CablingReadingTests : IRevitTestSuite
{
    public string Name => "Cabling";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "the default catalogue collects fittings, not only runs",
            CatalogueCollectsFittings),

        new RevitTestCase(
            "every carrier reports a class the catalogue names",
            EveryClassComesFromTheCatalogue,
            needsDocument: true),

        new RevitTestCase(
            "every carrier has somewhere a cable can enter it",
            EveryCarrierHasATerminal,
            needsDocument: true),

        new RevitTestCase(
            "our own markers are excluded, and the count accounts for the difference",
            MarkersAreNotStructure,
            needsDocument: true),

        new RevitTestCase(
            "a marker type this model does not have is reported unknown, not assumed absent",
            AnUnknownMarkerTypeIsReported,
            needsDocument: true),

        new RevitTestCase(
            "reading the same model twice reads the same network",
            ReadingIsRepeatable,
            needsDocument: true),
    };

    /// <summary>
    /// The one case here that needs no model, and it earns its place twice over.
    /// </summary>
    /// <remarks>
    /// It asserts the fact the whole junction-box design rests on - a fitting is a carrier, so a box
    /// modelled as one is already a node of the network and the route already goes through it. And,
    /// running in every mode of the sweep, it is the case that proves the harness itself is alive
    /// when no model is open: without it, a run without a document would report every case skipped,
    /// which is indistinguishable from a suite that failed to load.
    /// </remarks>
    private static void CatalogueCollectsFittings(RevitTestContext context)
    {
        var catalogue = new CarrierCatalogue();

        Expect.That(
            catalogue.ClassOf(BuiltInCategory.OST_CableTrayFitting) == CarrierCatalogue.Tray,
            "a cable tray fitting is not collected as tray, so a junction box modelled as one is not part of the network");

        Expect.That(
            catalogue.ClassOf(BuiltInCategory.OST_ConduitFitting) == CarrierCatalogue.Conduit,
            "a conduit fitting is not collected as conduit");

        Expect.That(
            catalogue.ClassOf(BuiltInCategory.OST_Walls).Length == 0,
            "a category nobody declared is being collected as a carrier");

        var declared = 0;

        foreach (var category in catalogue.Categories)
        {
            Expect.That(
                catalogue.ClassOf(category).Length > 0,
                "category " + category + " is declared and has no class");
            declared++;
        }

        context.Note("catalogue categories", declared.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A class the catalogue never named would divide the network in two silently.
    /// </summary>
    /// <remarks>
    /// The class is what decides whether a carrier is open along its length - a tray a cable may
    /// leave anywhere, a conduit it may not - so a value nobody declared makes a run behave like a
    /// tray by default, which is the wrong direction to guess in: it would invent drops that no
    /// cable can make.
    /// </remarks>
    private static void EveryClassComesFromTheCatalogue(RevitTestContext context)
    {
        var catalogue = new CarrierCatalogue();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var category in catalogue.Categories)
            known.Add(catalogue.ClassOf(category));

        var snapshot = Read(context, catalogue, boxes: null);

        foreach (var carrier in snapshot.Carriers)
        {
            Expect.That(
                known.Contains(carrier.Class),
                "carrier " + carrier.Id + " reports class '" + carrier.Class + "', which the catalogue does not name");
        }

        context.Note("carriers", snapshot.Carriers.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("classes in use", string.Join(", ", Classes(snapshot)));
    }

    /// <summary>
    /// A carrier with no terminal cannot be entered or left, so it is in the network and not on any
    /// route - the kind of absence that shows up as a longer cable rather than as an error.
    /// </summary>
    private static void EveryCarrierHasATerminal(RevitTestContext context)
    {
        var snapshot = Read(context, new CarrierCatalogue(), boxes: null);

        foreach (var carrier in snapshot.Carriers)
        {
            Expect.That(
                carrier.Terminals.Count > 0,
                "carrier " + carrier.Id + " (" + carrier.Class + ") has no terminals, so no route can use it");
        }

        context.Note("carriers", snapshot.Carriers.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The hazard that made the indicator a design question rather than a drawing one.
    /// </summary>
    /// <remarks>
    /// Measured on the owner's model before any of this was written: placing one free-standing
    /// marker took the carrier count from 358 to 359, because the family is a cable tray fitting and
    /// fittings are carriers. So a run that places markers would grow the network it is measuring.
    ///
    /// The assertion is the accounting, not the count: whatever the model holds, the carriers that
    /// disappear when markers are recognised must be exactly the markers that were recognised. A
    /// filter that dropped one carrier too many would pass a count check and fail this one.
    /// </remarks>
    private static void MarkersAreNotStructure(RevitTestContext context)
    {
        var catalogue = new CarrierCatalogue();
        var plain = Read(context, catalogue, boxes: null);
        var filtered = Read(context, catalogue, new RecommendedBoxes(
            RecommendedBoxes.DefaultFamily,
            RecommendedBoxes.DefaultType));

        Expect.Same(
            plain.Carriers.Count - filtered.Carriers.Count,
            filtered.MarkersExcluded,
            "carriers that disappeared when markers were recognised, against markers reported");

        Expect.That(
            plain.MarkersExcluded == 0,
            "a read that was given no marker type still excluded " + plain.MarkersExcluded + " elements");

        context.Note("carriers without the filter", plain.Carriers.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("markers excluded", filtered.MarkersExcluded.ToString(CultureInfo.InvariantCulture));
        context.Note("marker type present in this model", filtered.MarkerTypeKnown ? "yes" : "no");
    }

    /// <summary>
    /// Renaming the type is the one thing the family-and-type pair does not survive, so it has to be
    /// noticed rather than shrugged at: the markers of the last run are still in the model and are
    /// now indistinguishable from structure.
    /// </summary>
    private static void AnUnknownMarkerTypeIsReported(RevitTestContext context)
    {
        var snapshot = Read(
            context,
            new CarrierCatalogue(),
            new RecommendedBoxes("BHS_no_such_family", "no such type"));

        Expect.That(
            !snapshot.MarkerTypeKnown,
            "a family and type nobody has ever created resolved to something");

        Expect.Same(0, snapshot.MarkersExcluded, "elements excluded as markers of a type that does not exist");
    }

    /// <summary>
    /// Two reads of one unchanged model must agree.
    /// </summary>
    /// <remarks>
    /// Revit hands collectors back in an order it does not promise, and the network is built by
    /// walking them. A difference here would mean a route length that changes between two runs on
    /// the same model - the kind of finding a person reports as "sometimes it gives a different
    /// number", which is unresearchable without a check that reproduces it.
    /// </remarks>
    private static void ReadingIsRepeatable(RevitTestContext context)
    {
        var catalogue = new CarrierCatalogue();
        var first = Read(context, catalogue, boxes: null);
        var second = Read(context, catalogue, boxes: null);

        Expect.Same(first.Carriers.Count, second.Carriers.Count, "carriers on a second read of the same model");
        Expect.Same(first.LinksRead, second.LinksRead, "links read");
        Expect.Same(first.CarriersSkipped, second.CarriersSkipped, "carriers skipped");

        var one = TotalLength(first);
        var two = TotalLength(second);

        Expect.That(
            Math.Abs(one - two) < 1e-6,
            "total carrier length differs between two reads: " +
            one.ToString("F6", CultureInfo.InvariantCulture) + " and " +
            two.ToString("F6", CultureInfo.InvariantCulture));

        context.Note("total carrier length, internal feet", one.ToString("F3", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The production reading path, unchanged: a case that read the model its own way would prove
    /// something about itself.
    /// </summary>
    private static CablingSnapshot Read(RevitTestContext context, CarrierCatalogue catalogue, RecommendedBoxes? boxes) =>
        CablingSnapshot.Build(context.Document!, Options, catalogue, version: 1, boxes);

    /// <summary>
    /// Deliberately not read from settings: a case whose result depends on what somebody put in a
    /// file is a case that means something different on every machine.
    /// </summary>
    private static RoutingOptions Options { get; } = new()
    {
        JoinTolerance = 0.5,
        MaxApproach = 10,
        AxisAlignedApproach = true,
    };

    private static IEnumerable<string> Classes(CablingSnapshot snapshot)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var carrier in snapshot.Carriers)
            seen.Add(carrier.Class);

        return seen;
    }

    private static double TotalLength(CablingSnapshot snapshot)
    {
        var total = 0.0;

        foreach (var carrier in snapshot.Carriers)
            total += carrier.Length;

        return total;
    }
}
