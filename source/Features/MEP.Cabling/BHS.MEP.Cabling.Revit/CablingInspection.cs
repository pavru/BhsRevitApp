using Autodesk.Revit.DB;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// What the cabling calculation wrote on one element, read back exactly as it stands - the reading half of
/// the inspector pane, apart from WPF so that a sweep can check it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what was written, nothing worked out.</b> The owner's decision of 2026-09-20: the pane shows the
/// values a run left on the element and computes nothing - no routing, no comparison with the model as it is
/// now. Whether a stored length still holds is "Check lengths"; a pane that answered it too would be a second
/// place deciding it.
/// </para>
/// <para>
/// <b>By GUID, never by name.</b> A model bound on a Russian Revit keeps its Russian names on an English one,
/// and <c>Element.get_Parameter(Guid)</c> finds the parameter either way - the rule the scheme was built on.
/// </para>
/// <para>
/// <b>Three states, and "never written" is not zero.</b> A parameter that is not bound to the element's
/// category says the model was never prepared; one that is bound and has no value says no run ever wrote it;
/// a written zero is a zero. Read through <c>HasValue</c>, as <see cref="StoredRoutes"/> reads the same three
/// values for the check of stale lengths - and a text parameter holding an empty string reads as unwritten,
/// because no writer of ours writes one.
/// </para>
/// <para>
/// <b>What the element is decides what is read,</b> in this order: a link instance (the calculation writes
/// nothing into links, and selecting inside one selects the instance); an electrical circuit; an element
/// carrying our recommendation, which is what makes an indicator ours - the same mark the apply uses; an
/// element whose type carries the junction-box role; any other element bound to <c>BHS_Cbl_CircuitRefs</c>,
/// which is a carrier. Anything else carries nothing of ours. A panel is not read, although its circuits
/// inherit <c>BHS_Cbl_CircuitConnection</c> from it: the owner's list names circuits, carriers, boxes and
/// indicators.
/// </para>
/// </remarks>
public static class CablingInspection
{
    /// <summary>Reads what the calculation wrote on the element <paramref name="elementId"/> of <paramref name="document"/>.</summary>
    /// <returns>Null when the id names no element in this document - deleted since it was selected.</returns>
    public static ElementInspection? Read(Document document, long elementId)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        if (document.GetElement(new ElementId(elementId)) is not { } element)
            return null;

        var inspection = new ElementInspection(elementId, element.Name ?? string.Empty, element.Category?.Name ?? string.Empty);

        if (element is RevitLinkInstance)
        {
            inspection.Kind = InspectedKind.Link;
            return inspection;
        }

        if (element.Category?.Id.Value == (long)BuiltInCategory.OST_ElectricalCircuit)
        {
            inspection.Kind = InspectedKind.Circuit;
            inspection.CableLength = Length(element, CablingParameters.CableLength);
            inspection.LengthInTray = Length(element, CablingParameters.LengthInTray);
            inspection.LengthInConduit = Length(element, CablingParameters.LengthInConduit);
            inspection.LengthFree = Length(element, CablingParameters.LengthFree);
            inspection.LengthOther = Length(element, CablingParameters.LengthOther);
            inspection.LengthSlack = Length(element, CablingParameters.LengthSlack);
            inspection.RouteConnection = Text(element, CablingParameters.RouteConnection);
            inspection.RouteStamp = Text(element, CablingParameters.RouteStamp);
            inspection.CircuitConnection = Text(element, CablingParameters.CircuitConnection);
            return inspection;
        }

        var recommendation = Text(element, CablingParameters.Recommendation);
        var refs = Text(element, CablingParameters.CircuitRefs);

        if (recommendation.State == StoredState.Written)
        {
            inspection.Kind = InspectedKind.Indicator;
            inspection.Recommendation = recommendation;
            inspection.TapCount = Integer(element, CablingParameters.TapCount);
            inspection.CircuitRefs = refs;
            return inspection;
        }

        if (IsJunctionBox(document, element))
        {
            inspection.Kind = InspectedKind.JunctionBox;
            inspection.CircuitRefs = refs;
            return inspection;
        }

        if (refs.State != StoredState.NotBound)
        {
            inspection.Kind = InspectedKind.Carrier;
            inspection.CircuitRefs = refs;
            return inspection;
        }

