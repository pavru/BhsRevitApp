using Autodesk.Revit.UI;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BHS.Revit.Host;

/// <summary>
/// The icon a ribbon button wears until somebody draws it one.
/// </summary>
/// <remarks>
/// <para>
/// A button with no image is not a neutral button. Revit gives it an empty frame, and a panel of
/// those reads as broken rather than unfinished - so the framework supplies the house mark from the
/// vendor logo, at both sizes and in both themes, and an edition that has drawn its own overrides it.
/// </para>
/// <para>
/// Public because an edition that builds something by hand should be able to reach the same mark the
/// generated ribbon uses, rather than a button of its own looking unlike the rest.
/// </para>
/// <para>
/// <b>Embedded, not deployed.</b> A file beside the assembly would need a path resolved at run time,
/// would have to survive publishing and deployment, and would outlive whatever produced it -
/// deployment here copies and never deletes, which has already left assemblies in add-in folders
/// that no build had produced for weeks.
/// </para>
/// <para>
/// <b>Two slots, two themes, and twice the pixels at twice the declared dpi.</b> <c>ButtonData</c>
/// has exactly <c>Image</c> and <c>LargeImage</c>, so there is no set of variants to hand Revit -
/// which is what Autodesk's own icon guidelines assume, where the product picks one of five dpi
/// files itself. Through the add-in API the choice has to be made in the file: Revit's ribbon draws
/// with <c>Stretch="None"</c>, so the drawn size is the source's natural size, <c>pixels x 96/dpi</c>.
/// A 64-pixel image declaring 192 dpi is therefore 32 units wide - the same button - while carrying
/// twice the pixels for a display that has them.
/// </para>
/// </remarks>
public static class PlaceholderIcon
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    /// <summary>The small slot: 32 pixels at 192 dpi, which is 16 units wide.</summary>
    public static ImageSource? Small => Load(32, Dark);

    /// <summary>The large slot: 64 pixels at 192 dpi, which is 32 units wide.</summary>
    public static ImageSource? Large => Load(64, Dark);

    /// <summary>
    /// Whether Revit is currently showing its dark theme.
    /// </summary>
    /// <remarks>
    /// Through <c>UIThemeManager.CurrentTheme</c>, which is public Revit API and present on all four
    /// supported releases - checked against the metadata rather than remembered. The alternative,
    /// <c>ComponentManager.CurrentTheme</c>, lives in <c>Autodesk.Internal.Windows</c> and is not
    /// ours to depend on.
    /// </remarks>
    public static bool Dark
    {
        get
        {
            try
            {
                return UIThemeManager.CurrentTheme == UITheme.Dark;
            }
            catch (Exception)
            {
                // Asked too early, or asked of a Revit that has no opinion. Light is the older
                // default and the safer guess: a dark mark on a light ribbon is legible, and the
                // reverse is not.
                return false;
            }
        }
    }

    private static ImageSource? Load(int pixels, bool dark)
    {
        var name = $"BHS.Revit.Host.Resources.PlaceholderIcon.{(dark ? "dark." : string.Empty)}{pixels}.png";

        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
                return cached;

            var image = Read(name);
            Cache[name] = image;
            return image;
        }
    }

    private static ImageSource? Read(string name)
    {
        try
        {
            using var stream = typeof(PlaceholderIcon).Assembly.GetManifestResourceStream(name);

            if (stream is null)
                return null;

            var image = new BitmapImage();

            image.BeginInit();

            // The stream is closed the moment this method returns, so the bytes have to be taken
            // now rather than read lazily on first draw.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();

            // Frozen because the ribbon is built on the API thread and drawn on the UI one. An
            // unfrozen ImageSource belongs to the thread that made it, and this repository has
            // already paid once for asking WPF a question from the wrong thread.
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            // A missing icon is a plain button. It is never worth a failed startup.
            return null;
        }
    }
}
