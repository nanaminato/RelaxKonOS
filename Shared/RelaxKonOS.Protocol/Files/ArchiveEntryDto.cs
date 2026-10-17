namespace RelaxKonOS.Protocol.Files;

/// <summary>A validated, relative archive path. Never a host filesystem path.</summary>
public sealed record ArchiveEntryDto(string Name, bool IsDirectory, long Size);
public sealed record ArchiveListRequest(string Path, ArchiveCredentials? Credentials = null);
public sealed record ArchiveReadRequest(string Path, string EntryName, ArchiveCredentials? Credentials = null);
public static class ArchivePreviewLimits
{
    public const int MaximumBytes = 16 * 1024 * 1024;
}
