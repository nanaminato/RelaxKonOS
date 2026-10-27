using SkiaSharp;

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
///
/// The decoder is Skia (through SkiaSharp) rather than a managed image library: it is the same decoder
/// the desktop client already renders with, it is CPU-only and needs neither a GPU context nor a
/// display server, and decoding frame zero of an animation is what it does by default rather than an
/// option that has to be remembered.
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
    /// The guard runs on the size the header declares, before a single pixel is allocated: a
    /// 100000 × 100000 "image" is a few hundred bytes on disk and forty gigabytes decoded, so refusing
    /// on the declared size is the only place a bomb can be stopped for free. Sixty-four million pixels
    /// is more than any phone camera produces and about the largest picture that is still a picture, so
    /// what it excludes is the pathological case rather than the surprising one.
    /// </summary>
    private const long MaximumSourcePixels = 64L * 1024 * 1024;

    /// <summary>
    /// Quality of the JPEG a thumbnail is encoded as. High enough that a photograph looks like itself
    /// at this size, low enough that the answer stays a fraction of the original.
    /// </summary>
    private const int JpegQuality = 85;

    /// <summary>
    /// The pixel layout every thumbnail is decoded into.
    ///
    /// Decoding into a format this class names — instead of whatever the file happens to carry — is
    /// what keeps the rest of the file simple: the orientation pass, the alpha scan and the encoder
    /// choice all read four bytes per pixel and never have to ask whether the source was a palette, a
    /// grey ramp or a sixteen-bit CMYK scan. Straight (un-premultiplied) alpha because the scan asks
    /// whether a channel is used at all, not how much light it contributes.
    /// </summary>
    private const SKColorType DecodeColorType = SKColorType.Bgra8888;

    private const SKAlphaType DecodeAlphaType = SKAlphaType.Unpremul;

    /// <summary>
    /// The resampler every shrink uses.
    ///
    /// Mitchell is the cubic filter Skia offers for reducing a picture: it trades a touch of sharpness
    /// against the ringing a Catmull-Rom kernel leaves on high-contrast edges, which is the right trade
    /// at thumbnail size. Skia carries no Lanczos kernel, and the extra sharpness would not survive the
    /// JPEG quantisation that follows anyway.
    /// </summary>
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// How many pictures may be decoded at once.
    ///
    /// A server that renders previews between streaming files and sampling performance should not let a
    /// burst of thumbnail requests saturate the host: one full-resolution decode is tens to hundreds of
    /// megabytes of native memory, and Skia parallelises nothing on its own, so the only bound there is
    /// the one this gate imposes. It caps concurrency, never quality — a single thumbnail is still
    /// rendered as fast as the machine can.
    /// </summary>
    private static readonly SemaphoreSlim DecodeGate = new(Math.Min(4, Environment.ProcessorCount));

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

        // The wait is outside the decode so that at most one codec per permit is alive at a time. It is
        // not cancellable: it is bounded by the work already in flight, not by a queue.
        DecodeGate.Wait();
        try
        {
            return RenderCore(source, maxEdge);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static RenderedThumbnail? RenderCore(Stream source, int maxEdge)
    {
        try
        {
            source.Position = 0;
            // The codec reads the header and nothing else; this is where the bomb guard gets its number
            // for free. A file this build cannot decode — text, an archive, an unknown container — has
            // no codec at all, which is the "no thumbnail" answer rather than a failure.
            using var codec = SKCodec.Create(source);
            if (codec is null) return null;
            var header = codec.Info;
            if (header.Width <= 0 || header.Height <= 0) return null;
            if ((long)header.Width * header.Height > MaximumSourcePixels) return null;

            // One frame, and never more. `GetPixels` decodes the frame its options name, and the default
            // options name frame zero: an animated GIF whose eight hundred frames are each four
            // megapixels costs one frame here, and a thumbnail is a single still picture whatever the
            // original is.
            var format = new SKImageInfo(header.Width, header.Height, DecodeColorType, DecodeAlphaType);
            using var decoded = new SKBitmap(format);
            if (codec.GetPixels(format, decoded.GetPixels()) != SKCodecResult.Success) return null;

            // Orientation first, size second. A portrait photograph is stored landscape with a tag
            // saying to turn it, so resizing before turning would resample along the wrong axis, and a
            // client drawing the original upright would then be shown a thumbnail lying on its side.
            var upright = Orient(decoded, codec.EncodedOrigin);
            try
            {
                var thumbnail = Shrink(upright, maxEdge);
                try
                {
                    var transparent = UsesTransparency(thumbnail);
                    using var image = SKImage.FromBitmap(thumbnail);
                    using var encoded = image.Encode(
                        transparent ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
                        transparent ? 100 : JpegQuality);
                    return encoded is null
                        ? null
                        : new RenderedThumbnail(encoded.ToArray(), transparent ? "image/png" : "image/jpeg");
                }
                finally
                {
                    if (!ReferenceEquals(thumbnail, upright)) thumbnail.Dispose();
                }
            }
            finally
            {
                if (!ReferenceEquals(upright, decoded)) upright.Dispose();
            }
        }
        // Only IO failures are allowed out, because those describe the host rather than the picture.
        // Everything else — a truncated scan, a container whose pixels no decoder here understands, a
        // codec that gives up halfway — means "no thumbnail", which is the caller's normal answer.
        catch (IOException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="source"/> turned the way its EXIF orientation says, or the same instance
    /// when the file is already upright.
    ///
    /// The transform is a pixel copy rather than a canvas draw: ninety-degree rotations have nothing to
    /// resample, and writing them out as index arithmetic keeps the result exact instead of depending on
    /// a filter's idea of where half a pixel is.
    /// </summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return source;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swapsAxes ? source.Height : source.Width;
        var height = swapsAxes ? source.Width : source.Height;
        var target = new SKBitmap(new SKImageInfo(width, height, DecodeColorType, DecodeAlphaType));
        var from = source.GetPixelSpan();
        var to = target.GetPixelSpan();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            int sx, sy;
            switch (origin)
            {
                case SKEncodedOrigin.TopRight: (sx, sy) = (source.Width - 1 - x, y); break;
                case SKEncodedOrigin.BottomRight: (sx, sy) = (source.Width - 1 - x, source.Height - 1 - y); break;
                case SKEncodedOrigin.BottomLeft: (sx, sy) = (x, source.Height - 1 - y); break;
                case SKEncodedOrigin.LeftTop: (sx, sy) = (y, x); break;
                case SKEncodedOrigin.RightTop: (sx, sy) = (y, source.Height - 1 - x); break;
                case SKEncodedOrigin.RightBottom: (sx, sy) = (source.Width - 1 - y, source.Height - 1 - x); break;
                case SKEncodedOrigin.LeftBottom: (sx, sy) = (source.Width - 1 - y, x); break;
                default: (sx, sy) = (x, y); break;
            }
            var sourceIndex = (sy * source.Width + sx) * 4;
            var targetIndex = (y * width + x) * 4;
            to[targetIndex + 0] = from[sourceIndex + 0];
            to[targetIndex + 1] = from[sourceIndex + 1];
            to[targetIndex + 2] = from[sourceIndex + 2];
            to[targetIndex + 3] = from[sourceIndex + 3];
        }
        return target;
    }

    /// <summary>
    /// Returns <paramref name="source"/> fit inside a <paramref name="maxEdge"/> box, or the same
    /// instance when it already fits.
    ///
    /// The longest edge becomes <paramref name="maxEdge"/> and the aspect ratio is untouched, so the
    /// answer has the shape of the original and fits the box it was asked for. A picture smaller than
    /// the box is returned at its own size: enlarging it would spend bytes to show the same detail.
    /// </summary>
    private static SKBitmap Shrink(SKBitmap source, int maxEdge)
    {
        if (source.Width <= maxEdge && source.Height <= maxEdge) return source;
        var scale = Math.Min((float)maxEdge / source.Width, (float)maxEdge / source.Height);
        // Rounding can reach zero on an extremely thin panorama; one pixel is the smallest picture that
        // can still be drawn, and Skia refuses a zero dimension.
        var width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height, DecodeColorType, DecodeAlphaType), Sampling)
            ?? source;
    }

    /// <summary>
    /// Whether the picture uses an alpha channel at all, which is the whole question the encoder choice
    /// answers.
    ///
    /// JPEG has no alpha channel: send it a translucent logo and the transparent parts come back as a
    /// black card, so anything that uses one has to leave as PNG. The pixels are what is asked, rather
    /// than the file extension or the container format, because neither answers the question — a PNG
    /// written without an alpha channel has nothing to lose in a JPEG, and a four-channel WebP that
    /// happens to be entirely opaque has nothing to keep either. A container's own "has alpha" flag is
    /// not enough for the same reason: an RGBA screenshot of a solid window declares alpha and uses none
    /// of it. The scan runs over the thumbnail, which is at most a million pixels, and one picture's
    /// worth of that is nothing next to the decode that produced it.
    /// </summary>
    private static bool UsesTransparency(SKBitmap thumbnail)
    {
        var pixels = thumbnail.GetPixelSpan();
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != byte.MaxValue) return true;
        }
        return false;
    }
}
