using RelaxKonOS.Server.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// Checks the thumbnail renderer against images this check actually encodes.
///
/// Nothing here is a mock: the point of the renderer is that it agrees with real files about what an
/// image is, how big it is and which way up it goes, and a fake decoder would agree with itself
/// instead. What is checked is the boundary the endpoint depends on — the media type it will
/// announce, the size the client will draw, and the cases that must answer "no thumbnail" rather
/// than an exception or an allocation the host cannot afford.
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
        using (var image = new Image<Rgb24>(800, 400))
        {
            image.SaveAsJpeg(path);
        }

        var thumbnail = Render(path, 200);
        Assert(thumbnail is { ContentType: "image/jpeg" }, "An opaque photograph is answered as JPEG");
        Assert(Size(thumbnail!.Bytes) == (200, 100), "A 800×400 original becomes a 200×100 thumbnail, aspect ratio intact");
        Assert(thumbnail.Bytes.Length < new FileInfo(path).Length, "The thumbnail is smaller than the file it stands in for");
    }

    private static void CheckSmallPictureIsNotEnlarged(string directory)
    {
        var path = Path.Combine(directory, "icon.jpg");
        using (var image = new Image<Rgb24>(120, 60))
        {
            image.SaveAsJpeg(path);
        }

        var thumbnail = Render(path, 1024);
        Assert(Size(thumbnail!.Bytes) == (120, 60), "A picture smaller than the requested box is returned at its own size");
    }

    private static void CheckTransparencyKeepsPng(string directory)
    {
        var path = Path.Combine(directory, "logo.png");
        using (var logo = new Image<Rgba32>(64, 64))
        {
            // The left half stays fully transparent; JPEG has nowhere to put that, which is the whole
            // reason the encoder is chosen from the decoded pixels instead of from the extension.
            for (var y = 0; y < logo.Height; y++)
                for (var x = 32; x < logo.Width; x++)
                    logo[x, y] = new Rgba32(255, 0, 0, 255);
            logo.SaveAsPng(path);
        }

        var thumbnail = Render(path, 64);
        Assert(thumbnail is { ContentType: "image/png" }, "An image with an alpha channel is answered as PNG");
        using var decoded = Image.Load<Rgba32>(thumbnail!.Bytes);
        Assert(decoded[0, 0].A == 0 && decoded[63, 0].A == 255, "The transparent half is still transparent in the thumbnail");
    }

    private static void CheckOpaqueColorTypeUsesJpeg(string directory)
    {
        var path = Path.Combine(directory, "opaque.png");
        using (var image = new Image<Rgb24>(64, 64))
        {
            image.SaveAsPng(path);
        }

        var thumbnail = Render(path, 64);
        Assert(thumbnail is { ContentType: "image/jpeg" },
            "A PNG without an alpha channel is answered as JPEG: the format follows the pixels, not the container");
    }

    private static void CheckOrientationIsAppliedBeforeResizing(string directory)
    {
        var path = Path.Combine(directory, "portrait.jpg");
        using (var image = new Image<Rgb24>(200, 100))
        {
            // What every phone camera writes: the sensor image is landscape and the tag says to turn
            // it. The client draws the original upright, so a thumbnail that ignored the tag would
            // arrive on its side — and resizing before turning would resample along the wrong axis.
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
            image.SaveAsJpeg(path);
        }

        var thumbnail = Render(path, 200);
        Assert(Size(thumbnail!.Bytes) == (100, 200), "EXIF orientation is applied before the thumbnail is resized");
    }

    private static void CheckAnimatedPictureYieldsOneStillFrame(string directory)
    {
        var path = Path.Combine(directory, "animation.gif");
        using (var animation = new Image<Rgba32>(40, 40))
        {
            animation.Frames.AddFrame(animation.Frames.RootFrame);
            animation.Frames.AddFrame(animation.Frames.RootFrame);
            Assert(animation.Frames.Count == 3, "The animated probe really carries three frames");
            animation.SaveAsGif(path);
        }

        var thumbnail = Render(path, 40);
        Assert(thumbnail is not null, "An animated original still answers with a thumbnail");
        using var decoded = Image.Load(thumbnail!.Bytes);
        Assert(decoded.Frames.Count == 1, "The thumbnail is one still frame, however many the original carries");
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

        using var probe = File.OpenRead(path);
        Assert(Image.Identify(probe) is { Width: 40_000, Height: 40_000 },
            "The oversized probe is a readable header claiming 1.6 billion pixels");
        Assert(Render(path, 256) is null,
            "A picture whose header claims more pixels than the server decodes is refused instead of allocated");
    }

    private static void CheckEdgeMustBeInRange(string directory)
    {
        var path = Path.Combine(directory, "range.jpg");
        using (var image = new Image<Rgb24>(80, 40))
        {
            image.SaveAsJpeg(path);
        }

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

    /// <summary>The renderer's answer for a file on disk, through the same stream type the endpoint passes.</summary>
    private static RenderedThumbnail? Render(string path, int maxEdge)
    {
        using var stream = File.OpenRead(path);
        return ImageThumbnailRenderer.Render(stream, maxEdge);
    }

    /// <summary>The pixel size of an encoded image, which is what the client will lay out.</summary>
    private static (int Width, int Height) Size(byte[] bytes)
    {
        using var image = Image.Load(bytes);
        return (image.Width, image.Height);
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
    /// A 24-bit BMP file header that declares the given size and carries no pixel data at all — which
    /// is the shape of a decompression bomb: a few dozen bytes that would decode into gigabytes. BMP
    /// is used rather than PNG because its header has no checksum to compute.
    /// </summary>
    private static byte[] BitmapHeader(int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(bytes, 2);      // file size
        BitConverter.GetBytes(54).CopyTo(bytes, 10);     // offset of the pixel array
        BitConverter.GetBytes(40).CopyTo(bytes, 14);     // size of the DIB header
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);   // colour planes
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);  // bits per pixel
        BitConverter.GetBytes(0).CopyTo(bytes, 30);          // uncompressed
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
