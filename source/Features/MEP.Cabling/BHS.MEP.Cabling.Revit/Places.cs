using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Where an element is, when its connectors will not say.
/// </summary>
/// <remarks>
/// <para>
/// <b>The centre of the bounding box, and it is the owner's call over the insertion point.</b> A
/// panel's insertion point can sit at a corner, on the wall face, or outside the body altogether -
/// it is where the family was placed, not where the equipment is. The centre of its extent is
/// always inside the thing, which is a better guess at where a cable arrives.
/// </para>
/// <para>
/// It is a guess and stays labelled as one. The box is axis-aligned in model coordinates and covers
/// the whole element, so for something long - a busbar, a run of trunking - the centre can be far
/// from any terminal. It is reached only when Revit offers nothing better, and what it is worth
/// depends on the element it was asked about.
/// </para>
/// </remarks>
internal static class Places
{
    /// <summary>The middle of the element's extent, or null when it has none.</summary>
    /// <remarks>
    /// <c>null</c> for the view means the model box rather than what some view happens to crop.
    /// An element can still have no box at all - a purely logical one, or one whose geometry is not
    /// loaded - so the caller keeps a rung below this.
    /// </remarks>
    public static XYZ? CentreOrNull(this Element element)
    {
        var box = element?.get_BoundingBox(null);

        return box is null ? null : (box.Min + box.Max) / 2;
    }
}
