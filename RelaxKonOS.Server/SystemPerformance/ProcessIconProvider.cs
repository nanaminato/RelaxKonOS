using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Resolves an icon for a confirmed process instance and caches the small PNG by executable.</summary>
internal sealed partial class ProcessIconProvider : IDisposable
{
    private const int IconSize = 32;
    internal const int MaximumSvgBytes = 256 * 1024;
    internal const int MaximumPngBytes = 1024 * 1024;
    internal const int MaximumIconDimension = 1024;
    private static readonly ProcessIconContent EmptyIcon = new([], "image/png");
    private readonly ProcessIconCache _cache = new();
    private Lazy<IReadOnlyDictionary<string, string>>? _desktopIcons = new(ReadDesktopIcons,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ProcessIconWorkQueue _work;

    internal ProcessIconProvider() => _work = new(() =>
    { _cache.Dispose(); Interlocked.Exchange(ref _desktopIcons, null); });

    internal (int Entries, int Bytes, int OrderEntries) CacheSnapshot => _cache.Describe();

    public Task<ProcessIconContent?> GetIconAsync(int processId, DateTimeOffset expectedStartTime, CancellationToken cancellationToken)
        => _work.RunAsync(() => Task.Run(() => Resolve(processId, expectedStartTime), CancellationToken.None), cancellationToken);

    internal Task StopAsync(CancellationToken ct) => _work.StopAsync(ct);

    private ProcessIconContent? Resolve(int processId, DateTimeOffset expectedStartTime)
    {
        try
        {
            string? path;
            if (OperatingSystem.IsWindows())
            {
                // Creation time and image path come from the same handle; module enumeration
                // requires stronger access and can fail across process architectures.
                using var handle = NativeMethods.OpenProcess(0x1000, false, processId);
                if (handle.IsInvalid || !NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _)
                    || DateTime.FromFileTimeUtc(creation) != expectedStartTime.UtcDateTime) return null;
                var buffer = new StringBuilder(32768);
                var size = buffer.Capacity;
                path = NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            else
            {
                using var process = Process.GetProcessById(processId);
                if (process.StartTime.ToUniversalTime() != expectedStartTime.UtcDateTime) return null;
                path = GetExecutablePath(processId, process);
                if (process.StartTime.ToUniversalTime() != expectedStartTime.UtcDateTime) return null;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var file = new FileInfo(path);
            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            var icon = _cache.GetOrAdd(key, () => LoadIcon(file.FullName));
            return icon.Data.Length == 0 ? null : icon;
        }
        catch { return null; }
    }

    private static string? GetExecutablePath(int processId, Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            try { return new FileInfo($"/proc/{processId}/exe").ResolveLinkTarget(returnFinalTarget: true)?.FullName; }
            catch { return null; }
        }

        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private ProcessIconContent LoadIcon(string executablePath)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new(ReadWindowsExecutableIcon(executablePath), "image/png");
            if (OperatingSystem.IsLinux()) return ReadLinuxDesktopIcon(executablePath);
        }
        catch { /* Icon lookup is optional; a generic client icon is used when it fails. */ }
        return EmptyIcon;
    }

    private ProcessIconContent ReadLinuxDesktopIcon(string executablePath)
    {
        var binaryName = Path.GetFileName(executablePath);
        var desktopIcons = Volatile.Read(ref _desktopIcons);
        if (desktopIcons is null || !desktopIcons.Value.TryGetValue(binaryName, out var iconPath)) return EmptyIcon;
        return ReadDesktopIconFile(iconPath);
    }

    /// <summary>Read a bounded snapshot before invoking the decoder, then budget pixels before allocating them.</summary>
    internal static ProcessIconContent ReadDesktopIconFile(string iconPath)
    {
        try
        {
            var svg = Path.GetExtension(iconPath).Equals(".svg", StringComparison.OrdinalIgnoreCase);
            if (!svg && !Path.GetExtension(iconPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) return EmptyIcon;
            using var file = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = ReadIconBytes(file, svg ? MaximumSvgBytes : MaximumPngBytes);
            if (bytes is null) return EmptyIcon;
            if (svg) return new(bytes, "image/svg+xml");
            using var encoded = new MemoryStream(bytes, writable: false);
            using var codec = SKCodec.Create(encoded);
            if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png
                || codec.Info.Width is <= 0 or > MaximumIconDimension || codec.Info.Height is <= 0 or > MaximumIconDimension)
                return EmptyIcon;
            // Force 4 bytes per pixel; source formats must not select a larger destination allocation.
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var icon = new SKBitmap(info);
            if (icon.GetPixels() == IntPtr.Zero || codec.GetPixels(info, icon.GetPixels()) != SKCodecResult.Success) return EmptyIcon;
            var png = FitOnSquarePng(icon, IconSize);
            return png.Length == 0 ? EmptyIcon : new ProcessIconContent(png, "image/png");
        }
        catch { return EmptyIcon; } // Optional icon failure keeps the existing generic-client fallback.
    }

