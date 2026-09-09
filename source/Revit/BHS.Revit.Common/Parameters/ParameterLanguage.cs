using Autodesk.Revit.ApplicationServices;

namespace BHS.Revit.Common.Parameters;

/// <summary>
/// The languages we publish a shared parameter file in.
/// </summary>
/// <remarks>
/// <para>
/// One value per file, and the list is deliberately shorter than <see cref="LanguageType"/>: a
/// language we have no names for gets <see cref="English"/> rather than an entry of its own. An
/// empty file reads as a localisation abandoned half way and attracts filling with copies of the
/// English, which is worse than the English plainly shown - the same reasoning that keeps
/// <c>features.en-US.json</c> from existing.
/// </para>
/// </remarks>
public enum ParameterLanguage
{
    /// <summary>The neutral one. Every parameter has a name here, and it is the fallback.</summary>
    English,

    /// <summary>Russian.</summary>
    Russian,
}

/// <summary>Which file a Revit answers to.</summary>
public static class ParameterLanguages
{
    /// <summary>Every language a file is written for.</summary>
    public static readonly IReadOnlyList<ParameterLanguage> All =
        new[] { ParameterLanguage.English, ParameterLanguage.Russian };

    /// <summary>
    /// The file this Revit should be pointed at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The source is Revit, not Windows.</b> Revit's interface language is chosen at install and
    /// launch and is unrelated to the system's - a Russian Windows running an English Revit is an
    /// ordinary case, so <c>CurrentUICulture</c> would answer a different question. Same source as
    /// the ribbon's declaration strings, and for the same reason.
    /// </para>
    /// <para>
    /// Everything we have no names for - including <c>Unknown</c>, which Revit really does return -
    /// falls to <see cref="ParameterLanguage.English"/>. One state, not two: "a language we do not
    /// publish for" needs no further division.
    /// </para>
    /// </remarks>
    public static ParameterLanguage For(LanguageType language) => language switch
    {
        LanguageType.Russian => ParameterLanguage.Russian,
        _ => ParameterLanguage.English,
    };

    /// <summary>The suffix that tells the two files apart on disk.</summary>
    public static string Tag(ParameterLanguage language) => language switch
    {
        ParameterLanguage.Russian => "ru",
        _ => "en",
    };
}
