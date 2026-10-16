using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

public interface IArchiveClient
{
    Task<IReadOnlyList<ArchiveEntryDto>> ListArchiveAsync(string path, CancellationToken ct);
    Task<byte[]> ReadArchiveEntryAsync(string path, string entryName, CancellationToken ct);
}
