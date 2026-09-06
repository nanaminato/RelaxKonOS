namespace Client.Apps.Explorer.Models;

/// <summary>Lexical operations on server paths, independent of the client OS.
/// These helpers do not resolve symlinks or replace server-side authorization.</summary>
public static class ExplorerPath
{
    public static bool IsWindows(string? path) => !string.IsNullOrEmpty(path)
        && (path.StartsWith("\\\\", StringComparison.Ordinal)
            || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'));

    public static bool IsAbsolute(string path) => path.StartsWith('/')
        || path.StartsWith("\\\\", StringComparison.Ordinal)
        || (IsWindows(path) && path.Length > 2 && path[2] is '\\' or '/');

    public static StringComparison Comparison(string? path) => IsWindows(path)
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (IsWindows(path)) return path.Replace('/', '\\').TrimEnd('\\');
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    public static bool Equal(string? a, string? b) => IsWindows(a) == IsWindows(b)
        && string.Equals(Normalize(a), Normalize(b), Comparison(a));

    public static bool IsAncestorOrEqual(string? ancestor, string descendant)
    {
        if (string.IsNullOrEmpty(ancestor) || IsWindows(ancestor) != IsWindows(descendant)) return false;
        var a = Normalize(ancestor)!;
        var d = Normalize(descendant)!;
        return string.Equals(a, d, Comparison(a))
            || d.StartsWith(a.TrimEnd(IsWindows(a) ? '\\' : '/') + (IsWindows(a) ? '\\' : '/'), Comparison(a));
    }

    public static string Resolve(string directory, string path)
    {
        if (IsAbsolute(path)) return path;
        if (IsWindows(directory) && path.StartsWith('\\'))
            return ExplorerBreadcrumb.FromPath(directory)[0].Path!.TrimEnd('\\') + path;
        var separator = IsWindows(directory) ? '\\' : '/';
        return directory.TrimEnd(separator) + separator + (IsWindows(directory) ? path.Replace('/', '\\') : path);
    }

    public static string Combine(string directory, string name)
    {
        if (!IsValidName(name, directory)) throw new ArgumentException("Invalid file or folder name.", nameof(name));
        var separator = IsWindows(directory) ? '\\' : '/';
        var normalized = IsWindows(directory) ? directory.Replace('/', '\\') : directory;
        return normalized.TrimEnd(separator) + separator + name;
    }

    public static bool IsValidName(string? name, string? directory)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0')) return false;
        if (!IsWindows(directory)) return true; // Backslashes and colons are legal POSIX filename characters.
        if (name.Any(c => c < 32 || "\\<>:\"|?*".Contains(c)) || name.EndsWith('.') || name.EndsWith(' ')) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }

    public static string? Parent(string path) => ExplorerBreadcrumb.ParentPath(path);
    public static string Extension(string name)
    {
        var index = name.LastIndexOf('.');
        return index < 0 ? string.Empty : name[index..];
    }
}
