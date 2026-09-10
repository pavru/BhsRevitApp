using Autodesk.Revit.DB;
using BHS.Settings;

namespace BHS.MEP.Cabling.Revit;

/// <summary>
/// The family the calculation uses to show where it recommends a junction box.
/// </summary>
/// <remarks>
/// <para>
/// <b>An indicator, not a box.</b> Nothing is cut into the carrier system: the calculation places a
/// marker at the point where the cable leaves the trunk, and whether a real box goes there is the
/// designer's decision. So any family will do, and which one is the user's to say.
/// </para>
/// <para>
/// <b>Named by family and type rather than by id - the owner's decision.</b> An <c>ElementId</c> is
/// exact inside one document and says nothing to a person reading the setting; a pair of names is
/// read at a glance and survives the model being copied. What it does not survive is a rename, and
/// that is why <see cref="In"/> reports rather than falls back: a marker quietly placed with the
/// wrong family is worse than one not placed at all, because nobody will connect it to having
/// renamed a type last week.
/// </para>
/// <para>
/// <b>Project-scoped, hence the <c>Model:</c> prefix.</b> Which family stands for a recommended box
/// is a property of the project, like "no additional boxes" beside it - not of the person, and not
/// of the kind of device. The chain is product file, machine file, the model, then the environment;
/// the user's own file has no say, which is what a project rule means.
/// </para>
/// </remarks>
public sealed class RecommendedBoxes
{
    /// <summary>The family the marker is an instance of.</summary>
    public const string FamilyKey = "Model:Cabling:RecommendedBox:Family";

    /// <summary>Its type within that family.</summary>
    public const string TypeKey = "Model:Cabling:RecommendedBox:Type";

    /// <summary>What we ship, and what the setting falls back to when a project says nothing.</summary>
    /// <remarks>
    /// In code as well as in the product settings file, because a default that exists only in a file
    /// is a default that disappears the day somebody deploys without it - and the symptom would be
    /// markers silently not placed.
    /// </remarks>
    public const string DefaultFamily = "BHS_CBL_Рекомендуемая распределительная коробка";

    /// <summary>The type of it the owner chose; the family carries more than one.</summary>
    public const string DefaultType = "Рекомендуемая коробка";

    public RecommendedBoxes(string family, string type)
    {
        Family = family ?? string.Empty;
        Type = type ?? string.Empty;
    }

    /// <summary>Reads the pair a project has chosen, or ours.</summary>
    public static RecommendedBoxes Read(ISettings settings) =>
        new(settings.Text(FamilyKey, DefaultFamily) ?? DefaultFamily,
            settings.Text(TypeKey, DefaultType) ?? DefaultType);

    public string Family { get; }

    public string Type { get; }

    /// <summary>
    /// The type this document holds under those names, or nothing when it holds none.
    /// </summary>
    /// <remarks>
    /// <b>Looked up in whichever document is being read, and that is why names were the right
    /// choice here.</b> A carrier survey walks the host and every link, each with its own element
    /// ids; a pair of names means the same thing in all of them, and an id would have meant the
    /// marker was recognisable in exactly one.
    /// </remarks>
    public FamilySymbol? In(Document document)
    {
        if (document is null || Family.Length == 0 || Type.Length == 0)
            return null;

        foreach (var symbol in new FilteredElementCollector(document)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>())
        {
            if (symbol.Family is { } family
                && string.Equals(family.Name, Family, StringComparison.Ordinal)
                && string.Equals(symbol.Name, Type, StringComparison.Ordinal))
            {
                return symbol;
            }
        }

        return null;
    }
}

/// <summary>
/// Which elements of one document are our markers rather than structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not supposed: a marker is collected as a carrier unless something stops it.</b> On
/// the owner's model the indicator family is a cable tray fitting - the natural choice, since a
/// junction box is a fitting - and <c>CarrierCatalogue</c> collects by category alone. Placing one
/// free-standing marker took the carrier count from 358 to 359: the network grows a node the project
/// does not have, put there by the run that placed it.
/// </para>
/// <para>
/// <b>Keyed on what we placed, never on the category.</b> The category is the user's to choose, so a
/// rule about categories would be a rule about a moving target. Resolving the configured type in
/// each document answers exactly "is this one of ours".
/// </para>
/// <para>
/// The other half of the answer, and the owner's own definition, is connectedness: a real junction
/// box is joined to the carrier network and a marker is joined to nothing. Measured on the same run
/// - the family has four connectors and a freshly placed instance has none of them connected. That
/// is not used to exclude here, deliberately: an unconnected tray is a modelling error worth seeing,
/// and a rule that dropped everything unconnected would hide the class of finding that first proved
/// this tool worth running.
/// </para>
/// </remarks>
public sealed class RecommendedBoxMarkers
{
    private readonly ElementId? _type;

    private RecommendedBoxMarkers(ElementId? type) => _type = type;

    /// <summary>Nothing is a marker - for a document that holds no such type, and as a default.</summary>
    public static RecommendedBoxMarkers None { get; } = new(null);

    /// <summary>Whether the configured type was found in this document at all.</summary>
    /// <remarks>
    /// Worth reporting rather than shrugging at. A project that renamed the type still holds the
    /// markers of the last run, and they are now indistinguishable from structure - so "not found"
    /// is the moment to say so, while somebody still remembers renaming it.
    /// </remarks>
    public bool Known => _type is not null;

    public static RecommendedBoxMarkers For(Document document, RecommendedBoxes boxes) =>
        boxes?.In(document) is { } symbol ? new RecommendedBoxMarkers(symbol.Id) : None;

    /// <summary>Whether this element is a marker we placed.</summary>
    public bool Marks(Element element) =>
        _type is not null && element is not null && element.GetTypeId() == _type;
}
