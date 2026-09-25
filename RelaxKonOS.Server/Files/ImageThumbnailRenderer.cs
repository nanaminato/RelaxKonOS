using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RelaxKonOS.Server.Files;

/// <summary>One rendered thumbnail: the encoded bytes and the media type that describes them.</summary>
public sealed record RenderedThumbnail(byte[] Bytes, string ContentType);

/// <summary>
/// Renders the small copy of a picture that a client shows while the original is still on its way.
///
/// It exists because of a round trip rather than a rectangle: sending a forty-megapixel photograph to
/// a phone so that the phone can draw it in a 220 dp card means the user waits for the whole file
/// before seeing anything. The server already holds the file, so it can answer with a few hundred
/// kilobytes almost immediately and let the original follow.
///
/// Nothing here is a gate. A file that is not an image, uses a codec this build does not carry, or
/// declares more pixels than the host should decode for a thumbnail answers <c>null</c>, and the
/// caller reports "no thumbnail" — which is exactly what every client did before this existed.
/// </summary>
public static class ImageThumbnailRenderer
{
    /// <summary>The longest edge a caller that names no size gets.</summary>
    public const int DefaultMaxEdge = 256;

    /// <summary>The smallest size worth rendering; below it a thumbnail stops being a picture.</summary>
    public const int MinimumMaxEdge = 16;

    /// <summary>
    /// The largest size a caller may ask for.
    ///
    /// A thumbnail is a preview, and a preview larger than any screen it will be drawn on is just a
    /// more expensive download of the original. The bound also keeps one request's worst case — and
    /// so the endpoint's cost — a known quantity.
    /// </summary>
    public const int MaximumMaxEdge = 1024;

    /// <summary>
    /// The pixel count above which nothing is rendered.
    ///
    /// The guard against a decompression bomb is above this one — it refuses on the size the header
    /// declares, before a single pixel is allocated. This is the second half: a host that is not
    /// asked to hold hundreds of megabytes for a picture nobody asked to keep. Sixty-four million
    /// pixels is more than any phone camera produces and about the largest picture that is still a
    /// picture, so what it excludes is the pathological case rather than the surprising one.
    /// </summary>
    private const long MaximumSourcePixels = 64L * 1024 * 1024;

    /// <summary>
    /// Quality of the JPEG a thumbnail is encoded as. High enough that a photograph looks like itself
    /// at this size, low enough that the answer stays a fraction of the original.
    /// </summary>
    private const int JpegQuality = 85;

    /// <summary>
    /// The pipeline every thumbnail is decoded and resized with.
    ///
    /// Its own instance rather than <see cref="Configuration.Default"/>, because ImageSharp scales
    /// itself to every core by default and a server that renders previews between streaming files and
    /// sampling performance should not let four concurrent thumbnails saturate the host. The cap is on
    /// concurrency, not on quality: one thumbnail is still rendered as fast as the machine can.
    ///
    /// Built by cloning <see cref="Configuration.Default"/> and not by `new Configuration()`: a bare
    /// instance carries no decoders at all, and every file would come back as "not an image".
    /// </summary>
    private static readonly Configuration Pipeline = CreatePipeline();

    private static Configuration CreatePipeline()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount);
        return configuration;
    }

    /// <summary>
    /// Renders the picture in <paramref name="source"/> with its longest edge at most
    /// <paramref name="maxEdge"/> pixels.
    /// </summary>
    /// <param name="source">
    /// The file to read. It must be seekable: the header is read first so that an oversized image can
    /// be refused before its pixels are allocated, and reading it twice is what that costs.
    /// </param>
    /// <returns>The encoded thumbnail, or <c>null</c> when there is none to give.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEdge"/> is outside the supported range.</exception>
    public static RenderedThumbnail? Render(Stream source, int maxEdge)
    {
        if (maxEdge is < MinimumMaxEdge or > MaximumMaxEdge)
            throw new ArgumentOutOfRangeException(nameof(maxEdge), maxEdge,
                $"A thumbnail edge must be between {MinimumMaxEdge} and {MaximumMaxEdge} pixels.");
        if (!source.CanSeek)
            throw new ArgumentException("The source must be seekable, because its header is read before its pixels.", nameof(source));

        // One frame, and never more. An animated GIF whose eight hundred frames are each four
        // megapixels costs eight hundred times the memory its header declares, and a thumbnail is a
        // single still picture whatever the original is. It is also why the frame count cannot come
        // from the header: the honest bound is the one the decoder is held to.
        var options = new DecoderOptions { Configuration = Pipeline, MaxFrames = 1 };
        try
        {
            var info = Image.Identify(options, source);
            if (info is null || info.Width <= 0 || info.Height <= 0)
                return null;
            // A 100000 × 100000 "image" is a few hundred bytes on disk and forty gigabytes decoded.
            // Refusing on the declared size is the only place a bomb can be stopped for free.
            if ((long)info.Width * info.Height > MaximumSourcePixels)
                return null;
            source.Position = 0;

            using var image = Image.Load(options, source);
            // Orientation first, size second. A portrait photograph is stored landscape with a tag
            // saying to turn it, so resizing before turning would resample along the wrong axis. A
            // client drawing the original upright would then be shown a thumbnail lying on its side.
            image.Mutate(context => context.AutoOrient());
            if (image.Width > maxEdge || image.Height > maxEdge)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    // The longest edge becomes maxEdge and the aspect ratio is untouched, so the
                    // answer has the shape of the original and fits the box it was asked for.
                    Size = new Size(maxEdge, maxEdge),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            using var encoded = new MemoryStream();
            if (UsesTransparency(image))
            {
                image.SaveAsPng(encoded);
                return new RenderedThumbnail(encoded.ToArray(), "image/png");
            }

            image.SaveAsJpeg(encoded, new JpegEncoder { Quality = JpegQuality });
            return new RenderedThumbnail(encoded.ToArray(), "image/jpeg");
        }
        // The header identified a format whose content is unusable: truncated, corrupt, or a valid
        // container holding something no decoder here understands. All of them mean "no thumbnail";
        // only IO failures are allowed out, because those describe the host rather than the picture.
        catch (ImageFormatException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the picture uses an alpha channel at all, which is the whole question the encoder choice
    /// answers.
    ///
    /// JPEG has no alpha channel: send it a translucent logo and the transparent parts come back as a
    /// black card, so anything that uses one has to leave as PNG. The pixels are what is asked, rather
    /// than the file extension or the container format, because neither answers the question — a PNG
    /// written without an alpha channel has nothing to lose in a JPEG, and a four-channel WebP that
    /// happens to be entirely opaque has nothing to keep either. The scan runs over the thumbnail,
    /// which is at most a million pixels, and one picture's worth of that is nothing next to the
    /// decode that produced it.
    /// </summary>
    private static bool UsesTransparency(Image image)
    {
        // The scan needs one pixel format to look at, and four-channel pixels are the one every
        // other format can be widened into without losing anything. A format that cannot carry alpha
        // is promoted to "fully opaque" by the same conversion, which is the answer it deserves.
        using var rgba = image.CloneAs<Rgba32>();
        var transparent = false;
        rgba.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !transparent; y++)
            {
                foreach (var pixel in accessor.GetRowSpan(y))
                {
                    if (pixel.A == byte.MaxValue)
                    {
                        continue;
                    }
                    transparent = true;
                    return;
                }
            }
        });
        return transparent;
    }
}
