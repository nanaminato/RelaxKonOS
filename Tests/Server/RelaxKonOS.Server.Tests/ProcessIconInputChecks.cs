using RelaxKonOS.Server.SystemPerformance;
using SkiaSharp;

internal static class ProcessIconInputChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS ICON INPUT {++count}: {text}"); }
        var root = Path.Combine(Path.GetTempPath(), "icon-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pngPath = Path.Combine(root, "icon.png");
            File.WriteAllBytes(pngPath, Png(32, 16));
            var icon = ProcessIconProvider.ReadDesktopIconFile(pngPath);
            using (var bitmap = SKBitmap.Decode(icon.Data))
                Check(icon.ContentType == "image/png" && bitmap is { Width: 32, Height: 32 }
                    && bitmap.GetPixel(0, 0).Alpha == 0 && bitmap.GetPixel(16, 16).Red > 200,
                    "Real PNG decoding preserves the 32px square, aspect ratio and transparent padding");
            File.WriteAllBytes(pngPath, Png(ProcessIconProvider.MaximumIconDimension, ProcessIconProvider.MaximumIconDimension));
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length > 0, "A real PNG at the 1024px dimension budget remains supported");
            File.WriteAllBytes(pngPath, Png(ProcessIconProvider.MaximumIconDimension + 1, 1));
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "An over-wide PNG is rejected before destination pixel allocation");
            File.WriteAllBytes(pngPath, Png(1, ProcessIconProvider.MaximumIconDimension + 1));
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "An over-tall PNG is rejected before destination pixel allocation");
            File.WriteAllBytes(pngPath, new byte[ProcessIconProvider.MaximumPngBytes + 1]);
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "PNG encoded input above 1 MiB is rejected before codec creation");
            File.WriteAllBytes(pngPath, [1, 2, 3, 4]);
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "Malformed PNG retains the existing missing-icon fallback");
            using (var bitmap = new SKBitmap(16, 16))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90)) File.WriteAllBytes(pngPath, jpeg.ToArray());
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "A differently encoded image with a PNG extension is not accepted as PNG");
            var png = Png(32, 16);
            File.WriteAllBytes(pngPath, png[..(png.Length / 2)]);
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "Truncated PNG cannot become a successful partial decoded icon");
            File.WriteAllBytes(pngPath, []);
            Check(ProcessIconProvider.ReadDesktopIconFile(pngPath).Data.Length == 0, "Empty files do not enter native decoding");
            var svgPath = Path.Combine(root, "icon.svg");
            var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>".PadRight(ProcessIconProvider.MaximumSvgBytes));
            File.WriteAllBytes(svgPath, svg);
            var vector = ProcessIconProvider.ReadDesktopIconFile(svgPath);
            Check(vector.ContentType == "image/svg+xml" && vector.Data.SequenceEqual(svg), "SVG at 256 KiB is returned unchanged without raster decoding");
            using (var file = new FileStream(svgPath, FileMode.Append)) file.WriteByte(32);
            Check(ProcessIconProvider.ReadDesktopIconFile(svgPath).Data.Length == 0, "SVG above 256 KiB is rejected");
            Check(ProcessIconProvider.ReadDesktopIconFile(Path.Combine(root, "missing.png")).Data.Length == 0
                && ProcessIconProvider.ReadDesktopIconFile(root).Data.Length == 0, "Missing and unreadable/non-file icon inputs remain optional failures");
            using (var exact = new CountingInput(new byte[ProcessIconProvider.MaximumSvgBytes]))
                Check(ProcessIconProvider.ReadIconBytes(exact, ProcessIconProvider.MaximumSvgBytes)?.Length == ProcessIconProvider.MaximumSvgBytes,
                    "Actual byte counting accepts exactly the budget with a short-read nonseekable source");
            using (var changed = new CountingInput(new byte[65536], reportedLength: 1))
                Check(ProcessIconProvider.ReadIconBytes(changed, ProcessIconProvider.MaximumSvgBytes)?.Length == 65536,
                    "Growth within the input budget is read completely despite a stale small length hint");
            using (var small = new MemoryStream(new byte[128], writable: false))
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = ProcessIconProvider.ReadIconBytes(small, ProcessIconProvider.MaximumPngBytes);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(result?.Length == 128 && allocated < 65536,
                    "A small icon does not allocate the full 1 MiB maximum input budget");
            }
            using (var growing = new CountingInput(new byte[ProcessIconProvider.MaximumSvgBytes * 2], reportedLength: 1))
                Check(ProcessIconProvider.ReadIconBytes(growing, ProcessIconProvider.MaximumSvgBytes) is null
                    && growing.BytesRead == ProcessIconProvider.MaximumSvgBytes + 1,
                    "Input growth after the length hint is rejected after at most budget plus one bytes");
            using (var oversized = new CountingInput([1], reportedLength: ProcessIconProvider.MaximumSvgBytes + 1))
                Check(ProcessIconProvider.ReadIconBytes(oversized, ProcessIconProvider.MaximumSvgBytes) is null && oversized.BytesRead == 0,
                    "A known oversize input is refused without allocating its read buffer or consuming data");
            if (OperatingSystem.IsLinux())
            {
                File.WriteAllBytes(pngPath, Png(16, 16));
                var link = Path.Combine(root, "theme-link.png");
                File.CreateSymbolicLink(link, pngPath);
                Check(ProcessIconProvider.ReadDesktopIconFile(link).Data.Length > 0, "Existing Linux icon-theme symbolic links still use the same bounded decoder");
            }
            Console.WriteLine($"Process icon input checks passed: {count}");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class CountingInput(byte[] data, long? reportedLength = null) : Stream
    {
        internal int BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => reportedLength.HasValue;
        public override bool CanWrite => false;
        public override long Length => reportedLength ?? throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, 137), data.Length - BytesRead);
            data.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset)); BytesRead += read; return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
