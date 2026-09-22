using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// What a project counts as a carrier, said in one line for the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on this side because a category's name is Revit's to give</b>, and it differs between a
/// Russian and an English Revit. The window is on the plain axis and receives the finished string,
/// the same arrangement as the apply report.
/// </para>
/// <para>
/// <b>Silent where there is nothing to say.</b> A line naming the four shipped categories after
/// every run is a true sentence that pushes the findings down the screen, and this window already
/// follows that rule for cable groups and for box capacity. It speaks when the project stated a
/// catalogue of its own - because then "no route" may mean "this project does not count trays" - and
/// whenever a declared category yielded nothing.
/// </para>
/// </remarks>
public static class CarrierReport
{
    /// <summary>The line, or empty when nothing about the catalogue is worth a person's attention.</summary>
    public static string Describe(Document document, IReadOnlyList<CarrierTally> tallies, bool declared)
    {
        if (tallies is null || tallies.Count == 0)
            return string.Empty;

        var empty = tallies.Where(one => one.Empty).ToList();
        var incomparable = tallies.Where(one => one.Incomparable != 0).ToList();

        if (!declared && empty.Count == 0 && incomparable.Count == 0)
            return string.Empty;

        var said = new List<string>();

        // The finding first, and in the words that name the likely cause. A filter matching nothing
        // and a category holding nothing produce the same run, and only the count tells them apart -
        // which is why the count is in the sentence rather than the verdict.
        if (empty.Count != 0)
        {
            var named = empty
                .Select(one => $"{Name(document, one.Category)} ({one.Filter}, 0 of {one.Seen})")
                .ToList();

            said.Add(
                "Nothing counts as a carrier in " + empty.Count + " of the categories this project declared: "
                + string.Join(", ", named)
                + ". A rule that matches nothing reads exactly like a model that holds nothing.");
        }

        if (incomparable.Count != 0)
        {
            var named = incomparable
                .Select(one => $"{Name(document, one.Category)} ({one.Filter})")
                .ToList();

            said.Add(
                "The parameter named for " + string.Join(", ", named)
                + " holds a length, which cannot be compared as text: its stored value is in feet and a"
                + " project states millimetres.");
        }

        if (declared)
        {
            var named = tallies
                .Select(one => Name(document, one.Category) + " (" + Says(one) + ")")
                .ToList();

            said.Add(
                "This project states its own carrier categories instead of the shipped trays and conduits: "
                + string.Join(", ", named) + ".");
        }

        return string.Join(" ", said);
    }

    /// <summary>What one category counts as, and how much of it counted.</summary>
    private static string Says(CarrierTally tally)
    {
        var rule = tally.Filter.Absent ? tally.Class : tally.Class + ", " + tally.Filter;

        return tally.Filter.Absent
            ? rule + ", " + tally.Counted
            : rule + ", " + tally.Counted + " of " + tally.Seen;
    }

    /// <summary>What this Revit calls the category, or its identifier when the model has no such.</summary>
    private static string Name(Document document, BuiltInCategory category) =>
        Category.GetCategory(document, category)?.Name ?? category.ToString();
}
