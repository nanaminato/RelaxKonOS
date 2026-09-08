namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Remote paths must be parsed independently of the client operating system.</summary>
public sealed record ExplorerBreadcrumb(string Label, string? Path)
{
    public static IReadOnlyList<ExplorerBreadcrumb> FromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        var windows = ExplorerPath.IsWindows(path);
        var separator = windows ? '\\' : '/';
        var normalized = windows ? path.Replace('/', '\\') : path;
        var segments = normalized.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<ExplorerBreadcrumb>();
        string current;
        int start;
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            // A UNC share is the navigation root; do not manufacture a server-only folder.
            if (segments.Length < 2) return [new(path, path)];
            current = "\\\\" + segments[0] + "\\" + segments[1];
            start = 2;
        }
        else if (windows && normalized.Length >= 2 && normalized[1] == ':')
        {
            current = normalized[..2] + "\\";
            start = 1;
        }
        else
        {
            current = "/";
            start = 0;
        }
        result.Add(new(current, current));
        foreach (var segment in segments.Skip(start))
        {
            current = current.TrimEnd(separator) + separator + segment;
            result.Add(new(segment, current));
        }
        return result;
    }

    public static string? ParentPath(string path)
    {
        var breadcrumbs = FromPath(path);
        return breadcrumbs.Count > 1 ? breadcrumbs[^2].Path : null;
    }
}
