using BHS.Settings;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// Settings that hold exactly what a case puts in them.
/// </summary>
/// <remarks>
/// <b>Shared by the suites since 2026-09-18, and it was one suite's private class before.</b> The
/// check for stale lengths needs the same seam, and a second copy of fifteen lines is a second place
/// for "what a case puts in the settings" to mean something slightly different.
///
/// <b>Fifteen lines instead of loosening the production type, which is the better trade.</b>
/// <c>CablingProjectSettings</c> is constructed only through <c>Read(ISettings)</c>, and the
/// first thought was to add a constructor for tests - a change to shipped code so that a test
/// could reach it. <c>ISettings</c> turns out to be an indexer, a key list and a section view, so
/// the seam that already exists is enough, and nothing a customer receives changes shape to be
/// testable.
/// </remarks>
internal sealed class FixedSettings : ISettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key]
    {
        get => _values.TryGetValue(key, out var found) ? found : null;
        set => _values[key] = value ?? string.Empty;
    }

    public IEnumerable<string> Keys => _values.Keys;

    public ISettings Section(string name)
    {
        var prefix = name + ":";
        var section = new FixedSettings();

        foreach (var pair in _values)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                section[pair.Key.Substring(prefix.Length)] = pair.Value;
        }

        return section;
    }
}
