using Autodesk.Revit.DB;

namespace BHS.Revit.Common.Parameters;

/// <summary>
/// One shared parameter, declared once and used for three different things.
/// </summary>
/// <remarks>
/// <para>
/// The same declaration writes the shared parameter file, checks whether a document already has the
/// parameter bound, and binds it. One source, so the file a family author points Revit at and the
/// binding an add-in makes cannot describe different parameters - which they would within a month if
/// the file lived in git beside the code that binds it.
/// </para>
/// <para>
/// <b>The GUID is forever.</b> It is what makes a parameter the same parameter across models, across
/// families, and across versions of us; changing it produces a second parameter that looks identical
/// in the properties palette and shares nothing with the first. Same discipline as the Extensible
/// Storage schema id and the dockable pane id, and for the same reason: an identifier that has left
/// the building does not come back.
/// </para>
/// </remarks>
public sealed class SharedParameter
{
    public SharedParameter(
        string name,
        Guid id,
        ForgeTypeId spec,
        ForgeTypeId group,
        bool instance,
        IReadOnlyList<BuiltInCategory> categories,
        string description = "")
    {
        Name = name;
        Id = id;
        Spec = spec;
        Group = group;
        Instance = instance;
        Categories = categories;
        Description = description;
    }

    /// <summary>
    /// What it is called in the properties palette and in a schedule.
    /// </summary>
    /// <remarks>
    /// Prefixed <c>BHS_</c> for the same reason every assembly is: the GUID stops two vendors'
    /// parameters from being the same parameter, and does nothing at all about two of them being
    /// called the same thing in one schedule.
    /// </remarks>
    public string Name { get; }

    /// <summary>Fixed at birth, never changed. See the remarks on the type.</summary>
    public Guid Id { get; }

    /// <summary>What kind of value it holds - <c>SpecTypeId.String.Text</c> and its neighbours.</summary>
    public ForgeTypeId Spec { get; }

    /// <summary>Which group of the properties palette it appears under.</summary>
    public ForgeTypeId Group { get; }

    /// <summary>
    /// Whether it belongs to each element or to its type.
    /// </summary>
    /// <remarks>
    /// Not a detail: a role like "this is a junction box" is a property of the type, because every
    /// instance of that type is one. An instance parameter there would let one instance of a type be
    /// a box and another not, which is a state nobody wants and everybody can reach.
    /// </remarks>
    public bool Instance { get; }

    public IReadOnlyList<BuiltInCategory> Categories { get; }

    public string Description { get; }
}