        inspection.Kind = InspectedKind.NotOurs;
        return inspection;
    }

    /// <summary>The ids an id list holds - a stamp or a list of circuits - as they are spelled, in order.</summary>
    /// <remarks>
    /// Split on the separator the writers use and nothing else: a <c>link:element</c> entry stays one entry.
    /// </remarks>
    public static IReadOnlyList<string> Entries(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text!.Split(';').Select(one => one.Trim()).Where(one => one.Length > 0).ToArray();

    private static bool IsJunctionBox(Document document, Element element)
    {
        var type = element.GetTypeId() is { } typeId && typeId != ElementId.InvalidElementId
            ? document.GetElement(typeId)
            : null;

        return type?.get_Parameter(CablingParameters.ElementRole) is { HasValue: true } role
               && string.Equals(role.AsString()?.Trim(), CablingParameters.JunctionBoxRole, StringComparison.OrdinalIgnoreCase);
    }

    private static Stored<double> Length(Element element, Guid id) =>
        element.get_Parameter(id) switch
        {
            null => Stored<double>.NotBound,
            { HasValue: true } found => Stored<double>.Of(found.AsDouble()),
            _ => Stored<double>.NeverWritten,
        };

    private static Stored<int> Integer(Element element, Guid id) =>
        element.get_Parameter(id) switch
        {
            null => Stored<int>.NotBound,
            { HasValue: true } found => Stored<int>.Of(found.AsInteger()),
            _ => Stored<int>.NeverWritten,
        };

    private static Stored<string> Text(Element element, Guid id) =>
        element.get_Parameter(id) switch
        {
            null => Stored<string>.NotBound,
            { HasValue: true } found when found.AsString() is { Length: > 0 } text => Stored<string>.Of(text),
            _ => Stored<string>.NeverWritten,
        };
}

/// <summary>What an inspected element is to cabling.</summary>
public enum InspectedKind
{
    /// <summary>Carries nothing the calculation writes.</summary>
    NotOurs,

    /// <summary>A link instance: nothing is written into links.</summary>
    Link,

    /// <summary>An electrical circuit: its length, how it is laid, how it was routed and along what.</summary>
    Circuit,

    /// <summary>An indicator of ours: what it recommends and how many cables enter.</summary>
    Indicator,

    /// <summary>A junction box a designer placed: only the circuits through it.</summary>
    JunctionBox,

    /// <summary>A carrier: the circuits through it.</summary>
    Carrier,
}

/// <summary>Whether a parameter is on the element, and whether anything was ever written into it.</summary>
public enum StoredState
{
    /// <summary>The parameter is not bound to this element's category in this model.</summary>
    NotBound,

    /// <summary>Bound, and never written.</summary>
    NeverWritten,

    /// <summary>Written; the value is what stands.</summary>
    Written,
}

/// <summary>One value as it stands on the element.</summary>
/// <remarks>
/// A struct with a state rather than a nullable, because "not bound" and "never written" are two findings -
/// one says the model was never prepared, the other that no run reached this element.
/// </remarks>
public readonly struct Stored<T>
{
    private Stored(StoredState state, T value)
    {
        State = state;
        Value = value;
    }

    public static Stored<T> NotBound => new(StoredState.NotBound, default!);

    public static Stored<T> NeverWritten => new(StoredState.NeverWritten, default!);

    public static Stored<T> Of(T value) => new(StoredState.Written, value);

    public StoredState State { get; }

    /// <summary>The value; meaningful only when <see cref="State"/> is <see cref="StoredState.Written"/>.</summary>
    public T Value { get; }

    public bool Written => State == StoredState.Written;
}

/// <summary>What <see cref="CablingInspection"/> read off one element.</summary>
/// <remarks>
/// Every value starts as <see cref="StoredState.NotBound"/> and only those the element's kind asks for are
/// read; the rest stay so. Settable rather than <c>init</c>: <c>init</c> on <c>net48</c> needs a polyfill in
/// the AppDomain Revit 2024 shares with every other vendor.
/// </remarks>
public sealed class ElementInspection
{
    public ElementInspection(long id, string name, string category)
    {
        Id = id;
        Name = name;
        Category = category;
    }

    public long Id { get; }

    /// <summary>The element's name as Revit gives it - for a circuit, its number.</summary>
    public string Name { get; }

    /// <summary>The element's category, in Revit's own words and language.</summary>
    public string Category { get; }

    public InspectedKind Kind { get; set; }

    public Stored<double> CableLength { get; set; }

    public Stored<double> LengthInTray { get; set; }

    public Stored<double> LengthInConduit { get; set; }

    public Stored<double> LengthFree { get; set; }

    public Stored<double> LengthOther { get; set; }

    public Stored<double> LengthSlack { get; set; }

    /// <summary>The connection the stored length was computed with: the calculation's output.</summary>
    public Stored<string> RouteConnection { get; set; }

    /// <summary>The carriers the stored length was measured along.</summary>
    public Stored<string> RouteStamp { get; set; }

    /// <summary>The connection the designer asked for on the circuit itself: an input, not a result.</summary>
    /// <remarks>Only the circuit's own value; what it inherits from its panel is not read here.</remarks>
    public Stored<string> CircuitConnection { get; set; }

    public Stored<string> CircuitRefs { get; set; }

    public Stored<string> Recommendation { get; set; }

    public Stored<int> TapCount { get; set; }
}
