using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

public interface IArchiveClient
{
    Task<IReadOnlyList<ArchiveEntryDto>> ListArchiveAsync(string path, ArchiveCredentials? credentials, CancellationToken ct);
    Task<byte[]> ReadArchiveEntryAsync(string path, string entryName, ArchiveCredentials? credentials, CancellationToken ct);
}
