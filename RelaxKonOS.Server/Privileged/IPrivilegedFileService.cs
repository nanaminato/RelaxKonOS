using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Privileged;

public interface IPrivilegedFileService
{
    Task<DirectoryDto> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<(Stream Stream, string FileName)> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    Task<FileEntryDto> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    // ---- Resumable upload staging inside a protected directory -----------------------------------
    // The Helper caps one request at MaximumFileContentBytes, so a large upload into a directory this
    // process cannot write to is only possible in chunks. All three members are shape-constrained by the
    // Helper itself: the staging name must parse as ".<destination>.<sessionId>.rkup" and a commit names
    // its destination by a single component inside the staging file's own directory.

    /// <summary>Creates the zero-length staging file for a session.</summary>
    Task CreateStagingAsync(string stagingPath, CancellationToken cancellationToken = default);

    /// <summary>Appends one chunk at an explicit offset and returns the resulting length.</summary>
    Task<long> AppendChunkAsync(string stagingPath, long offset, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Renames the staging file onto <paramref name="destinationFileName"/> inside its directory.</summary>
    Task<FileEntryDto> CommitAsync(string stagingPath, string destinationFileName, CancellationToken cancellationToken = default);
}
