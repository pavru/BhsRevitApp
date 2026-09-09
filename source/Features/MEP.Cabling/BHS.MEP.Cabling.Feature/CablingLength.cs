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

        WithASymbol(units);

        return value => UnitFormatUtils.Format(units, SpecTypeId.Length, value, false);
    }

    /// <summary>
    /// Gives the number its unit back, when the document suppresses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured on the first press, and it is the one place where saying exactly what Revit says
    /// is wrong.</b> This model's length format suppresses the unit symbol, so the line read
    /// "470354 computed, Revit reports 281871" - two bare integers, on a dialog, with nothing
    /// nearby to say what they are. Revit gets away with the same setting because its number sits
    /// in a row labelled Length in a palette whose units the reader already knows; our sentence has
    /// neither.
    /// </para>
    /// <para>
    /// So exactly one thing is overridden - whether a symbol is shown - and everything the user
    /// chose about precision, rounding, separators and the unit itself is left alone. It is written
    /// on a copy: <c>Document.GetUnits</c> hands one out, and changing the document's own would need
    /// a transaction, which is how Autodesk's own <c>UnitsAPI</c> sample does it.
    /// </para>
    /// </remarks>
    private static void WithASymbol(Units units)
    {
        try
        {
            var options = units.GetFormatOptions(SpecTypeId.Length);

            if (!options.CanHaveSymbol() || !options.GetSymbolTypeId().Empty())
                return;

            foreach (var symbol in FormatOptions.GetValidSymbols(options.GetUnitTypeId()))
            {
                // The list leads with the empty one, which is the "no symbol" the document already
                // chose. The first real entry is the unit's ordinary spelling.
                if (symbol.Empty())
                    continue;

                options.SetSymbolTypeId(symbol);
                units.SetFormatOptions(SpecTypeId.Length, options);
                return;
            }
        }
        catch (Exception)
        {
            // A number without its unit is a poor line; a command that will not open because the
            // unit settings are shaped unusually is a worse one. The plain format still works.
        }
    }
}
