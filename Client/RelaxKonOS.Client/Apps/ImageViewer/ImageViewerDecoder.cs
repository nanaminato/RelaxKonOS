using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg;

namespace RelaxKonOS.Client.Apps.ImageViewer;

internal static class ImageViewerDecoder
{
    public static IImage Load(string path, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        if (!string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
            return new Bitmap(stream);

        var source = SvgSource.Load(stream);
        var image = new SvgImage { Source = source };
        if (source.Picture is null || !double.IsFinite(image.Size.Width) || !double.IsFinite(image.Size.Height)
            || image.Size.Width <= 0 || image.Size.Height <= 0
            || image.Size.Width > int.MaxValue || image.Size.Height > int.MaxValue)
            throw new InvalidDataException("SVG has no drawable image or valid dimensions.");
        return image;
    }
}
