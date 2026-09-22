using System.Globalization;
using Autodesk.Revit.DB;
using BHS.Settings;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// Which categories carry cable, what class each one counts as, and which of their elements count.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list rather than a switch, because the predecessor's switch grew.</b> It hard-coded trays
/// and conduits, then acquired nine device categories and a generic-model escape hatch for the
/// models where somebody had drawn a tray as something else. The set is the user's to configure;
/// the defaults below are only what a project usually looks like.
/// </para>
/// <para>
/// The class is a string for the same reason it is a string on
/// <see cref="BHS.MEP.Cabling.Routing.CarrierNode.Class"/>: the preference between conduit and tray
/// is expressed in it, and a user who adds a category has to be able to say which of the two it
/// behaves like - or to name a third.
/// </para>
/// <para>
/// <b>Read from the project since 2026-09-22, and until then this was a constant.</b> Every command
/// built one with the four shipped categories and nothing could change it, so the sentence above -
/// "the set is the user's to configure" - described an intention rather than the code. What a
/// project says now replaces the defaults outright; see <see cref="Read"/> for why replacing and
/// not adding.
/// </para>
/// </remarks>
public sealed class CarrierCatalogue
{
    /// <summary>The class name the routing options' conduit preference is expressed against.</summary>
    public const string Conduit = "conduit";

    /// <summary>The class name for tray-like carriers.</summary>
    public const string Tray = "tray";

    /// <summary>Where a project states which of its categories carry cable.</summary>
    /// <remarks>
    /// <b>Project-scoped, so <c>Model:</c>, like every other cabling rule.</b> Which categories a
    /// building's cable runs in is a fact about the building, not a preference of whoever opened it,
    /// and two people opening one model have to get one answer out of it.
    /// </remarks>
    public const string CarriersKey = "Model:Cabling:Carriers";

    /// <summary>What class a declared category counts as.</summary>
    public const string ClassField = "Class";

    /// <summary>The name of the type parameter that decides whether an element counts.</summary>
    public const string ParameterField = "Parameter";

    /// <summary>The value that parameter has to hold; empty means "holds anything at all".</summary>
    public const string ValueField = "Equals";

    /// <summary>What a project that says nothing carries cable with.</summary>
    /// <remarks>
    /// <b>Trays, conduits and their fittings, and nothing else</b> - the owner's answer of
    /// 2026-09-22 when ducts were offered as a fifth. A duct is a duct until somebody says
    /// otherwise, and a shipped default that read the ventilation of every model as cable structure
    /// would be wrong in the direction nobody checks.
    /// </remarks>
    public static IReadOnlyDictionary<BuiltInCategory, string> Defaults { get; } =
        new Dictionary<BuiltInCategory, string>
        {
            [BuiltInCategory.OST_CableTray] = Tray,
            [BuiltInCategory.OST_CableTrayFitting] = Tray,
            [BuiltInCategory.OST_Conduit] = Conduit,
            [BuiltInCategory.OST_ConduitFitting] = Conduit,
        };

    private readonly Dictionary<BuiltInCategory, string> _classes;

    private readonly Dictionary<BuiltInCategory, CarrierFilter> _filters;

    /// <summary>Which classes a cable may leave anywhere along, by class name.</summary>
    /// <remarks>
    /// A class not named here is open, and that is the shipped answer rather than an oversight: a
    /// pipe is the exception among carriers, and a project that invents a class - trunking, say -
    /// means something a cable comes out of.
    /// </remarks>
    private readonly Dictionary<string, bool> _open;

    public CarrierCatalogue(
        IReadOnlyDictionary<BuiltInCategory, string>? classes = null,
        IReadOnlyDictionary<string, bool>? openAlongTheirLength = null,
        IReadOnlyDictionary<BuiltInCategory, CarrierFilter>? filters = null,
        bool declared = false,
        string unreadable = "",
        InstallationMethods? methods = null)
    {
        Declared = declared;
        Methods = methods ?? InstallationMethods.Off;
        Unreadable = Methods.Unreadable.Length == 0 ? unreadable
            : unreadable.Length == 0 ? Methods.Unreadable
            : unreadable + "; " + Methods.Unreadable;

        _open = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [Tray] = true,
            [Conduit] = false,
        };

