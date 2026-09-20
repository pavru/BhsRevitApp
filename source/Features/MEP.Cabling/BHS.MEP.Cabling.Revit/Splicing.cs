using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Whether cable may be spliced in an element - asked of its type, and cached by type.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's rule of 2026-09-20.</b> A cable is not spliced inside a pipe, and an open splice
/// is not made in a tray, which is why the calculation offers a junction box at every tap. But a
/// trunking with a removable cover is a place cable is spliced, and so is a fitting or an access
/// hatch a designer declares as one - and there the cable graph simply branches, with nothing to
/// recommend and nothing to place.
/// </para>
/// <para>
/// <b>On the type, because one category holds both answers.</b> A plain tray and a trunking with a
/// cover are both <c>OST_CableTray</c>, so the catalogue - which maps categories to classes - cannot
/// tell them apart, and the designer can. Same shape as the junction-box role, same reason, and read
/// the same way: by GUID, never by name, because a renamed parameter would make this go quiet rather
/// than fail.
/// </para>
/// <para>
/// <b>Absence is "no", at every step</b> - no type, no parameter, parameter not bound, empty value.
/// The two wrong answers are not equally wrong: a forgotten "no" costs a recommended box, which is a
/// visible suggestion a designer can reject, and a forgotten "yes" would silently drop one.
/// </para>
/// <para>
/// One reader per document, like <see cref="JunctionBoxReader"/>: the type lives in the document the
/// element belongs to, and a link's types belong to the link.
/// </para>
/// </remarks>
internal sealed class SplicingReader
{
    private readonly Document _document;

    /// <summary>Answer by type id: a run has thousands of elements and a handful of types.</summary>
    private readonly Dictionary<long, bool> _types = new();

    public SplicingReader(Document document) => _document = document;

    /// <summary>Whether cable may be spliced in this element.</summary>
    public bool Allows(Element element)
    {
        if (element is null)
            return false;

        var type = element.GetTypeId();

        if (type == ElementId.InvalidElementId)
            return false;

        if (_types.TryGetValue(type.Value, out var known))
            return known;

        // Yes/No is an integer parameter in Revit, and AsInteger on an unset one reads zero - which
        // is the answer we want it to have. HasValue is asked all the same: a parameter that exists
        // and was never filled and a parameter that says no are the same thing here, but reading
        // through HasValue says so on purpose rather than by arithmetic.
        var parameter = _document.GetElement(type)?.get_Parameter(CablingParameters.Splicing);

        known = parameter is { HasValue: true } && parameter.AsInteger() != 0;
        _types[type.Value] = known;

        return known;
    }
}
