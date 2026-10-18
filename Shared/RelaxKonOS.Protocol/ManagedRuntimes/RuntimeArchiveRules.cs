namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>Portable archive names and bounded, signed file declarations. No OS path guessing.</summary>
public static class RuntimeArchiveRules
{
    /// <summary>
    /// Every archive format this build can verify. <c>gz</c> is a single compressed member rather than a
    /// container, so it can only describe one declared executable and never a directory tree.
    /// </summary>
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal) { "zip", "tar.gz", "gz" };

    public static bool SafePath(string? path)
    {
        if (path is not { Length: > 0 and <= 512 }) return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part.EndsWith('.') || !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4
                && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && name[3] is >= '1' and <= '9') return false;
        }
        return true;
    }

    public static bool Valid(RuntimeArtifact artifact)
    {
        if (artifact.Layout is not { } layout || !SafePath(artifact.EntryPoint)
            || artifact.Format is null || !Formats.Contains(artifact.Format)
            || layout.MaximumExpandedBytes is <= 0 or > 2L * 1024 * 1024 * 1024
            || layout.MaximumEntries is <= 0 or > 4096 || layout.Files is null
            || layout.Files.Count == 0 || layout.Files.Count > layout.MaximumEntries) return false;
        // A single-member gzip carries no names and no directories: the one declared file must be the
        // entrypoint itself. Anything else would require guessing a layout the archive never states.
        if (artifact.Format == "gz" && (layout.Files.Count != 1 || layout.Files[0]?.Path != artifact.EntryPoint)) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var file in layout.Files)
        {
            if (file is null || !SafePath(file.Path) || !paths.Add(file.Path)
                || file.Length < 0 || file.Length > layout.MaximumExpandedBytes
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)) return false;
            size += file.Length;
            if (size > layout.MaximumExpandedBytes) return false;
            for (var slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
            {
                var directory = file.Path[..slash];
                if (directoryNames.TryGetValue(directory, out var name) && name != directory) return false;
                directoryNames[directory] = directory;
                directories.Add(directory);
            }
        }
        return !paths.Overlaps(directories) && paths.Count + directories.Count <= layout.MaximumEntries
            && layout.Files.Any(f => f.Path == artifact.EntryPoint && f.Executable);
    }
}
