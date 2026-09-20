using System.Globalization;
using System.Resources;

namespace BHS.MEP.Cabling.Ui;

/// <summary>The inspector pane's content strings, in the language Revit speaks.</summary>
/// <remarks>
/// <para>
/// <b>Content strings, not declaration strings.</b> The pane's title and its button are declared in the Entry
/// project and baked into the manifest before anything of the feature loads; these are read only once the pane
/// has been shown and its content built, so an ordinary satellite assembly carries them - <c>ru-RU</c> beside
/// this assembly, as <c>BHS.Revit.Host</c> carries the shell's.
/// </para>
/// <para>
/// <b>The culture is the host's, handed over by the pane context</b> - one mapping from Revit's language to a
/// culture in the whole process, so the shell's sentence and the content's cannot come out in two languages.
/// A missing string reads as its key rather than as a pane that stopped.
/// </para>
/// </remarks>
public static class InspectorStrings
{
    public const string NothingSelected = "Inspector.NothingSelected";
    public const string SeveralSelected = "Inspector.SeveralSelected";
    public const string NotOurs = "Inspector.NotOurs";
    public const string Link = "Inspector.Link";
    public const string Gone = "Inspector.Gone";
    public const string Family = "Inspector.Family";
    public const string Failed = "Inspector.Failed";

    public const string KindCircuit = "Kind.Circuit";
    public const string KindIndicator = "Kind.Indicator";
    public const string KindJunctionBox = "Kind.JunctionBox";

    public const string SectionResult = "Section.Result";
    public const string SectionInput = "Section.Input";
    public const string SectionInputNote = "Section.InputNote";
    public const string SectionNotBoundNote = "Section.NotBoundNote";

    public const string CableLength = "Label.CableLength";
    public const string InTray = "Label.InTray";
    public const string InConduit = "Label.InConduit";
    public const string Free = "Label.Free";
    public const string Other = "Label.Other";
    public const string Slack = "Label.Slack";
    public const string RouteConnection = "Label.RouteConnection";
    public const string RouteStamp = "Label.RouteStamp";
    public const string CircuitConnection = "Label.CircuitConnection";
    public const string CircuitRefs = "Label.CircuitRefs";
    public const string Recommendation = "Label.Recommendation";
    public const string TapCount = "Label.TapCount";

    public const string NeverWritten = "Value.NeverWritten";
    public const string NotSet = "Value.NotSet";
    public const string NotBound = "Value.NotBound";
    public const string Terminal = "Value.Terminal";
    public const string JunctionBox = "Value.JunctionBox";
    public const string RecommendsBox = "Value.RecommendsBox";

    private static readonly ResourceManager Resources =
        new("BHS.MEP.Cabling.Ui.Strings.Inspector", typeof(InspectorStrings).Assembly);

    /// <summary>The culture strings are read in; null for the neutral English. Set by the pane's content.</summary>
    public static CultureInfo? Culture { get; set; }

    public static string Get(string key)
    {
        try
        {
            return Resources.GetString(key, Culture ?? CultureInfo.InvariantCulture) ?? key;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, object argument) =>
        string.Format(Culture ?? CultureInfo.InvariantCulture, Get(key), argument);
}
