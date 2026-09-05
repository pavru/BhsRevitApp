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
/// vendor logo, at both sizes Revit asks for, and an edition that has drawn its own overrides it.
/// </para>
/// <para>
/// <b>Embedded, not deployed.</b> A file beside the assembly would need a path resolved at run time,
/// would have to survive publishing and deployment, and would outlive whatever produced it -
/// deployment here copies and never deletes, which has already left assemblies in add-in folders
/// that no build had produced for weeks. Something that must always be there is better inside the
/// assembly that needs it.
/// </para>
/// <para>
/// <b>Two sizes, not seven.</b> <c>ButtonData</c> has exactly <c>Image</c> and <c>LargeImage</c> -
/// checked against the metadata of 2024 and 2027, where both sit on the shared base, so every kind
/// of button has them. What Revit wants at 150% and 200% display scaling, and whether it will take
/// a vector at all, is not measured; when it is, this is one file to change and the manifest gains
/// icon fields. Guessing at extra sizes now would ship files nothing reads.
/// </para>
/// </remarks>
internal static class PlaceholderIcon
{
    private static readonly Lazy<ImageSource?> SmallImage = new(() => Load(16));
    private static readonly Lazy<ImageSource?> LargeImage_ = new(() => Load(32));

    /// <summary>16x16, for a small button. Null only if the resource could not be read.</summary>
    public static ImageSource? Small => SmallImage.Value;

    /// <summary>32x32, for a large one.</summary>
    public static ImageSource? Large => LargeImage_.Value;

    private static ImageSource? Load(int size)
    {
        var name = $"BHS.Revit.Host.Resources.PlaceholderIcon.{size}.png";

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
