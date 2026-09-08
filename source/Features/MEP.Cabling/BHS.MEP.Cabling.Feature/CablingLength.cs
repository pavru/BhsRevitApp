using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// Shows a length the way this document would show it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Revit formats it, not us, and this is the whole reason the screen takes a delegate.</b> Our
/// number stands next to Revit's own - in the properties palette, in a schedule, on a drawing - and
/// the unit, the precision, the group separator, the decimal symbol and whether trailing zeros are
/// suppressed were all chosen by the user and live in the document's <c>Units</c>. A number that
/// disagrees with Revit's on the same screen is worse than no number at all, because it is the one
/// people will believe until they check.
/// </para>
/// <para>
/// It is also why <c>UnitsNet</c> was turned down for Revit-side work: it cannot know any of that,
/// and it has feet-and-fractional-inches nowhere.
/// </para>
/// <para>
/// The four-argument <c>Format</c> is the one that exists on all four supported releases - checked
/// against Autodesk's own samples for 2024 and 2027 rather than remembered, because this API changed
/// shape in 2021 and the memory of that change is exactly the sort that misleads.
/// </para>
/// </remarks>
internal static class CablingLength
{
    /// <summary>Binds a document's unit settings into something the screen can call.</summary>
    /// <remarks>
    /// The <c>Units</c> object is fetched once and captured, rather than the document. That is not
    /// a claim that formatting is thread-free - it is still a Revit call, and it is only ever made
    /// where one is legal: the view model evaluates it for binding on the UI thread, which inside a
    /// modal dialog raised from a command is the API thread. Capturing the document instead would
    /// invite the same call from wherever somebody later invokes the delegate.
    /// </remarks>
    public static Func<double, string> Formatter(Document document)
    {
        var units = document.GetUnits();

        return value => UnitFormatUtils.Format(units, SpecTypeId.Length, value, false);
    }
}