        if (openAlongTheirLength is not null)
        {
            foreach (var pair in openAlongTheirLength)
                _open[pair.Key] = pair.Value;
        }

        _filters = new Dictionary<BuiltInCategory, CarrierFilter>();

        if (filters is not null)
        {
            foreach (var pair in filters)
                _filters[pair.Key] = pair.Value;
        }

        if (classes is null)
        {
            _classes = new Dictionary<BuiltInCategory, string>(Defaults.Count);

            foreach (var pair in Defaults)
                _classes[pair.Key] = pair.Value;

            return;
        }

        // Copied pair by pair rather than cast to IDictionary. The cast works for a Dictionary and
        // throws for every other implementation of the read-only interface the parameter asks for -
        // an exception thrown by the type that accepted the argument, on a caller that did nothing
        // wrong.
        _classes = new Dictionary<BuiltInCategory, string>(classes.Count);

        foreach (var pair in classes)
            _classes[pair.Key] = pair.Value;
    }

    /// <summary>Whether the project stated a catalogue of its own, rather than taking the defaults.</summary>
    /// <remarks>
    /// The screen says so. A project that declared its carriers and left trays out gets the answer
    /// it asked for, and the difference between "this model has no trays" and "this project does not
    /// count trays" is one a person has to be told about rather than deduce from a route that is
    /// missing.
    /// </remarks>
    public bool Declared { get; }

    /// <summary>How the project names its installation methods, and the slot each one is written into.</summary>
    /// <remarks>
    /// Carried by the catalogue because it is read from the same carriers and edited in the same window:
    /// which elements carry cable, and how each one is laid, are two questions about one list.
    /// </remarks>
    public InstallationMethods Methods { get; }

    /// <summary>Rules that are present and cannot be read, or empty.</summary>
    /// <remarks>
    /// The rule this repository applies to every value: absence is ordinary and gets the default, a
    /// typo is not and gets said out loud. A category name nobody recognises would otherwise be a
    /// carrier class that silently collects nothing.
    /// </remarks>
    public string Unreadable { get; }

    /// <summary>The categories to collect, host and link alike.</summary>
    public IEnumerable<BuiltInCategory> Categories => _classes.Keys;

    /// <summary>What class a category counts as, or empty when it is not a carrier at all.</summary>
    public string ClassOf(BuiltInCategory category) =>
        _classes.TryGetValue(category, out var found) ? found : string.Empty;

    /// <summary>What a category's elements have to say about themselves to count as carriers.</summary>
    /// <remarks>
    /// <b>Only where the project asked for one</b> - the owner's answer of 2026-09-22. A filter that
    /// applied everywhere would, on the day it shipped, take the trays and conduits away from every
    /// model in existence, because nobody has marked their types. So a category declared without one
    /// counts whole, exactly as all four have since the beginning.
    /// </remarks>
    public CarrierFilter FilterOf(BuiltInCategory category) =>
        _filters.TryGetValue(category, out var found) ? found : CarrierFilter.None;

    /// <summary>Whether a cable may leave carriers of this class anywhere along them.</summary>
    /// <remarks>
    /// <para>
    /// <b>Moved here from the routing core on 2026-09-20, and the move is the point.</b> The core
    /// used to answer it itself, as <c>Class != "conduit"</c> - a rule about how carriers behave,
    /// written as a string comparison, in the one assembly the user cannot configure. The class is
    /// what a project configures, so a project naming a third class had no way to say what it
    /// behaves like; now it says so here, beside the categories, and the core is told the answer.
    /// </para>
    /// <para>
    /// This is not the same question as whether cable may be <i>spliced</i> in a carrier. A tray is
    /// open along its length and is still no place for a splice; a trunking with a removable cover is
    /// both. The first is a property of the class and lives here; the second is a property of the
    /// Revit type and lives on the element - see <c>CablingParameters.Splicing</c>.
    /// </para>
    /// </remarks>
    public bool IsOpenAlongItsLength(string carrierClass) =>
        !_open.TryGetValue(carrierClass ?? string.Empty, out var open) || open;

    /// <summary>What the project says its carriers are, or the shipped defaults when it says nothing.</summary>
    /// <remarks>
    /// <para>
    /// <b>The spelling</b>, one key per field so that the flat map a model stores settings in holds
    /// it without indices:
    /// </para>
    /// <code>
    /// Model:Cabling:Carriers:OST_DuctCurves:Class     = trunking
    /// Model:Cabling:Carriers:OST_DuctCurves:Parameter = Способ прокладки
    /// Model:Cabling:Carriers:OST_DuctCurves:Equals    = Кабель-канал
    /// </code>
    /// <para>
    /// <b>Replacing rather than adding</b> - the owner's answer of 2026-09-22. A list that only ever
    /// grew would be one a project could not shorten: a building whose conduits are drawn as
    /// something else could add the something else and never stop the conduits being read. The cost
    /// is that a project adding ducts has to name trays and conduits again, and it is paid by saying
    /// so out loud: <see cref="Declared"/> reaches the screen, so a catalogue that quietly lost the
    /// trays reports itself instead of turning into routes that are not there.
    /// </para>
    /// <para>
    /// <b>The category is stored by its enum name</b>, not by its number and not by its label. The
    /// label is what the user's Revit calls it and differs between languages; the number is stable
    /// and unreadable. The name is the one spelling that survives both a translated interface and a
    /// person reading the model's settings.
    /// </para>
    /// </remarks>
    public static CarrierCatalogue Read(ISettings model)
    {
        var section = model.Section(CarriersKey);

        // The first segment of every key under the section: a category is declared by having said
        // anything at all about itself.
        var names = new List<string>();

        foreach (var key in section.Keys)
        {
            var cut = key.IndexOf(':');
            var name = (cut < 0 ? key : key.Substring(0, cut)).Trim();

            if (name.Length != 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        var methods = InstallationMethods.Read(model);

        if (names.Count == 0)
            return new CarrierCatalogue(methods: methods);

        var classes = new Dictionary<BuiltInCategory, string>();
        var filters = new Dictionary<BuiltInCategory, CarrierFilter>();
        var unreadable = new List<string>();

        foreach (var name in names)
        {
            if (!Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var category)
                || !Enum.IsDefined(typeof(BuiltInCategory), category))
            {
                unreadable.Add(CarriersKey + ":" + name + " - no category of that name");
                continue;
            }

            var one = section.Section(name);
            var carrierClass = (one[ClassField] ?? string.Empty).Trim();

            if (carrierClass.Length == 0)
            {
                unreadable.Add(CarriersKey + ":" + name + ":" + ClassField + " is empty");
                continue;
            }

            classes[category] = carrierClass;

            var filter = CarrierFilter.Of(one[ParameterField], one[ValueField]);

            if (!filter.Absent)
                filters[category] = filter;
        }

        // Every rule unreadable is the same as none stated, and the defaults are the honest answer -
        // a model with no carriers at all would report itself as a modelling fault. The text travels
        // either way.
        return new CarrierCatalogue(
            classes.Count == 0 ? null : classes,
            openAlongTheirLength: null,
            filters,
            declared: classes.Count != 0,
            unreadable: string.Join("; ", unreadable),
            methods: methods);
    }
}

