using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Reads which groups of cables a carrier admits, off the element itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off the instance, unlike role, splicing and capacity, which are read off the type</b> - the
/// owner's answer of 2026-09-22. A divider belongs to the run that was installed, not to the
/// catalogue entry it was cut from.
/// </para>
/// <para>
/// <b>So the cache is keyed by the value, not by the type</b>, and that is the whole difference from
/// its neighbours. A model has thousands of carriers and a handful of distinct answers - "", "ЭОМ",
/// "ЭОМ; СКС" - so parsing is done once per distinct string rather than once per element, and the
/// resulting <see cref="CableGroups"/> is shared by every element that says the same thing.
/// </para>
/// </remarks>
internal sealed class CableGroupReader
{
    private readonly Dictionary<string, CableGroups> _parsed = new(StringComparer.Ordinal);

    /// <summary>Which groups this element admits; unmarked when it says nothing.</summary>
    public CableGroups Of(Element? element)
    {
        var said = element?.get_Parameter(CablingParameters.AllowedGroups)?.AsString();

        if (string.IsNullOrWhiteSpace(said))
            return CableGroups.Unmarked;

        if (_parsed.TryGetValue(said!, out var known))
            return known;

        known = CableGroups.Parse(said);
        _parsed[said!] = known;

        return known;
    }
}
