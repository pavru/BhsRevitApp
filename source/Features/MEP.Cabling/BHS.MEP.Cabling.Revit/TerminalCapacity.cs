using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// How many conductors a device's terminal block holds - asked of its type, and cached by type.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's rule of 2026-09-21.</b> A cable may be cut at a device only while its terminal
/// block has room: the trunk arrives and a run leaves for the next device, which is two conductors
/// of the circuit. A block that takes one ends its branch; one that takes three can start a second.
/// </para>
/// <para>
/// <b>Absent means the type did not say, and the caller falls back to the project's default.</b>
/// That is the whole reason this reader exists rather than a bare <c>AsInteger</c>: an unset integer
/// parameter reads as zero, and a model where nobody filled the capacity in would forbid every
/// splice at every device - the tree collapsing back into a chain, silently, on a correct search.
/// <c>HasValue</c> is what tells a blank from a number.
/// </para>
/// <para>
/// One reader per document, like <see cref="SplicingReader"/> and <see cref="JunctionBoxReader"/>:
/// the type lives in the document its element belongs to. Devices are read from the host, but the
/// rule is the same and costs nothing to keep.
/// </para>
/// </remarks>
internal sealed class TerminalCapacityReader
{
    private readonly Document _document;

    /// <summary>Answer by type id: a circuit has dozens of devices and a handful of types.</summary>
    private readonly Dictionary<long, int?> _types = new();

    public TerminalCapacityReader(Document document) => _document = document;

    /// <summary>What this device's type says, or <c>null</c> when it says nothing usable.</summary>
    public int? Of(Element element)
    {
        if (element is null)
            return null;

        var type = element.GetTypeId();

        if (type == ElementId.InvalidElementId)
            return null;

        if (_types.TryGetValue(type.Value, out var known))
            return known;

        var parameter = _document.GetElement(type)?.get_Parameter(CablingParameters.TerminalCapacity);

        // Zero and below are not answers anybody meant. A device that is on the circuit holds at
        // least one conductor by standing there, so "holds none" is a typo in a type rather than a
        // property of a product - and taken literally it would forbid a splice in a way nobody could
        // explain from the model. It reads as "the type did not say", which is what the project's
        // default is for.
        known = parameter is { HasValue: true } && parameter.AsInteger() > 0
            ? parameter.AsInteger()
            : null;

        _types[type.Value] = known;

        return known;
    }
}