/// <summary>What an element's type has to say for the element to count as a carrier.</summary>
/// <remarks>
/// <para>
/// <b>The user's parameter, not ours - the owner's answer of 2026-09-22.</b> The question was
/// whether to ship a <c>BHS_Cbl_Носитель</c> flag beside the role, the splicing permission and the
/// capacity, all of which are ours and read by GUID. The answer was no: a project that decides to
/// draw its cable channels as ducts already has its own way of telling them from ventilation, and
/// it looks after that itself.
/// </para>
/// <para>
/// <b>Which means this is the one place in the program that finds a parameter by name</b>, and the
/// rule it appears to break is worth restating rather than quietly bending. Nothing looks up
/// <i>our</i> parameters by name, because a name that reached a model cannot be taken back and
/// differs between a Russian and an English Revit - so ours travel by GUID, and that stays true.
/// A parameter the user chose has no GUID we could know, and naming it is the only way to ask for
/// it. The cost lands where the user can see it: a name that matches nothing admits nothing, and
/// the count of what matched is reported rather than left at zero in silence.
/// </para>
/// <para>
/// <b>Asked of the type</b> - the owner's answer to whether one duct type is ever a cable channel in
/// one place and ventilation in another. It is not, so the answer belongs to the product rather than
/// to the run, which is where the role, the splicing permission and both capacities already live.
/// </para>
/// </remarks>
public sealed class CarrierFilter
{
    private CarrierFilter(string parameter, string value)
    {
        Parameter = parameter;
        Value = value;
    }

