using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// How many elements a carrier rule sees, and how many of them it admits.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so that a rule can be checked before it is saved.</b> A rule naming a type parameter
/// spelled slightly differently, or a value since renamed, admits nothing - and a run then comes
/// back short, which reads as a model with no structure in it rather than as a mistake in a setting.
/// The number beside the rule is the only thing that tells those apart.
/// </para>
/// <para>
/// <b>It counts what the read would count</b>: the host and every loaded link, with our own
/// recommended-box markers left out. Counting the host alone would understate a project whose trays
/// live in a link - measured, 13 trays and 71 conduits of one surveyed model - and counting the
/// markers in would overstate by however many a previous run placed. Either would be a number that
/// disagrees with the run it is meant to predict.
/// </para>
/// </remarks>
public static class CarrierCount
{
    /// <summary>What one rule finds across the model and its links.</summary>
    public static (int Seen, int Counted) Of(
        Document host,
        RecommendedBoxes boxes,
        BuiltInCategory category,
        CarrierFilter filter)
    {
        var seen = 0;
        var counted = 0;

        Count(host, boxes, category, filter, ref seen, ref counted);

        var links = new FilteredElementCollector(host)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();

        foreach (var link in links)
        {
            if (link.GetLinkDocument() is { } linked)
                Count(linked, boxes, category, filter, ref seen, ref counted);
        }

        return (seen, counted);
    }

    private static void Count(
        Document document,
        RecommendedBoxes boxes,
        BuiltInCategory category,
        CarrierFilter filter,
        ref int seen,
        ref int counted)
    {
        // Per document, exactly as the read does it: a type belongs to the document its element is
        // in, and a link's types are the link's.
        var markers = RecommendedBoxMarkers.For(document, boxes);
        var marking = new CarrierMarkingReader(document, filter);

        var found = new FilteredElementCollector(document)
            .OfCategory(category)
            .WhereElementIsNotElementType();

        foreach (var element in found)
        {
            if (markers.Marks(element))
                continue;

            seen++;

            if (marking.Admits(element))
                counted++;
        }
    }
}
