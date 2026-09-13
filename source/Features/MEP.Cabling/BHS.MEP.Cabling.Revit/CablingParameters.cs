using Autodesk.Revit.DB;
using BHS.Revit.Common.Parameters;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// The shared parameters cabling needs, declared once with identifiers that never change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two, because two have consumers.</b> A parameter declared before something reads it is the
/// same mistake as a mechanism with no consumer, and it is worse here: a parameter that has reached
/// a customer's model cannot be withdrawn, only ignored. The apply phase will want more - a computed
/// length, a route identity - and they arrive with the code that writes them.
/// </para>
/// <para>
/// <b>The GUIDs are new rather than the predecessor's</b> - the owner's decision, a clean slate. They
/// are fixed here for ever: the same parameter across models, families, languages and versions of us
/// is the same GUID, and a different one is a second parameter that looks identical in the palette
/// and shares nothing.
/// </para>
/// <para>
/// <b>Names follow the owner's template, <c>Vendor_Feature_Meaning</c>, and each third answers to a
/// different rule.</b> The vendor is a mark and stays Latin. The feature is an English abbreviation
/// and is <i>the same in every file</i> - <c>Cbl</c> here - so the two names of one parameter differ
/// in exactly one place, which is what makes them recognisable as a pair rather than as two
/// parameters. Only the meaning is in the file's language.
/// </para>
/// </remarks>
public sealed class CablingParameters : SharedParameterScheme
{
    /// <summary>What an element is, when it is one of ours.</summary>
    /// <remarks>
    /// <b>How a junction box is recognised, and the reason it is not recognised by name.</b> A family
    /// or a type gets renamed, and code that matches on a name then goes quiet - "not found" and
    /// "there is no such thing" are indistinguishable from this side. A type parameter with a fixed
    /// GUID says what the thing is regardless of what it, or the parameter itself, is called.
    /// </remarks>
    public static readonly Guid ElementRole = new("37076c5b-ba7c-4f51-b05a-86734af4a0e7");

    /// <summary>How a circuit's devices are connected to the trunk.</summary>
    /// <remarks>
    /// Read from the circuit, and from its panel when the circuit says nothing - a power panel wires
    /// all its circuits through junction boxes, an RS485 panel wires through the terminal. One
    /// parameter bound to both categories rather than two, because it is one question asked at two
    /// levels.
    /// </remarks>
    public static readonly Guid CircuitConnection = new("3aae5787-e914-41e5-b488-2921b1c33407");

    /// <summary>What one of our own elements recommends, and the sign that we placed it.</summary>
    /// <remarks>
    /// <b>It is what makes an indicator ours, and it is not the same question as
    /// <see cref="ElementRole"/>.</b> The role says what an element <i>is</i> - a designer sets it on
    /// a family type, and a real junction box carries it. This says that a calculation of ours put
    /// this instance here and what it is proposing, which nobody sets by hand. Keeping them apart is
    /// what lets a marker become a real box by being connected, without the two ever contradicting
    /// each other.
    /// </remarks>
    public static readonly Guid Recommendation = new("d43fe69a-326b-47a0-95d4-2bb20719a6d4");

    /// <summary>The circuits whose cable passes through this element.</summary>
    /// <remarks>
    /// <b>Written to carriers and to the boxes actually used, and to a real box it is the only thing
    /// written</b> - the owner's decision. A box somebody drew is theirs; what we may add to it is
    /// the fact that these circuits run through it, which is a reading of the model rather than a
    /// proposal about it.
    /// </remarks>
    public static readonly Guid CircuitRefs = new("1b83d465-af5e-4d55-8e7a-b1ba0ee017cf");

    /// <summary>How many cables enter: the trunk in, the trunk out, and every spur.</summary>
    /// <remarks>
    /// The owner's definition of 2026-09-11 - what the box has to take, not what one device needs.
    /// An intermediate box serving one device is three; the last box of a circuit is two; a box two
    /// circuits share takes the sum. It is what a designer picks a real box by, which is why it
    /// counts entries rather than devices.
    /// </remarks>
    public static readonly Guid TapCount = new("39098f30-b004-4d27-a294-9aa978603b7a");

    /// <summary>The value of <see cref="ElementRole"/> that means "this is a junction box".</summary>
    /// <remarks>
    /// <b>Values are not translated, and that is deliberate rather than unfinished.</b> The name is
    /// what a person reads; the value is what code compares. Translate the value and the same family
    /// means different things depending on which Revit the author had open, which is a defect that
    /// travels inside the family and shows up in somebody else's office.
    /// </remarks>
    public const string JunctionBoxRole = "JunctionBox";

    /// <summary>Cut at the terminal: a doubled cable, down to the device and away from it.</summary>
    public const string ConnectionAtTerminal = "Terminal";

    /// <summary>Cut at a junction box: a single cable, with a spur to the device.</summary>
    public const string ConnectionAtJunctionBox = "JunctionBox";