    /// <summary>What a category counts whole, with nothing asked of its elements.</summary>
    public static CarrierFilter None { get; } = new(string.Empty, string.Empty);

    /// <summary>The name of the type parameter to read.</summary>
    public string Parameter { get; }

    /// <summary>The value it has to hold, or empty for "holds anything at all".</summary>
    public string Value { get; }

    /// <summary>Whether this asks nothing, so the category counts whole.</summary>
    public bool Absent => Parameter.Length == 0;

    /// <summary>A filter, or <see cref="None"/> when no parameter was named.</summary>
    /// <remarks>
    /// <b>A value nobody stated means "filled in with something"</b>, rather than "equal to the
    /// empty string". That is the shape a project wants when the parameter it chose is the one
    /// naming how a run is installed: every type that answered the question is a carrier, and the
    /// answers themselves are its business. Equality is the other half and needs both halves stated.
    /// </remarks>
    public static CarrierFilter Of(string? parameter, string? value)
    {
        var named = (parameter ?? string.Empty).Trim();

        return named.Length == 0 ? None : new CarrierFilter(named, (value ?? string.Empty).Trim());
    }

    /// <summary>Whether a value read off a type satisfies this.</summary>
    /// <remarks>
    /// Compared without case and without surrounding space, the same rule as a cable group, and for
    /// the same reason: both sides are typed by a person, once in a parameter and once in a setting.
    /// </remarks>
    public bool Admits(string? read)
    {
        if (read is null)
            return false;

        var found = read.Trim();

        return Value.Length == 0
            ? found.Length != 0
            : string.Equals(found, Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rule in one line, for the screen and for notes.</summary>
    public override string ToString() =>
        Absent ? string.Empty : Value.Length == 0 ? Parameter + " (any value)" : Parameter + " = " + Value;
}

/// <summary>Reads a user-chosen type parameter as text, and caches the answer by type.</summary>
/// <remarks>
/// <para>
/// <b>As Revit stores it, never as Revit shows it.</b> <c>AsValueString</c> formats through the
/// current culture and the document's units, so a Yes/No would compare as "Да" on one machine and
/// "Yes" on another, and a number would move its decimal separator - which is the defect this
/// repository already found in the predecessor's length parameter. Text is text, an integer is its
/// digits (so a Yes/No is <c>1</c> or <c>0</c>), and an element reference is the name of what it
/// points at, which is what a person means by "the system this run belongs to".
/// </para>
/// <para>
/// <b>A length is refused rather than guessed.</b> Its stored value is in internal feet and the
/// project would be typing millimetres, so any comparison we invented would be right for one of
/// them and plausible for the other. Refused values are counted and named on the screen.
/// </para>
/// <para>
/// One reader per document, like <see cref="SplicingReader"/>: the type lives in the document the
/// element belongs to, and a link's types belong to the link.
/// </para>
/// </remarks>
internal sealed class CarrierMarkingReader
{
    private readonly Document _document;

    private readonly CarrierFilter _filter;

    /// <summary>Answer by type id: a run has thousands of elements and a handful of types.</summary>
    private readonly Dictionary<long, bool> _types = new();

    public CarrierMarkingReader(Document document, CarrierFilter filter)
    {
        _document = document;
        _filter = filter;
    }

    /// <summary>Elements whose type holds the parameter in a form no comparison can read.</summary>
    public int Incomparable { get; private set; }

    /// <summary>Whether this element's type says what the project asked it to say.</summary>
    public bool Admits(Element element)
    {
        if (_filter.Absent)
            return true;

        if (element is null)
            return false;

        var type = element.GetTypeId();

        if (type == ElementId.InvalidElementId)
            return false;

        if (_types.TryGetValue(type.Value, out var known))
            return known;

        known = Says(_document.GetElement(type));
        _types[type.Value] = known;

        return known;
    }

    private bool Says(Element? type)
    {
        var text = TypeParameterText.Read(_document, type, _filter.Parameter, out var incomparable);

        if (incomparable)
            Incomparable++;

        return text is not null && _filter.Admits(text);
    }
}

/// <summary>How a carrier is installed, as the project's type parameter says, cached by type.</summary>
/// <remarks>
/// Empty when the project named no parameter, when the type lacks it or leaves it blank, and when it
/// holds a kind no comparison can read. All of those put the length under «прочая» and the screen
/// names the carriers, so none of them is a reason to fail the read.
/// </remarks>
internal sealed class InstallationMethodReader
{
    private readonly Document _document;

    private readonly string _parameter;

    private readonly Dictionary<long, string> _types = new();

    public InstallationMethodReader(Document document, string parameter)
    {
        _document = document;
        _parameter = parameter;
    }

    /// <summary>The element's installation method, trimmed, or empty.</summary>
    public string Of(Element element)
    {
        if (_parameter.Length == 0 || element is null)
            return string.Empty;

        var type = element.GetTypeId();

        if (type == ElementId.InvalidElementId)
            return string.Empty;

        if (_types.TryGetValue(type.Value, out var known))
            return known;

        known = (TypeParameterText.Read(_document, _document.GetElement(type), _parameter, out _) ?? string.Empty).Trim();
        _types[type.Value] = known;

        return known;
    }
}

/// <summary>
/// A user-chosen type parameter as text, by the one rule both the catalogue filter and the
/// installation method read it with.
/// </summary>
/// <remarks>
/// <b>One rule, because two readers of one parameter disagreeing is the failure to avoid.</b> A
/// project that filters ducts by <c>Способ прокладки</c> and takes the method from the same
/// parameter would otherwise be told a duct counts and then find its length laid by no method.
/// The rule itself is written on <see cref="CarrierMarkingReader"/>.
/// </remarks>
internal static class TypeParameterText
{
    /// <summary>
    /// The value as stored, or null when the type has no such parameter, it holds nothing, or it holds
    /// a kind no comparison can read - which last sets <paramref name="incomparable"/>.
    /// </summary>
    public static string? Read(Document document, Element? type, string name, out bool incomparable)
    {
        incomparable = false;

        var parameter = type?.LookupParameter(name);

        if (parameter is not { HasValue: true })
            return null;

        switch (parameter.StorageType)
        {
            case StorageType.String:
                return parameter.AsString();

            case StorageType.Integer:
                return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);

            case StorageType.ElementId:
                return document.GetElement(parameter.AsElementId())?.Name;

            default:
                incomparable = true;
                return null;
        }
    }
}
