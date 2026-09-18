using System.Globalization;
using System.Resources;
using Autodesk.Revit.ApplicationServices;

namespace BHS.Revit.Host;

/// <summary>
/// The culture our own strings follow: Revit's interface language, never Windows'.
/// </summary>
/// <remarks>
/// <para>
/// Revit's language is chosen at installation and at launch and has nothing to do with the language
/// of Windows - a Russian Windows running an English Revit is an ordinary case - so
/// <c>CultureInfo.CurrentUICulture</c> is the wrong source. <c>ControlledApplication.Language</c> is
/// the right one; it lives in RevitAPI, so both forms of add-in have it, and it is the same member on
/// all four releases (checked against the metadata of 2024 and 2027).
/// </para>
/// <para>
/// <b>Two overlays, not three.</b> <c>English_USA</c> and <c>Unknown</c> land in one branch on purpose:
/// our neutral strings are American English, so "a language we have no overlay for" is one state, not
/// two. The dockable pane shell is the first consumer; baking the declaration strings - pane titles,
/// button texts - will be the second (PR 1b).
/// </para>
/// </remarks>
public static class RevitLanguage
{
    /// <summary>The overlay culture for Revit's language, or null for the neutral strings.</summary>
    public static CultureInfo? Culture(LanguageType language) => language switch
    {
        LanguageType.English_GB => CultureInfo.GetCultureInfo("en-GB"),
        LanguageType.Russian => CultureInfo.GetCultureInfo("ru-RU"),
        _ => null,
    };

    /// <summary>The culture of the Revit this process runs, set once by the host at startup.</summary>
    /// <remarks>Null until then, and for a language without an overlay: both mean the neutral strings.</remarks>
    public static CultureInfo? Current { get; internal set; }
}

/// <summary>The dockable pane shell's own sentences, in Revit's language.</summary>
/// <remarks>
/// A plain <see cref="ResourceManager"/> rather than Lepo.i18n: three sentences, asked by code, in a
/// culture named explicitly - the markup extension and its own culture state would buy nothing here.
/// The <c>ru-RU</c> sentences travel in a satellite assembly beside the host, the first satellite of
/// ours Revit is asked to load; whether it finds it inside Revit is not measured, and a miss falls back
/// to English silently - so the probe notes which sentence came back.
/// </remarks>
internal static class ShellStrings
{
    public const string NoDocument = "Pane.NoDocument";
    public const string WaitingForRevit = "Pane.WaitingForRevit";
    public const string Failed = "Pane.Failed";

    /// <summary>Named here, because the probe reads the same resources to see which culture comes back.</summary>
    public const string BaseName = "BHS.Revit.Host.Resources.Shell";

    private static readonly ResourceManager Resources = new(BaseName, typeof(ShellStrings).Assembly);

    public static string Get(string key)
    {
        try
        {
            return Resources.GetString(key, RevitLanguage.Current ?? CultureInfo.InvariantCulture) ?? key;
        }
        catch (Exception)
        {
            // A missing string must not become a missing pane: the key itself says what was meant.
            return key;
        }
    }
}