    /// <summary>Categories a junction box family may belong to, one per kind of carrier.</summary>
    /// <remarks>
    /// A list rather than a pair, and open by intent: the owner expects it to grow to rectangular
    /// ducts standing in for plastic trunking.
    /// </remarks>
    private static readonly BuiltInCategory[] CarrierFittings =
    {
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_ConduitFitting,
    };

    /// <summary>Every category a carrier may be, as <c>CarrierCatalogue</c> ships it.</summary>
    /// <remarks>
    /// The shipped defaults, not the whole answer: the catalogue is the user's to configure, so a
    /// project that calls something else a carrier adds that category at run time. Declaring the
    /// four here means the ordinary project needs no runtime list at all, and the unusual one adds
    /// to a set rather than replacing it.
    /// </remarks>
    private static readonly BuiltInCategory[] Carriers =
    {
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting,
    };

    /// <summary>Nothing at compile time - the indicator's family is the project's to choose.</summary>
    /// <remarks>
    /// <b>Empty is the honest declaration, and it carries a danger that has to be met elsewhere.</b>
    /// Which category an indicator belongs to is settled when somebody picks the family, so guessing
    /// a list here would be a registry that drifts from reality - and the day a project picks a
    /// category nobody listed, the parameter would not arrive and the failure would read as "the
    /// tool cannot see my box". The cost is that a caller who forgets to pass the runtime category
    /// binds nothing at all, silently; the apply path therefore refuses out loud rather than
    /// proceeding, because there is no state in which binding none of them is what anybody wanted.
    /// </remarks>
    private static readonly BuiltInCategory[] WhicheverTheIndicatorIs = Array.Empty<BuiltInCategory>();

    private static readonly BuiltInCategory[] CircuitAndPanel =
    {
        BuiltInCategory.OST_ElectricalCircuit,
        BuiltInCategory.OST_ElectricalEquipment,
    };

    protected override string GroupName(ParameterLanguage language) => language switch
    {
        ParameterLanguage.Russian => "BHS Кабели",
        _ => "BHS Cabling",
    };

    protected override IReadOnlyList<SharedParameter> Parameters { get; } = new[]
    {
        new SharedParameter(
            ElementRole,
            SpecTypeId.String.Text,
            GroupTypeId.Data,
            instance: false,
            CarrierFittings,
            english: new ParameterText(
                "BHS_Cbl_ElementRole",
                "What this element is to BHS tools - for example JunctionBox. Set on the type."),
            russian: new ParameterText(
                "BHS_Cbl_РольЭлемента",
                "Чем этот элемент является для инструментов BHS - например JunctionBox. "
                + "Задаётся у типа.")),

        new SharedParameter(
            CircuitConnection,
            SpecTypeId.String.Text,
            GroupTypeId.ElectricalCircuiting,
            instance: true,
            CircuitAndPanel,
            english: new ParameterText(
                "BHS_Cbl_CircuitConnection",
                "How this circuit's devices are connected: Terminal or JunctionBox. "
                + "Left empty on a circuit, the panel's value is used."),
            russian: new ParameterText(
                "BHS_Cbl_ПодключениеЦепи",
                "Как подключены устройства этой цепи: Terminal или JunctionBox. "
                + "Если у цепи пусто, берётся значение щита.")),

        new SharedParameter(
            Recommendation,
            SpecTypeId.String.Text,
            GroupTypeId.Data,
            instance: true,
            WhicheverTheIndicatorIs,
            english: new ParameterText(
                "BHS_Cbl_Recommendation",
                "What a BHS calculation recommends here - for example JunctionBox. "
                + "Written by the tool; not set by hand."),
            russian: new ParameterText(
                "BHS_Cbl_Рекомендация",
                "Что расчёт BHS рекомендует в этом месте - например JunctionBox. "
                + "Пишется инструментом, вручную не задаётся.")),

        new SharedParameter(
            CircuitRefs,
            SpecTypeId.String.Text,
            GroupTypeId.ElectricalCircuiting,
            instance: true,
            Carriers,
            english: new ParameterText(
                "BHS_Cbl_CircuitRefs",
                "The circuits whose cable passes through this element, as they are numbered."),
            russian: new ParameterText(
                "BHS_Cbl_СсылкиНаЦепи",
                "Цепи, кабель которых проходит через этот элемент, с их номерами.")),

        new SharedParameter(
            TapCount,
            SpecTypeId.Int.Integer,
            GroupTypeId.ElectricalCircuiting,
            instance: true,
            WhicheverTheIndicatorIs,
            english: new ParameterText(
                "BHS_Cbl_TapCount",
                "How many cables enter this box: the trunk in, the trunk out, and every spur."),
            russian: new ParameterText(
                "BHS_Cbl_ЧислоВводов",
                "Сколько кабелей входит в эту коробку: магистраль на вход, на выход и каждый отвод.")),
    };
}