    internal static byte[]? ReadIconBytes(Stream input, int maximum)
    {
        if (maximum is <= 0 or > MaximumPngBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        var length = input.CanSeek ? input.Length : 0;
        if (input.CanSeek && (length <= 0 || length > maximum)) return null;
        // Small icons should not allocate the entire maximum input budget. Growth remains bounded.
        var bytes = new byte[(int)Math.Min(Math.Max(length, 4096L), maximum) + 1];
        var total = 0;
        while (true)
        {
            if (total == bytes.Length) Array.Resize(ref bytes, Math.Min(bytes.Length * 2, maximum + 1));
            var read = input.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
            if (total > maximum) return null;
        }
        if (total == 0) return null;
        Array.Resize(ref bytes, total);
        return bytes;
    }

    /// <summary>
    /// Draws <paramref name="source"/> centred on a transparent square of <paramref name="size"/> and
    /// encodes it as PNG.
    ///
    /// Desktop icon themes mix 16, 32, 48, 128 and 256 pixel files, so the client would otherwise have to
    /// rescale a different shape for every row. Padding rather than cropping keeps a non-square icon
    /// whole; the transparent band is what a list row already assumes it is drawing into.
    /// </summary>
    private static byte[] FitOnSquarePng(SKBitmap source, int size)
    {
        var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        var fitted = new SKImageInfo(
            Math.Max(1, (int)MathF.Round(source.Width * scale)),
            Math.Max(1, (int)MathF.Round(source.Height * scale)),
            SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var scaled = source.Resize(fitted, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (scaled is null) return [];
        using var square = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(scaled, (size - fitted.Width) / 2f, (size - fitted.Height) / 2f);
        }
        return EncodePng(square);
    }

    /// <summary>Encodes a bitmap as PNG, or answers an empty payload when the encoder refuses.</summary>
    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? [];
    }

    public void Dispose()
        => _work.BeginStop();

    private static byte[] ReadWindowsExecutableIcon(string executablePath)
    {
        var fileInfo = NativeMethods.SHGetFileInfo(executablePath, 0, out var shellInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(), 0x000000100 | 0x000000000);
        if (fileInfo == IntPtr.Zero || shellInfo.Icon == IntPtr.Zero) return [];
        try
        {
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return [];
            var memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) { NativeMethods.ReleaseDC(IntPtr.Zero, screen); return []; }
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = IconSize, Height = -IconSize,
                    Planes = 1, BitCount = 32, Compression = 0, ImageSize = IconSize * IconSize * 4,
                },
            };
            var dib = NativeMethods.CreateDIBSection(screen, ref bitmapInfo, 0, out var pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memory); NativeMethods.ReleaseDC(IntPtr.Zero, screen); return [];
            }
            var previous = NativeMethods.SelectObject(memory, dib);
            try
            {
                Marshal.Copy(new byte[IconSize * IconSize * 4], 0, pixels, IconSize * IconSize * 4);
                if (!NativeMethods.DrawIconEx(memory, 0, 0, shellInfo.Icon, IconSize, IconSize, 0, IntPtr.Zero, 0x0003)) return [];
                var data = new byte[IconSize * IconSize * 4];
                Marshal.Copy(pixels, data, 0, data.Length);
                return EncodeIconPng(data);
            }
            finally
            {
                if (previous != IntPtr.Zero) NativeMethods.SelectObject(memory, previous);
                NativeMethods.DeleteObject(dib); NativeMethods.DeleteDC(memory); NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }
        finally { NativeMethods.DestroyIcon(shellInfo.Icon); }
    }

    /// <summary>
    /// Turns the 32 × 32 BGRA pixels GDI painted into a PNG.
    ///
    /// The DIB section already holds the icon at the size the client draws, so there is nothing to
    /// decode or resample here — only the container changes, and the byte order is the one Skia calls
    /// <c>Bgra8888</c> so the copy is straight. A pixel that carries a colour but no alpha is promoted
    /// to opaque, which is what the shell's own mask means by "this pixel is part of the icon"; without
    /// that, icons drawn with a legacy mask come out as a transparent square.
    /// </summary>
    private static byte[] EncodeIconPng(byte[] bgra)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(IconSize, IconSize, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var target = bitmap.GetPixelSpan();
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            var alpha = bgra[offset + 3];
            if (alpha == 0 && (bgra[offset] != 0 || bgra[offset + 1] != 0 || bgra[offset + 2] != 0)) alpha = byte.MaxValue;
            target[offset + 0] = bgra[offset + 0];
            target[offset + 1] = bgra[offset + 1];
            target[offset + 2] = bgra[offset + 2];
            target[offset + 3] = alpha;
        }
        return EncodePng(bitmap);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public ShellFileInfo() { DisplayName = string.Empty; TypeName = string.Empty; }
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public int ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr flickerFreeBrush, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(IntPtr dc);
    }
}
