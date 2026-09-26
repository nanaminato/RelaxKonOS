using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

public interface IPrivilegedFileService
{
    Task<IReadOnlyList<SpecialLocationDto>> GetSpecialLocationsAsync(PrivilegedFileAuthorizationSource source, string home, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto?> GetInfoAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default);
    Task<FilePropertiesDto?> GetPropertiesAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default);
    Task<FilePropertiesDto> SetUnixPermissionsAsync(PrivilegedFileAuthorizationSource source, string path, int unixMode, CancellationToken cancellationToken = default);
    Task<DirectoryDto> ListDirectoryAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default);
    Task<(Stream Stream, string FileName)> OpenReadAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default);
    Task<FileEntryDto> WriteAsync(PrivilegedFileAuthorizationSource source, string path, Stream content, CancellationToken cancellationToken = default);
    Task DeleteAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> RenameAsync(PrivilegedFileAuthorizationSource source, string sourcePath, string newName, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> MoveAsync(PrivilegedFileAuthorizationSource source, string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> CopyAsync(PrivilegedFileAuthorizationSource source, string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<FileEntryDto> UploadAsync(PrivilegedFileAuthorizationSource source, string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default);

    // ---- Resumable upload staging inside a protected directory -----------------------------------
    // The Helper caps one request at MaximumFileContentBytes, so a large upload into a directory this
    // process cannot write to is only possible in chunks. All three members are shape-constrained by the
    // Helper itself: the staging name must parse as ".<destination>.<sessionId>.rkup" and a commit names
    // its destination by a single component inside the staging file's own directory.

    /// <summary>Creates the zero-length staging file for a session.</summary>
    Task CreateStagingAsync(PrivilegedFileAuthorizationSource source, string stagingPath, CancellationToken cancellationToken = default);

    /// <summary>Appends one chunk at an explicit offset and returns the resulting length.</summary>
    Task<long> AppendChunkAsync(PrivilegedFileAuthorizationSource source, string stagingPath, long offset, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Renames the staging file onto <paramref name="destinationFileName"/> inside its directory.</summary>
    Task<FileEntryDto> CommitAsync(PrivilegedFileAuthorizationSource source, string stagingPath, string destinationFileName, CancellationToken cancellationToken = default);
    Task<long> StagingLengthAsync(PrivilegedFileAuthorizationSource source, string stagingPath, CancellationToken cancellationToken = default);
    Task<bool> DeleteStagingAsync(PrivilegedFileAuthorizationSource source, string stagingPath, CancellationToken cancellationToken = default);
}
