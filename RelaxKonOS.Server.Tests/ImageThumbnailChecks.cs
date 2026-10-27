using System.Buffers.Binary;
using RelaxKonOS.Server.Files;
using SkiaSharp;

/// <summary>
/// Checks the thumbnail renderer against images this check actually encodes.
///
/// Nothing here is a mock: the point of the renderer is that it agrees with real files about what an
/// image is, how big it is and which way up it goes, and a fake decoder would agree with itself
/// instead. What is checked is the boundary the endpoint depends on — the media type it will
/// announce, the size the client will draw, and the cases that must answer "no thumbnail" rather
/// than an exception or an allocation the host cannot afford.
///
/// The size of a thumbnail is read back out of the encoded bytes by this file itself — the PNG header,
/// the JPEG frame header — rather than by handing the answer to a decoder and asking it. The decoder
/// that produced those bytes is the same one that would read them, so asking it would only prove that
/// a round trip is consistent; parsing the container proves the bytes say what a client will act on.
/// </summary>
public static class ImageThumbnailChecks
{
    public static void Run(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "thumbnails"));
        try
        {
            CheckJpegIsShrunkWithItsAspectRatio(directory.FullName);
            CheckSmallPictureIsNotEnlarged(directory.FullName);
            CheckTransparencyKeepsPng(directory.FullName);
            CheckOpaqueColorTypeUsesJpeg(directory.FullName);
            CheckOrientationIsAppliedBeforeResizing(directory.FullName);
            CheckAnimatedPictureYieldsOneStillFrame(directory.FullName);
            CheckNonImagesAnswerNothing(directory.FullName);
            CheckOversizedHeaderIsRefusedBeforeAllocation(directory.FullName);
            CheckEdgeMustBeInRange(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void CheckJpegIsShrunkWithItsAspectRatio(string directory)
    {
        var path = Path.Combine(directory, "photo.jpg");
        Save(path, Paint(800, 400, transparentLeftHalf: false), SKEncodedImageFormat.Jpeg, 90);

        var thumbnail = Render(path, 200);
        Assert(thumbnail is { ContentType: "image/jpeg" }, "An opaque photograph is answered as JPEG");
        Assert(JpegSize(thumbnail!.Bytes) == (200, 100), "A 800×400 original becomes a 200×100 thumbnail, aspect ratio intact");
        Assert(thumbnail.Bytes.Length < new FileInfo(path).Length, "The thumbnail is smaller than the file it stands in for");
    }

    private static void CheckSmallPictureIsNotEnlarged(string directory)
    {
        var path = Path.Combine(directory, "icon.jpg");
        Save(path, Paint(120, 60, transparentLeftHalf: false), SKEncodedImageFormat.Jpeg, 90);

        var thumbnail = Render(path, 1024);
        Assert(JpegSize(thumbnail!.Bytes) == (120, 60), "A picture smaller than the requested box is returned at its own size");
    }

    private static void CheckTransparencyKeepsPng(string directory)
    {
        var path = Path.Combine(directory, "logo.png");
        Save(path, Paint(64, 64, transparentLeftHalf: true), SKEncodedImageFormat.Png, 100);

        var thumbnail = Render(path, 64);
        Assert(thumbnail is { ContentType: "image/png" }, "An image with an alpha channel is answered as PNG");
        using var decoded = SKBitmap.Decode(thumbnail!.Bytes);
        Assert(decoded is not null && decoded.GetPixel(0, 0).Alpha == 0 && decoded.GetPixel(63, 0).Alpha == 255,
            "The transparent half is still transparent in the thumbnail");
    }

    private static void CheckOpaqueColorTypeUsesJpeg(string directory)
    {
        var path = Path.Combine(directory, "opaque.png");
        // Saved as PNG, so the container carries an alpha channel; every pixel uses it. The encoder is
        // chosen from the pixels, so this one leaves as JPEG.
        Save(path, Paint(64, 64, transparentLeftHalf: false), SKEncodedImageFormat.Png, 100);

        var thumbnail = Render(path, 64);
        Assert(thumbnail is { ContentType: "image/jpeg" },
            "A PNG without an alpha channel is answered as JPEG: the format follows the pixels, not the container");
    }

    private static void CheckOrientationIsAppliedBeforeResizing(string directory)
    {
        var path = Path.Combine(directory, "portrait.jpg");
        // What every phone camera writes: the sensor image is landscape and the tag says to turn it. The
        // client draws the original upright, so a thumbnail that ignored the tag would arrive on its
        // side — and resizing before turning would resample along the wrong axis.
        Save(path, Paint(200, 100, transparentLeftHalf: false), SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, WithExifOrientation(File.ReadAllBytes(path), 6));

        var thumbnail = Render(path, 200);
        Assert(JpegSize(thumbnail!.Bytes) == (100, 200), "EXIF orientation is applied before the thumbnail is resized");
    }

    private static void CheckAnimatedPictureYieldsOneStillFrame(string directory)
    {
        var path = Path.Combine(directory, "animation.gif");
        File.Copy(Fixture("animation-3-frames.gif"), path, overwrite: true);
        using (var source = File.OpenRead(path))
        using (var probe = SKCodec.Create(source))
        {
            Assert(probe is not null && probe.FrameCount == 3, "The animated probe really carries three frames");
        }

        var thumbnail = Render(path, 40);
        Assert(thumbnail is not null, "An animated original still answers with a thumbnail");
        using var decoded = SKCodec.Create(new MemoryStream(thumbnail!.Bytes));
        Assert(decoded is not null && decoded.FrameCount <= 1, "The thumbnail is one still frame, however many the original carries");
        // Which container it left in follows the frame's pixels — this GIF is opaque, so it is a JPEG —
        // and the declared size is the same either way.
        Assert(SizeOf(thumbnail) == (8, 8), "The animated thumbnail keeps the frame's size");
    }

    private static void CheckNonImagesAnswerNothing(string directory)
    {
        var path = Path.Combine(directory, "notes.txt");
        File.WriteAllText(path, "This is not a picture, and the server must not pretend otherwise.");
        Assert(Render(path, 256) is null, "A file that is not an image has no thumbnail and no exception");

        var empty = Path.Combine(directory, "empty.bin");
        File.WriteAllBytes(empty, []);
        Assert(Render(empty, 256) is null, "An empty file has no thumbnail and no exception");
    }

    private static void CheckOversizedHeaderIsRefusedBeforeAllocation(string directory)
    {
        var path = Path.Combine(directory, "bomb.bmp");
        File.WriteAllBytes(path, BitmapHeader(width: 40_000, height: 40_000));

        using (var probe = File.OpenRead(path))
        using (var codec = SKCodec.Create(probe))
        {
            Assert(codec is { Info.Width: 40_000, Info.Height: 40_000 },
                "The oversized probe is a readable header claiming 1.6 billion pixels");
        }
        Assert(Render(path, 256) is null,
            "A picture whose header claims more pixels than the server decodes is refused instead of allocated");
    }

    private static void CheckEdgeMustBeInRange(string directory)
    {
        var path = Path.Combine(directory, "range.jpg");
        Save(path, Paint(80, 40, transparentLeftHalf: false), SKEncodedImageFormat.Jpeg, 90);

        foreach (var edge in new[] { 0, ImageThumbnailRenderer.MinimumMaxEdge - 1, ImageThumbnailRenderer.MaximumMaxEdge + 1 })
        {
            using var stream = File.OpenRead(path);
            Assert(Throws<ArgumentOutOfRangeException>(() => ImageThumbnailRenderer.Render(stream, edge)),
                $"A thumbnail edge of {edge} pixels is rejected rather than clamped");
        }

        using (var stream = File.OpenRead(path))
        {
            Assert(Throws<ArgumentException>(() => ImageThumbnailRenderer.Render(new ForwardOnlyStream(File.ReadAllBytes(path)), 128)),
                "A source that cannot be re-read is rejected: the header is read before the pixels");
            Assert(stream.CanSeek, "The stream the endpoint hands over is seekable");
        }
    }

    /// <summary>A flat picture whose left half is either transparent or fully opaque.</summary>
    private static SKBitmap Paint(int width, int height, bool transparentLeftHalf)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var alpha = transparentLeftHalf && x < width / 2 ? (byte)0 : byte.MaxValue;
            pixels[offset + 0] = (byte)(0x10 + y % 200);
            pixels[offset + 1] = (byte)(0x20 + x % 200);
            pixels[offset + 2] = 0x30;
            pixels[offset + 3] = alpha;
        }
        return bitmap;
    }

    private static void Save(string path, SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(format, quality))
        {
            if (data is null) throw new InvalidOperationException("The check could not encode its own fixture as " + format);
            File.WriteAllBytes(path, data.ToArray());
        }
    }

    /// <summary>The renderer's answer for a file on disk, through the same stream type the endpoint passes.</summary>
    private static RenderedThumbnail? Render(string path, int maxEdge)
    {
        using var stream = File.OpenRead(path);
        return ImageThumbnailRenderer.Render(stream, maxEdge);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>The declared pixel size of a rendered thumbnail, read from the container it left in.</summary>
    private static (int Width, int Height) SizeOf(RenderedThumbnail thumbnail)
        => thumbnail.ContentType == "image/png" ? PngSize(thumbnail.Bytes) : JpegSize(thumbnail.Bytes);

    /// <summary>The pixel size a PNG declares, which is the first thing a client reads.</summary>
    private static (int Width, int Height) PngSize(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50) return (-1, -1);
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
    }

    /// <summary>The pixel size a JPEG's frame header declares, walked through its segment table.</summary>
    private static (int Width, int Height) JpegSize(byte[] bytes)
    {
        var index = 2;
        while (index + 9 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
            {
                index++;
                continue;
            }
            var marker = bytes[index + 1];
            // Standalone markers carry no length field: the image delimiters, the escape that means
            // "this 0xFF was data", and the restart markers.
            if (marker is 0xD8 or 0xD9 or 0x01 or 0xFF or (>= 0xD0 and <= 0xD7))
            {
                index += 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 7)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 5)));
            if (length < 2) return (-1, -1);
            index += 2 + length;
        }
        return (-1, -1);
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return true;
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Inserts an EXIF orientation tag into a JPEG, in the one segment shape every reader understands:
    /// an APP1 block holding a little-endian TIFF header whose IFD0 carries a single entry.
    ///
    /// The bytes are written here rather than committed as a fixture so that the check states what the
    /// file means instead of carrying an opaque blob — and so a reader can see that orientation 6 is
    /// "turn the stored landscape upright".
    /// </summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        var tiff = new byte[26];
        tiff[0] = 0x49;
        tiff[1] = 0x49;                                                  // "II": little-endian
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);      // IFD0 starts after the header
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);      // one entry
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112); // Orientation
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);     // type SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);     // one value
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        var payload = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.AsSpan().CopyTo(payload.AsSpan(6));
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;                                               // APP1
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.AsSpan().CopyTo(segment.AsSpan(4));
        var result = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);                                 // SOI stays first
        segment.AsSpan().CopyTo(result.AsSpan(2));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }

    /// <summary>
    /// A 24-bit BMP file header that declares the given size and carries no pixel data at all — which
    /// is the shape of a decompression bomb: a few dozen bytes that would decode into gigabytes. BMP
    /// is used rather than PNG because its header has no checksum to compute.
    /// </summary>
    private static byte[] BitmapHeader(int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), 54);    // file size
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);   // offset of the pixel array
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);   // size of the DIB header
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);    // colour planes
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);   // bits per pixel
        return bytes;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS THUMBNAILS: " + message);
    }

    /// <summary>Reads forward once and never back, like a network body rather than a file.</summary>
    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(count, content.Length - _offset);
            if (available <= 0) return 0;
            Array.Copy(content, _offset, buffer, offset, available);
            _offset += available;
            return available;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
