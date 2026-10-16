using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>Validated archive contents, independent of UI and host destination paths.</summary>
public interface IArchiveReader : IAsyncDisposable
{
    IReadOnlyList<ArchiveEntryDto> Entries { get; }
    Task CopyEntryAsync(string name, Stream output, CancellationToken ct);
}

internal static class ArchiveEntryValidation
{
    public static void Validate(IReadOnlyList<ArchiveEntryDto> entries)
    {
        if (entries.Count > FileArchiveService.MaximumEntries) throw new InvalidDataException("Too many archive entries.");
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            FileArchiveService.ValidateName(entry.Name);
            if (!names.TryAdd(entry.Name.Normalize(), entry.IsDirectory)) throw new InvalidDataException("Duplicate archive entries.");
            if (entry.Size < 0 || entry.Size > FileArchiveService.MaximumEntryBytes || entry.IsDirectory && entry.Size != 0
                || (total = checked(total + entry.Size)) > FileArchiveService.MaximumTotalBytes)
                throw new InvalidDataException("Archive expansion limit exceeded.");
        }
        foreach (var name in names.Keys)
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                if (names.TryGetValue(name[..slash], out var directory) && !directory)
                    throw new InvalidDataException("An archive file is also a parent directory.");
    }
}
