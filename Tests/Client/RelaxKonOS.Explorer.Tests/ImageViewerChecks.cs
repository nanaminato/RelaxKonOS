using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Svg;
using RelaxKonOS.Client.Apps.ImageViewer;

internal static class ImageViewerChecks
{
    public static void Run(Action<bool, string> check)
    {
        var svg = Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 60">
              <rect width="120" height="60" fill="red"/>
            </svg>
            """);
        var image = ImageViewerDecoder.Load("/remote/logo.SVG", svg);
        check(image is SvgImage && image.Size == new Size(120, 60),
            "SVG uses a vector image and derives dimensions from viewBox, including uppercase extensions");
        using (var target = new RenderTargetBitmap(new PixelSize(240, 120)))
        using (var drawing = target.CreateDrawingContext())
            image.Draw(drawing, new Rect(image.Size), new Rect(0, 0, 240, 120));
        check(true, "SVG draws at enlarged dimensions after its input stream is closed");

        var rejected = false;
        try
        {
            ImageViewerDecoder.Load("broken.svg", Encoding.UTF8.GetBytes("not an SVG"));
        }
        catch (Exception)
        {
            rejected = true;
        }
        check(rejected, "Invalid SVG reports a decoding failure");
    }
}
