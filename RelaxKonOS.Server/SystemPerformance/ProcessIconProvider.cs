using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Resolves an icon for a confirmed process instance and caches the small PNG by executable.</summary>
internal sealed class ProcessIconProvider
{
    private const int IconSize = 32;
    private const int MaxCachedIcons = 512;
    private static readonly ProcessIconContent EmptyIcon = new([], "image/png");
    private static readonly ConcurrentDictionary<string, Lazy<ProcessIconContent>> IconCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> CacheOrder = new();
    private static readonly Lazy<IReadOnlyDictionary<string, string>> DesktopIcons = new(ReadDesktopIcons,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<ProcessIconContent?> GetIconAsync(int processId, DateTimeOffset expectedStartTime, CancellationToken cancellationToken)
        => Task.Run(() => Resolve(processId, expectedStartTime), cancellationToken);

    private static ProcessIconContent? Resolve(int processId, DateTimeOffset expectedStartTime)
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
            var lazy = IconCache.GetOrAdd(key, cacheKey =>
            {
                CacheOrder.Enqueue(cacheKey);
                return new Lazy<ProcessIconContent>(() => LoadIcon(file.FullName), LazyThreadSafetyMode.ExecutionAndPublication);
            });
            TrimCache();
            var icon = lazy.Value;
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

    private static ProcessIconContent LoadIcon(string executablePath)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new(ReadWindowsExecutableIcon(executablePath), "image/png");
            if (OperatingSystem.IsLinux()) return ReadLinuxDesktopIcon(executablePath);
        }
        catch { /* Icon lookup is optional; a generic client icon is used when it fails. */ }
        return EmptyIcon;
    }

    private static ProcessIconContent ReadLinuxDesktopIcon(string executablePath)
    {
        var binaryName = Path.GetFileName(executablePath);
        if (!DesktopIcons.Value.TryGetValue(binaryName, out var iconPath)) return EmptyIcon;
        if (Path.GetExtension(iconPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var file = new FileInfo(iconPath);
            if (file.Length is <= 0 or > 262_144) return EmptyIcon;
            return new ProcessIconContent(File.ReadAllBytes(iconPath), "image/svg+xml");
        }
        using var image = Image.Load<Rgba32>(iconPath);
        image.Mutate(operation => operation.Resize(new ResizeOptions
        {
            Size = new Size(IconSize, IconSize),
            Mode = ResizeMode.Pad,
            PadColor = Color.Transparent,
        }));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return new ProcessIconContent(output.ToArray(), "image/png");
    }

    private static IReadOnlyDictionary<string, string> ReadDesktopIcons()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetDesktopDirectories())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, "*.desktop", SearchOption.AllDirectories).ToArray(); }
            catch { continue; }
            foreach (var file in files)
            {
                try
                {
                    string? command = null;
                    string? icon = null;
                    var hidden = false;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith("Exec=", StringComparison.Ordinal)) command = FirstDesktopCommand(line[5..]);
                        else if (line.StartsWith("Icon=", StringComparison.Ordinal)) icon = line[5..].Trim();
                        else if (line.Equals("NoDisplay=true", StringComparison.OrdinalIgnoreCase) || line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)) hidden = true;
                    }
                    if (hidden || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(icon)) continue;
                    var binary = Path.GetFileName(command);
                    var resolvedIcon = ResolveIconPath(icon);
                    if (!string.IsNullOrWhiteSpace(binary) && resolvedIcon is not null) result.TryAdd(binary, resolvedIcon);
                }
                catch { /* One malformed desktop file must not hide other application icons. */ }
            }
        }
        return result;
    }

    private static IEnumerable<string> GetDesktopDirectories()
    {
        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            "/usr/share/applications", "/usr/local/share/applications", "/var/lib/snapd/desktop/applications",
            "/root/.local/share/applications",
        };
        foreach (var root in (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share").Split(':'))
            roots.Add(Path.Combine(root, "applications"));
        try
        {
            foreach (var home in Directory.EnumerateDirectories("/home")) roots.Add(Path.Combine(home, ".local/share/applications"));
        }
        catch { }
        return roots.Where(Directory.Exists);
    }

    private static string? FirstDesktopCommand(string exec)
    {
        var token = ReadToken(exec.AsSpan().TrimStart(), out var rest);
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!Path.GetFileName(token).Equals("env", StringComparison.Ordinal)) return token;
        while (rest.TrimStart() is { } remaining && remaining.Length > 0)
        {
            token = ReadToken(remaining, out rest);
            if (token is null) break;
            if (token.Contains('=')) continue;
            return token;
        }
        return null;
    }

    private static string? ReadToken(ReadOnlySpan<char> input, out ReadOnlySpan<char> rest)
    {
        input = input.TrimStart();
        if (input.IsEmpty) { rest = []; return null; }
        if (input[0] is '"' or '\'')
        {
            var quote = input[0];
            var end = input[1..].IndexOf(quote);
            if (end < 0) { rest = []; return null; }
            var token = input.Slice(1, end).ToString();
            rest = input[(end + 2)..];
            return token;
        }
        var separator = input.IndexOfAny(' ', '\t');
        if (separator < 0) { rest = []; return input.ToString(); }
        rest = input[(separator + 1)..];
        return input[..separator].ToString();
    }

    private static string? ResolveIconPath(string icon)
    {
        if (Path.IsPathRooted(icon)) return File.Exists(icon) && IsSupportedIcon(icon) ? icon : null;
        var names = Path.HasExtension(icon) ? [icon] : new[] { icon + ".png", icon + ".svg" };
        var roots = new[] { "/usr/share/icons", "/usr/local/share/icons", "/usr/share/pixmaps", "/usr/local/share/pixmaps" };
        var sizes = new[] { "128x128", "96x96", "64x64", "48x48", "32x32", "256x256", "scalable" };
        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                if (root.EndsWith("pixmaps", StringComparison.Ordinal))
                {
                    var direct = Path.Combine(root, name);
                    if (File.Exists(direct) && IsSupportedIcon(direct)) return direct;
                    continue;
                }
                var themes = new List<string> { "hicolor" };
                try { themes.AddRange(Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Equals("hicolor", StringComparison.OrdinalIgnoreCase))!); }
                catch { }
                foreach (var theme in themes)
                foreach (var size in sizes)
                {
                    var candidate = Path.Combine(root, theme, size, "apps", name);
                    if (File.Exists(candidate) && IsSupportedIcon(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    private static bool IsSupportedIcon(string path)
        => Path.GetExtension(path) is { } extension && (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase));

    private static void TrimCache()
    {
        while (IconCache.Count > MaxCachedIcons && CacheOrder.TryDequeue(out var oldest)) IconCache.TryRemove(oldest, out _);
    }

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
                using var image = new Image<Rgba32>(IconSize, IconSize);
                for (var y = 0; y < IconSize; y++)
                for (var x = 0; x < IconSize; x++)
                {
                    var offset = (y * IconSize + x) * 4;
                    var alpha = data[offset + 3];
                    if (alpha == 0 && (data[offset] != 0 || data[offset + 1] != 0 || data[offset + 2] != 0)) alpha = 255;
                    image[x, y] = new Rgba32(data[offset + 2], data[offset + 1], data[offset], alpha);
                }
                using var output = new MemoryStream();
                image.SaveAsPng(output);
                return output.ToArray();
            }
            finally
            {
                if (previous != IntPtr.Zero) NativeMethods.SelectObject(memory, previous);
                NativeMethods.DeleteObject(dib); NativeMethods.DeleteDC(memory); NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }
        finally { NativeMethods.DestroyIcon(shellInfo.Icon); }
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
