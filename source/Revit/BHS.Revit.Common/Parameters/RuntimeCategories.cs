using Autodesk.Revit.DB;

namespace BHS.Revit.Common.Parameters;

/// <summary>
/// Categories a parameter must cover that were not known when it was declared.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because one of our parameters lands on a family the user chooses.</b> The indicator
/// of a recommended junction box may be any family that meets a short list of requirements, so its
/// category is settled when somebody picks it and not a moment earlier. A scheme that can only
/// declare categories at compile time would have to guess - and a guessed list is a registry that
/// drifts from the code: the day a user picks a category nobody listed, the parameter does not
/// arrive and the failure reads as "the tool cannot see my box".
/// </para>
/// <para>
/// <b>A type rather than a dictionary in the signature</b>, so the thing has a name and this note
/// has somewhere to live. It adds to what a parameter declares and never replaces it: a runtime
/// category is one more place the parameter is wanted, not a different set of places.
/// </para>
/// </remarks>
public sealed class RuntimeCategories
{
    private readonly Dictionary<Guid, List<BuiltInCategory>> _extra = new();

    /// <summary>Says that this parameter is also wanted on these categories.</summary>
    /// <returns>The same instance, so several can be named in one expression.</returns>
    public RuntimeCategories Add(Guid parameter, params BuiltInCategory[] categories)
    {
        if (categories is null || categories.Length == 0)
            return this;

        if (!_extra.TryGetValue(parameter, out var list))
        {
            list = new List<BuiltInCategory>();
            _extra[parameter] = list;
        }

        foreach (var category in categories)
        {
            // Deduplicated here rather than at the point of use: the same category named twice is a
            // caller assembling a list from two places, which is ordinary, and a category set that
            // receives it twice is not.
            if (!list.Contains(category))
                list.Add(category);
        }

        return this;
    }

    internal IReadOnlyList<BuiltInCategory> For(Guid parameter) =>
        _extra.TryGetValue(parameter, out var list) ? list : Array.Empty<BuiltInCategory>();
}
