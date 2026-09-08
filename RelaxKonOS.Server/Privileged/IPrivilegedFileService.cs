using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Privileged;

public interface IPrivilegedFileService
{
    Task<(Stream Stream, string FileName)> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    Task<FileEntryDto> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
}
