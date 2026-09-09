using Autodesk.Revit.DB;

namespace BHS.Revit.Common.Parameters;

/// <summary>What one parameter is called, and what it says about itself, in one language.</summary>
/// <remarks>
/// The description travels with the name because they are read in the same place - the family
/// editor's parameter dialog - and a Russian name explained in English is a half-finished job that
/// looks finished.
/// </remarks>
public sealed class ParameterText
{
    public ParameterText(string name, string description = "")
    {
        Name = name;
        Description = description;
    }

    /// <summary>
    /// What it is called in the properties palette and in a schedule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaped <c>Vendor_Feature_Meaning</c> - the owner's template, and each third has its own rule:
    /// <b>vendor</b> is a mark and stays Latin in every file, <b>feature</b> is an English
    /// abbreviation and is identical in every file, <b>meaning</b> is in that file's language.
    /// </para>
    /// <para>
    /// The vendor third stops two vendors' parameters from reading alike in one schedule, which the
    /// GUID does not help with at all: the GUID makes them different parameters and does nothing
    /// about them being called the same thing. The feature third does that job one level down,
    /// between our own features - and holding it steady across languages is what leaves the two names
    /// of one parameter differing in exactly one place, so a reader seeing both recognises a pair
    /// rather than two parameters that happen to be near each other.
    /// </para>
    /// </remarks>
    public string Name { get; }

    public string Description { get; }
}

/// <summary>
/// One shared parameter, declared once and used for three different things.
/// </summary>
/// <remarks>
/// <para>
/// The same declaration writes the shared parameter files, checks whether a document already has the
/// parameter bound, and binds it. One source, so the file a family author points Revit at and the
/// binding an add-in makes cannot describe different parameters - which they would within a month if
/// the file lived in git beside the code that binds it.
/// </para>
/// <para>
/// <b>The GUID is forever, and it is the only thing that is.</b> It is what makes a parameter the
/// same parameter across models, across families, across languages and across versions of us;
/// changing it produces a second parameter that looks identical in the properties palette and shares
/// nothing with the first. Same discipline as the Extensible Storage schema id and the dockable pane
/// id, and for the same reason: an identifier that has left the building does not come back.
/// </para>
/// <para>
/// <b>The name is not.</b> One parameter carries a different name in each published file, and which
/// one a model ends up showing depends on which Revit bound it first - so nothing in our code may
/// ever look a parameter up by name. <c>SharedParameterElement.Lookup(document, guid)</c> exists on
/// all four versions and is the only correct way to find one.
/// </para>
/// </remarks>
public sealed class SharedParameter
{
    private readonly IReadOnlyDictionary<ParameterLanguage, ParameterText> _text;

    /// <param name="english">
    /// Required, and the fallback for every language that has no text of its own.
    /// </param>
    /// <param name="russian">Optional; when absent this parameter reads English in a Russian Revit.</param>
    public SharedParameter(
        Guid id,
        ForgeTypeId spec,
        ForgeTypeId group,
        bool instance,
        IReadOnlyList<BuiltInCategory> categories,
        ParameterText english,
        ParameterText? russian = null)
    {
        Id = id;
        Spec = spec;
        Group = group;
        Instance = instance;
        Categories = categories;

        var text = new Dictionary<ParameterLanguage, ParameterText>
        {
            [ParameterLanguage.English] = english,
        };

        if (russian is not null)
            text[ParameterLanguage.Russian] = russian;

        _text = text;
    }

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

    /// <summary>What this parameter is called in one language.</summary>
    public ParameterText In(ParameterLanguage language) =>
        _text.TryGetValue(language, out var text) ? text : _text[ParameterLanguage.English];

    /// <summary>Every name this parameter answers to, for a message that has to name it.</summary>
    /// <remarks>
    /// Distinct, because a parameter with no translation carries one name in both files, and saying
    /// it twice would read as two parameters.
    /// </remarks>
    public IReadOnlyList<string> Names() => ParameterLanguages.All
        .Select(language => In(language).Name)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
