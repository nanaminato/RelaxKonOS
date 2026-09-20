using System.IO.Compression;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Bounded, traversal-safe archive extraction for a deployment build context. Only declared files
/// are materialized: no links, no absolute paths, no parent traversal, and both the declared and the
/// observed expanded size are enforced so an archive cannot lie its way past the limit.
/// </summary>
internal static class ApplicationArchiveSafety
{
    internal sealed record ExtractionReport(int EntryCount, long ExpandedBytes);

    public static async Task<ExtractionReport> ExtractAsync(Stream archive, string destination, ApplicationDeploymentOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);
        using var zip = OpenArchive(archive);
        if (zip.Entries.Count > options.MaximumArchiveEntries)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveTooManyEntries, 400);

        long declared = 0;
        long observed = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Normalize(entry.FullName, options);
            if (relative is null)
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveUnsafeEntry, 400);
            if (relative.Length == 0) continue;
            if (IsLink(entry))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveUnsafeEntry, 400);

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsWithin(target, root))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveUnsafeEntry, 400);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            declared += entry.Length;
            if (declared > options.MaximumExpandedBytes)
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveExpandedTooLarge, 400);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var destinationStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                observed += read;
                if (observed > options.MaximumExpandedBytes)
                    throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveExpandedTooLarge, 400);
                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return new(zip.Entries.Count, observed);
    }

    private static ZipArchive OpenArchive(Stream archive)
    {
        try { return new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveUnavailable, 400); }
    }

    /// <summary>Returns a canonical relative path, or null when the entry name is unusable or unsafe.</summary>
    private static string? Normalize(string name, ApplicationDeploymentOptions options)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return null;
        var normalized = name.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return string.Empty;
        if (normalized.StartsWith('/') || normalized.Length >= 2 && normalized[1] == ':') return null;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > options.MaximumPathDepth) return null;
        if (segments.Any(segment => segment is "." or "..")) return null;
        return string.Join('/', segments);
    }

    /// <summary>
    /// Detects a symbolic-link entry through the stored Unix mode bits. The server materializes file
    /// contents itself, so a link can never become a real link; rejecting it keeps the build context
    /// free of entries that only pretend to be files.
    /// </summary>
    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
