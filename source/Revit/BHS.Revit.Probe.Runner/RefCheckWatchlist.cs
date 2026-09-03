using System.Text.Json;

namespace BHS.Revit.Probe.Runner;

/// <summary>
/// The assemblies RefCheck compares surfaces for, read from its own list.
/// </summary>
/// <remarks>
/// Revit substituting its copy of a library for ours is expected, not a defect: whoever loads
/// first wins and Revit always loads first. What matters is whether that substitution has been
/// vetted, and the answer to that lives in RefCheck's watchlist - a name on it has a stored member
/// surface that the build compares against on every publish.
/// <para>
/// So the two halves divide the class between them. RefCheck says at build time that Revit's copy
/// carries every member we use; the probe says at run time which copy actually got loaded. A
/// substitution of something on the list is a fact worth printing. A substitution of something not
/// on it is unverified, and that is the failure.
/// </para>
/// </remarks>
internal static class RefCheckWatchlist
{
    private const string RelativePath = @"build\RefCheck\baselines\watchlist.json";

    /// <summary>Names RefCheck vets, or null when the list cannot be read from here.</summary>
    public static HashSet<string>? TryLoad(string? repositoryRoot)
    {
        if (repositoryRoot is null)
            return null;

        var path = Path.Combine(repositoryRoot, RelativePath);
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("assemblies", out var assemblies))
                return null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in assemblies.EnumerateArray())
            {
                if (name.GetString() is { } text)
                    names.Add(text);
            }

            return names;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
