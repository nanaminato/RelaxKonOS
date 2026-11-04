using System.Text;

namespace RelaxKonOS.Server.SystemPerformance;

internal sealed partial class ProcessIconProvider
{
    internal const int MaximumDesktopRoots = 64;
    internal const int MaximumDesktopEntries = 8192;
    internal const int MaximumDesktopDepth = 8;
    internal const int MaximumDesktopFileBytes = 64 * 1024;
    internal const int MaximumDesktopReadBytes = 8 * 1024 * 1024;
    internal const int MaximumDesktopMappings = 2048;
    internal const int MaximumDesktopTextBytes = 512 * 1024;
    internal const int MaximumDesktopPathChecks = 32768;
    internal const int MaximumThemeEntries = 128;
    private const int MaximumPathCharacters = 4096;

    private static IReadOnlyDictionary<string, string> ReadDesktopIcons()
        => BuildDesktopIcons(GetDesktopDirectories(), out _);

    internal static IReadOnlyDictionary<string, string> BuildDesktopIcons(IEnumerable<string> roots,
        out DesktopScanSnapshot snapshot, Func<string, string?>? resolveIcon = null)
    {
        var scan = new DesktopScan(resolveIcon);
        using var directories = roots.GetEnumerator();
        while (!scan.Exhausted && scan.Roots < MaximumDesktopRoots && directories.MoveNext())
        {
            scan.Roots++;
            scan.Visit(directories.Current, 0);
        }
        snapshot = new(scan.Roots, scan.Entries, scan.ReadBytes, scan.Result.Count, scan.TextBytes, scan.PathChecks, scan.ThemeEntries);
        return scan.Result;
    }

    internal readonly record struct DesktopScanSnapshot(int Roots, int Entries, int ReadBytes, int Mappings,
        int TextBytes, int PathChecks, int ThemeEntries);

    private sealed class DesktopScan(Func<string, string?>? resolver)
    {
        internal readonly Dictionary<string, string> Result = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _themes = new(StringComparer.Ordinal);
        internal int Roots, Entries, ReadBytes, TextBytes, PathChecks, ThemeEntries;
        internal bool Exhausted => Entries >= MaximumDesktopEntries || ReadBytes >= MaximumDesktopReadBytes
            || Result.Count >= MaximumDesktopMappings || TextBytes >= MaximumDesktopTextBytes || PathChecks >= MaximumDesktopPathChecks;

        internal void Visit(string directory, int depth)
        {
            if (directory.Length > MaximumPathCharacters || depth > MaximumDesktopDepth || Exhausted) return;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (!Exhausted && entries.MoveNext())
                {
                    var path = entries.Current;
                    Entries++;
                    if (path.Length > MaximumPathCharacters) continue;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        // Count links toward the work budget; directory cycles are never followed.
                        if ((attributes & FileAttributes.Directory) != 0)
                        { if ((attributes & FileAttributes.ReparsePoint) == 0) Visit(path, depth + 1); }
                        else if (Path.GetExtension(path).Equals(".desktop", StringComparison.Ordinal)) Read(path);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        private void Read(string path)
        {
            byte[]? bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                try { bytes = ReadIconBytes(file, Math.Min(MaximumDesktopFileBytes, MaximumDesktopReadBytes - ReadBytes)); }
                finally { ReadBytes += (int)file.Position; } // Includes the extra over-limit detection byte and partial failed reads.
            }
            if (bytes is null) return;
            string? command = null, icon = null;
            var hidden = false;
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("Exec=", StringComparison.Ordinal)) command = FirstDesktopCommand(line[5..]);
                else if (line.StartsWith("Icon=", StringComparison.Ordinal)) icon = line[5..].Trim();
                else if (line.Equals("NoDisplay=true", StringComparison.OrdinalIgnoreCase) || line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)) hidden = true;
            }
            if (hidden || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(icon)
                || command.Length > MaximumPathCharacters || icon.Length > MaximumPathCharacters) return;
            var binary = Path.GetFileName(command);
            if (string.IsNullOrWhiteSpace(binary) || Result.ContainsKey(binary)) return;
            var resolved = resolver is null ? Resolve(icon) : resolver(icon);
            if (resolved is null || resolved.Length > MaximumPathCharacters) return;
            var size = (binary.Length + resolved.Length) * sizeof(char);
            if (size > MaximumDesktopTextBytes - TextBytes) return;
            Result.Add(binary, resolved); TextBytes += size;
        }

        private string? ExistingIcon(string path)
        {
            if (path.Length > MaximumPathCharacters || PathChecks >= MaximumDesktopPathChecks) return null;
            PathChecks++;
            return File.Exists(path) && IsSupportedIcon(path) ? path : null;
        }

        private string? Resolve(string icon)
        {
            if (Path.IsPathRooted(icon)) return ExistingIcon(icon);
            var names = Path.HasExtension(icon) ? [icon] : new[] { icon + ".png", icon + ".svg" };
            foreach (var root in new[] { "/usr/share/icons", "/usr/local/share/icons", "/usr/share/pixmaps", "/usr/local/share/pixmaps" })
            foreach (var name in names)
            {
                if (Exhausted) return null;
                if (root.EndsWith("pixmaps", StringComparison.Ordinal))
                { if (ExistingIcon(Path.Combine(root, name)) is { } direct) return direct; continue; }
                foreach (var theme in Themes(root))
                foreach (var size in new[] { "128x128", "96x96", "64x64", "48x48", "32x32", "256x256", "scalable" })
                    if (ExistingIcon(Path.Combine(root, theme, size, "apps", name)) is { } found) return found;
            }
            return null;
        }

        private IReadOnlyList<string> Themes(string root)
        {
            if (_themes.TryGetValue(root, out var cached)) return cached;
            var result = new List<string> { "hicolor" };
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(root).Take(MaximumThemeEntries))
                {
                    ThemeEntries++;
                    if (result.Count >= 64) break;
                    if (path.Length <= MaximumPathCharacters && Directory.Exists(path)
                        && Path.GetFileName(path) is { Length: > 0 } name && !name.Equals("hicolor", StringComparison.OrdinalIgnoreCase))
                        result.Add(name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            return _themes[root] = result;
        }
    }

    private static IEnumerable<string> GetDesktopDirectories()
    {
        var roots = new HashSet<string>(StringComparer.Ordinal)
        { "/usr/share/applications", "/usr/local/share/applications", "/var/lib/snapd/desktop/applications", "/root/.local/share/applications" };
        foreach (var root in (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share").Split(':', MaximumDesktopRoots + 1))
        {
            if (roots.Count >= MaximumDesktopRoots) break;
            if (root.Length <= MaximumPathCharacters - 13) roots.Add(Path.Combine(root, "applications"));
        }
        try
        {
            foreach (var home in Directory.EnumerateFileSystemEntries("/home").Take(MaximumDesktopRoots))
            {
                if (roots.Count >= MaximumDesktopRoots) break;
                if (home.Length <= MaximumPathCharacters - 26 && Directory.Exists(home)) roots.Add(Path.Combine(home, ".local/share/applications"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
            var end = input[1..].IndexOf(input[0]);
            if (end < 0) { rest = []; return null; }
            var token = input.Slice(1, end).ToString(); rest = input[(end + 2)..]; return token;
        }
        var separator = input.IndexOfAny(' ', '\t');
        if (separator < 0) { rest = []; return input.ToString(); }
        rest = input[(separator + 1)..]; return input[..separator].ToString();
    }

    private static bool IsSupportedIcon(string path)
        => Path.GetExtension(path) is { } extension && (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase));
}
