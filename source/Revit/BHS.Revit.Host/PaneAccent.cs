using System.Windows.Media;

namespace BHS.Revit.Host;

/// <summary>
/// The one place the dockable panes' colours are decided: the accent resources WPF-UI's controls look
/// up, and the pane's own background. Values from the Designer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why we write these ourselves.</b> In an ordinary WPF-UI application <c>ApplicationAccentColorManager</c>
/// writes the accent keys into <c>Application.Current.Resources</c>. Inside Revit that dictionary is
/// Revit's - read from the IL of 4.0.3 - so calling it would recolour somebody else's interface, and
/// not calling it leaves our controls on the defaults <c>ControlsDictionary</c> merges from
/// <c>Accent.xaml</c>: WPF-UI's blue. So the host writes the same keys into the pane's own root.
/// </para>
/// <para>
/// <b>Into the root's own <c>Resources</c>, not a merged dictionary</b>: a dictionary's own entries win
/// over its merged ones, and the theme and control dictionaries are merged. Re-written on every theme
/// change, because half the values differ between light and dark.
/// </para>
/// <para>
/// The first eighteen keys are exactly the ones <c>ApplicationAccentColorManager.UpdateColorResources</c>
/// writes, read by the Designer from its IL; two values deviate on purpose and say why. The last four
/// are the brushes <c>Accent.xaml</c> defines.
/// </para>
/// </remarks>
internal static class PaneAccent
{
    /// <summary>Our own key for the pane's background, matched to Revit's panels.</summary>
    /// <remarks>
    /// Not WPF-UI's <c>ApplicationBackgroundBrush</c>, which is <c>#202020</c> in dark and would sit in a
    /// Revit panel as a hole. The two values are the Designer's reading of Revit's panel colour and are
    /// <b>not measured</b>; the probe notes what it can sample.
    /// </remarks>
    public const string BackgroundKey = "BhsPaneBackgroundBrush";

    /// <summary>The logo teal, the same in both themes.</summary>
    public const uint Base = 0xFF109E92;

    public static uint Primary(bool dark) => dark ? 0xFF3DB8AC : 0xFF0E8C82;

    public static uint Secondary(bool dark) => dark ? 0xFF5CCFC3 : 0xFF0B7A71;

    public static uint Tertiary(bool dark) => dark ? 0xFF85E0D6 : 0xFF08665F;

    /// <summary>Every key the root's own dictionary gets for <paramref name="dark"/>.</summary>
    public static IReadOnlyDictionary<string, object> Resources(bool dark)
    {
        var sys = Base;
        var pri = Primary(dark);
        var sec = Secondary(dark);
        var ter = Tertiary(dark);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["TextOnAccentFillColorPrimary"] = C(dark ? 0xFF000000 : 0xFFFFFFFF),
            ["TextOnAccentFillColorSecondary"] = C(dark ? 0x80000000 : 0x80FFFFFF),
            ["TextOnAccentFillColorDisabled"] = C(dark ? 0x77000000 : 0x87FFFFFF),
            // WPF-UI writes transparent here in dark, which makes selected text invisible.
            ["TextOnAccentFillColorSelectedText"] = C(dark ? 0xFF000000 : 0xFFFFFFFF),
            ["AccentTextFillColorDisabled"] = C(dark ? 0x5D000000u : 0x5DFFFFFFu),
            ["SystemAccentColor"] = C(sys),
            ["SystemAccentColorPrimary"] = C(pri),
            ["SystemAccentColorSecondary"] = C(sec),
            ["SystemAccentColorTertiary"] = C(ter),
            ["SystemAccentBrush"] = B(sec),
            ["SystemFillColorAttentionBrush"] = B(sec),
            ["AccentTextFillColorPrimaryBrush"] = B(ter),
            ["AccentTextFillColorSecondaryBrush"] = B(ter),
            ["AccentTextFillColorTertiaryBrush"] = B(sec),
            // White on the base teal is 3.3:1 in light, so light takes the darker secondary.
            ["AccentFillColorSelectedTextBackgroundBrush"] = B(dark ? sys : sec),
            ["AccentFillColorDefaultBrush"] = B(sec),
            ["AccentFillColorSecondaryBrush"] = B(sec, 0.9),
            ["AccentFillColorTertiaryBrush"] = B(sec, 0.8),
            ["SystemAccentColorBrush"] = B(sys),
            ["SystemAccentColorPrimaryBrush"] = B(pri),
            ["SystemAccentColorSecondaryBrush"] = B(sec),
            ["SystemAccentColorTertiaryBrush"] = B(ter),
            [BackgroundKey] = B(dark ? 0xFF3B4453 : 0xFFF0F0F0),
        };
    }

    public static Color C(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>Frozen: built on the UI thread, but a frozen brush is also cheaper to hand around.</summary>
    private static SolidColorBrush B(uint argb, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(C(argb)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
