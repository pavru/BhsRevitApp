using System.Globalization;
using System.Resources;

namespace BHS.Revit.Probe.Pane.Support;

/// <summary>
/// What the probe pane's content asks of an assembly beside it, and of that assembly's satellite.
/// </summary>
/// <remarks>
/// Both are called from <c>ProbePane.Create</c>, where the cabling pane failed: the dependency of a
/// pane's content is bound while the content's own code runs, not when the host loads it, so this is the
/// place the question is really asked.
/// </remarks>
public static class PaneSupport
{
    /// <summary>What <see cref="Say"/> returns, so that the probe can compare without loading this.</summary>
    public const string Answer = "the assembly beside the pane answered";

    public static string Say() => Answer;

    /// <summary>The same sentence out of the satellite, asked for a culture rather than for Revit's.</summary>
    /// <remarks>
    /// Asked for <c>ru-RU</c> whatever language Revit runs in: the question is whether the satellite is
    /// found beside its parent at all, and a Revit-language answer would ask it only on a Russian Revit.
    /// </remarks>
    public static string Localised() =>
        new ResourceManager("BHS.Revit.Probe.Pane.Support.Strings.Support", typeof(PaneSupport).Assembly)
            .GetString("Answer", new CultureInfo("ru-RU")) ?? "(null)";

    /// <summary>Where this assembly was loaded from, which only somebody holding it can say.</summary>
    public static string From() => typeof(PaneSupport).Assembly.Location;
}
