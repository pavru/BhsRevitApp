using System.Globalization;
using BHS.MEP.Cabling.Revit;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// Everything the read of the model left behind, said in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place because otherwise it becomes two, and two disagree.</b> The counts were written so
/// that nothing is lost silently; the collect command displayed them and the routing command did
/// not, which is its own kind of silence - a run can come back with half its circuits unroutable
/// while the reason sits in a number nobody was shown.
/// </para>
/// <para>
/// The wording lives on this side rather than in the routing assembly on purpose. What a link is,
/// and what it means for one to be placed but not loaded, is knowledge the Revit side has and the
/// search does not; the search carries these lines and never reads them.
/// </para>
/// </remarks>
internal static class CablingGaps
{
    /// <summary>What was skipped, one line each, empty when nothing was.</summary>
    public static IReadOnlyList<string> Describe(CablingSnapshot snapshot)
    {
        var lines = new List<string>();

        Add(lines, snapshot.CarriersSkipped, "carriers with no readable geometry");
        Add(lines, snapshot.LinksNotLoaded, "links placed but not loaded");
        Add(lines, snapshot.NestedLinksIgnored, "links inside links, not followed");
        Add(lines, snapshot.Circuits.WithoutPanel, "circuits with no panel");
        Add(lines, snapshot.Circuits.WithoutDevices, "circuits with no reachable device");
        Add(lines, snapshot.Circuits.DevicesSkipped, "devices dropped from circuits that were described");

        // Named, not only counted: a typo in a connection value is fixed on one circuit or one panel,
        // and a count sends the designer looking through all of them.
        // A box that is joined to nothing is a modelling fault, and from the routing side it looks
        // like a box the calculation ignored for no reason. Said, therefore, rather than dropped.
        Add(lines, snapshot.BoxesUnconnected, "junction boxes by their type, joined to no carrier");

        var unreadable = snapshot.Circuits.UnreadableConnection;

        if (unreadable.Count > 0)
        {
            lines.Add(unreadable.Count.ToString(CultureInfo.CurrentCulture)
                      + " circuit(s) with a connection value that is neither Terminal nor JunctionBox,"
                      + " routed with the project's default: " + string.Join("; ", unreadable.Take(5))
                      + (unreadable.Count > 5 ? "; ..." : string.Empty));
        }

        return lines;
    }

    /// <summary>The same, as one block for a dialog that takes text.</summary>
    public static string AsText(CablingSnapshot snapshot) => string.Join("\n", Describe(snapshot));

    private static void Add(List<string> lines, int count, string what)
    {
        if (count > 0)
            lines.Add(count.ToString(CultureInfo.CurrentCulture) + " " + what);
    }
}
