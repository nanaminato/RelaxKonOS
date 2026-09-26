using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.ServerCenter;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// Provides the subset of the remote file API needed by the built-in editors while an SSH desktop
/// is active. Every operation creates an SFTP connection whose host key is checked against the
/// fingerprint accepted for the current SSH desktop session.
/// </summary>
public sealed class SshExplorerClient(SshDesktopSession session) : IExplorerClient
{
    public Task<IReadOnlyList<DriveDto>> GetDrivesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DriveDto>>([new DriveDto("/", "/", null, true)]);

    public Task<IReadOnlyList<SpecialLocationDto>> GetSpecialLocationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SpecialLocationDto>>([]);

    public Task<DirectoryDto> GetDirectoryAsync(string? path, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            client.ChangeDirectory(string.IsNullOrWhiteSpace(path) ? "." : path);
            var directoryPath = client.WorkingDirectory;
            var entries = client.ListDirectory(directoryPath)
                .Where(entry => entry.Name is not ("." or ".."))
                .ToArray();
            var directories = entries.Where(entry => entry.IsDirectory && !entry.IsSymbolicLink)
                .Select(ToDirectoryEntry)
                .ToArray();
            var files = entries.Where(entry => !entry.IsDirectory || entry.IsSymbolicLink)
                .Select(ToFileEntry)
                .ToArray();
            return new DirectoryDto(directoryPath, DirectoryName(directoryPath), FileSystemEntryType.Directory,
                directories, files, null, null);
        }, ct);

    public Task<FileSystemEntryDto?> GetInfoAsync(string path, CancellationToken ct = default) =>
        ExecuteAsync<FileSystemEntryDto?>(client =>
        {
            if (!client.Exists(path)) return null;
            var entry = client.Get(path);
            return entry.IsDirectory && !entry.IsSymbolicLink ? ToDirectoryEntry(entry) : ToFileSystemEntry(entry);
        }, ct);

    public Task<(Stream Stream, string FileName)?> DownloadAsync(string path, CancellationToken ct = default) =>
        ExecuteAsync<(Stream Stream, string FileName)?>(client =>
        {
            if (!client.Exists(path)) return null;
            var stream = new MemoryStream();
            client.DownloadFile(path, stream);
            stream.Position = 0;
            return (stream, Path.GetFileName(path));
        }, ct);

    public Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default) =>
        ExecuteAsync<byte[]?>(client =>
        {
            if (!client.Exists(path)) return null;
            using var stream = new MemoryStream();
            client.DownloadFile(path, stream);
            return stream.ToArray();
        }, ct);

    public Task<FileEntryDto> WriteFileAsync(string path, byte[] content, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            using var stream = new MemoryStream(content, writable: false);
            client.UploadFile(stream, path, canOverride: true);
            return ToFileEntry(client.Get(path));
        }, ct);

    public Task<FileSystemEntryDto> CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            client.CreateDirectory(path);
            return ToDirectoryEntry(client.Get(path));
        }, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            var entry = client.Get(path);
            if (entry.IsDirectory && !entry.IsSymbolicLink) client.DeleteDirectory(path);
            else client.DeleteFile(path);
            return true;
        }, ct);

    public Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            var target = Combine(ParentPath(sourcePath), newName);
            client.RenameFile(sourcePath, target);
            var entry = client.Get(target);
            return entry.IsDirectory && !entry.IsSymbolicLink ? ToDirectoryEntry(entry) : ToFileSystemEntry(entry);
        }, ct);

    public Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            if (!overwrite && client.Exists(destinationPath)) throw new IOException("Destination already exists.");
            client.RenameFile(sourcePath, destinationPath);
            var entry = client.Get(destinationPath);
            return entry.IsDirectory && !entry.IsSymbolicLink ? ToDirectoryEntry(entry) : ToFileSystemEntry(entry);
        }, ct);

    public Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content,
        IProgress<long>? progress = null, CancellationToken ct = default) =>
        ExecuteAsync(client =>
        {
            var target = Combine(targetDirectoryPath, fileName);
            if (client.Exists(target)) throw new IOException("Destination already exists.");
            client.UploadFile(content, target, uploaded => progress?.Report((long)uploaded));
            return ToFileEntry(client.Get(target));
        }, ct);

    public Task<FileOperationDto> StartOperationAsync(StartFileOperationRequest request, CancellationToken ct = default) => Unsupported<FileOperationDto>();
    public Task<IReadOnlyList<FileOperationDto>> ListOperationsAsync(CancellationToken ct = default) => Unsupported<IReadOnlyList<FileOperationDto>>();
    public Task<FileOperationDto> GetOperationAsync(Guid id, CancellationToken ct = default) => Unsupported<FileOperationDto>();
    public Task<FileOperationDto> CancelOperationAsync(Guid id, CancellationToken ct = default) => Unsupported<FileOperationDto>();
    public Task<FileOperationDto> DecideOperationAsync(Guid id, FileOperationDecisionRequest request, CancellationToken ct = default) => Unsupported<FileOperationDto>();
    public Task<FileElevationResult> ElevateFileAccessAsync(string path, FileElevationCapability capability, string? password = null, string? administratorUsername = null, CancellationToken ct = default) => Unsupported<FileElevationResult>();
    public Task<FileElevationResult> ElevateFileOperationAsync(IReadOnlyList<string> directoryPaths, FileElevationCapability capability, string? password = null, string? administratorUsername = null, CancellationToken ct = default) => Unsupported<FileElevationResult>();
    public Task<FilePropertiesDto?> GetPropertiesAsync(string path, CancellationToken ct = default) => Unsupported<FilePropertiesDto?>();
    public Task<FilePropertiesDto> SetUnixPermissionsAsync(string path, int unixMode, CancellationToken ct = default) => Unsupported<FilePropertiesDto>();
    public Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default) => Unsupported<FileSystemEntryDto>();

    private async Task<T> ExecuteAsync<T>(Func<SftpClient, T> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var client = await Task.Run(OpenClient, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return action(client);
        }, ct).ConfigureAwait(false);
    }

    private SftpClient OpenClient()
    {
        var endpoint = session.Endpoint ?? throw new InvalidOperationException("SSH session ended.");
        var fingerprint = session.HostKeyFingerprint ?? throw new InvalidOperationException("SSH host key is unavailable.");
        var password = session.Password ?? throw new InvalidOperationException("SSH session ended.");
        var client = new SftpClient(endpoint.Host, endpoint.Port, endpoint.UserName, password);
        client.HostKeyReceived += (_, args) =>
            args.CanTrust = string.Equals(ServerHostTrustRules.Fingerprint(args.HostKey), fingerprint, StringComparison.Ordinal);
        try
        {
            client.Connect();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This SFTP operation is not available in an SSH desktop."));

    private static FileSystemEntryDto ToDirectoryEntry(ISftpFile entry) =>
        new(entry.FullName, entry.Name, null, FileSystemEntryType.Directory, null, Modified(entry), null,
            IsHidden(entry.Name), false, null);

    private static FileSystemEntryDto ToFileSystemEntry(ISftpFile entry) =>
        new(entry.FullName, entry.Name, entry.Length, FileSystemEntryType.File, null, Modified(entry), null,
            IsHidden(entry.Name), false, MimeType(entry.Name));

    private static FileEntryDto ToFileEntry(ISftpFile entry) =>
        new(entry.FullName, entry.Name, Path.GetExtension(entry.Name), entry.Length, null, Modified(entry), null,
            IsHidden(entry.Name), false, MimeType(entry.Name));

    private static DateTimeOffset? Modified(ISftpFile entry) => entry.LastWriteTime == default
        ? null
        : new DateTimeOffset(entry.LastWriteTime.ToUniversalTime());

    private static bool IsHidden(string name) => name.StartsWith(".", StringComparison.Ordinal) && name.Length > 1;
    private static string MimeType(string name) => "application/octet-stream";
    private static string DirectoryName(string path) => path == "/" ? "/" : Path.GetFileName(path.TrimEnd('/'));
    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var separator = trimmed.LastIndexOf('/');
        return separator <= 0 ? "/" : trimmed[..separator];
    }
    private static string Combine(string directory, string name) => directory.TrimEnd('/') + "/" + name;
}
