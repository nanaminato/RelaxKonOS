using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Common;
using System.Text.Json;

namespace RelaxKonOS.Server.Privileged;

public sealed class PrivilegedFileService(IPrivilegedOperationTransport runner) : IPrivilegedFileService
{
    public async Task<IReadOnlyList<SpecialLocationDto>> GetSpecialLocationsAsync(PrivilegedFileAuthorizationSource source, string home, CancellationToken cancellationToken = default)
        => await SendAsync<SpecialLocationDto[]>(new(PrivilegedOperationKind.FileGetSpecialLocations,
            Path: home, FileAuthorizationSource: source), home, cancellationToken);

    public Task<FileSystemEntryDto?> GetInfoAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default)
        => SendAsync<FileSystemEntryDto?>(new(PrivilegedOperationKind.FileGetInfo,
            Path: path, FileAuthorizationSource: source), path, cancellationToken);

    public Task<FilePropertiesDto?> GetPropertiesAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default)
        => SendAsync<FilePropertiesDto?>(new(PrivilegedOperationKind.FileGetProperties,
            Path: path, FileAuthorizationSource: source), path, cancellationToken);

    public Task<FilePropertiesDto> SetUnixPermissionsAsync(PrivilegedFileAuthorizationSource source, string path, int unixMode, CancellationToken cancellationToken = default)
        => SendAsync<FilePropertiesDto>(new(PrivilegedOperationKind.FileSetUnixPermissions,
            Path: path, FileAuthorizationSource: source, UnixMode: unixMode), path, cancellationToken);

    public async Task<DirectoryDto> ListDirectoryAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default)
    {
        return await SendAsync<DirectoryDto>(new(PrivilegedOperationKind.FileListDirectory,
            Path: path, FileAuthorizationSource: source), path, cancellationToken);
    }

    public async Task<(Stream Stream, string FileName)> OpenReadAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default)
    {
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.FileRead,
            Path: path, FileAuthorizationSource: source), cancellationToken);
        if (!result.Success) throw ToException(result, path);
        var bytes = Convert.FromBase64String(result.OutputBase64 ?? string.Empty);
        return (new MemoryStream(bytes, writable: false), Path.GetFileName(path));
    }

    public async Task<FileEntryDto> WriteAsync(PrivilegedFileAuthorizationSource source, string path, Stream content, CancellationToken cancellationToken = default)
    {
        using var bytes = await ReadContentAsync(content, cancellationToken);
        return await SendAsync<FileEntryDto>(new(PrivilegedOperationKind.FileWrite, Path: path,
            ContentBase64: Convert.ToBase64String(bytes.ToArray()), FileAuthorizationSource: source), path, cancellationToken);
    }

    public async Task DeleteAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default)
        => EnsureSuccess(await runner.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.FileDelete,
            Path: path, FileAuthorizationSource: source), cancellationToken), path);

    public async Task<FileSystemEntryDto> RenameAsync(PrivilegedFileAuthorizationSource source, string sourcePath, string newName, CancellationToken cancellationToken = default)
    {
        return await SendAsync<FileSystemEntryDto>(new(PrivilegedOperationKind.FileRename,
            Path: sourcePath, NewName: newName, FileAuthorizationSource: source), sourcePath, cancellationToken);
    }

    public async Task<FileSystemEntryDto> MoveAsync(PrivilegedFileAuthorizationSource source, string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        return await SendAsync<FileSystemEntryDto>(new(PrivilegedOperationKind.FileMove, Path: sourcePath,
            DestinationPath: destinationPath, Overwrite: overwrite, FileAuthorizationSource: source), sourcePath, cancellationToken);
    }

    public async Task<FileSystemEntryDto> CopyAsync(PrivilegedFileAuthorizationSource source, string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        return await SendAsync<FileSystemEntryDto>(new(PrivilegedOperationKind.FileCopy, Path: sourcePath,
            DestinationPath: destinationPath, Overwrite: overwrite, FileAuthorizationSource: source), sourcePath, cancellationToken);
    }

    public async Task<FileEntryDto> UploadAsync(PrivilegedFileAuthorizationSource source, string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var bytes = await ReadContentAsync(content, cancellationToken);
        var path = Path.Combine(targetDirectoryPath, fileName);
        return await SendAsync<FileEntryDto>(new(PrivilegedOperationKind.FileUpload, Path: targetDirectoryPath,
            FileName: fileName, ContentBase64: Convert.ToBase64String(bytes.ToArray()),
            FileAuthorizationSource: source), path, cancellationToken);
    }

    public async Task CreateDirectoryAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken cancellationToken = default)
        => EnsureSuccess(await runner.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.FileCreateDirectory,
            Path: path, FileAuthorizationSource: source), cancellationToken), path);

    public async Task CreateStagingAsync(PrivilegedFileAuthorizationSource source, string stagingPath, CancellationToken cancellationToken = default)
    {
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.FileCreateStaging,
            Path: stagingPath, FileAuthorizationSource: source), cancellationToken);
        EnsureSuccess(result, stagingPath);
    }

    public async Task<long> AppendChunkAsync(PrivilegedFileAuthorizationSource source, string stagingPath, long offset, Stream content, CancellationToken cancellationToken = default)
    {
        using var bytes = await ReadContentAsync(content, cancellationToken);
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.FileUploadChunk,
            Path: stagingPath, Offset: offset, ContentBase64: Convert.ToBase64String(bytes.ToArray()),
            FileAuthorizationSource: source), cancellationToken);
        if (!result.Success) throw ToException(result, stagingPath);
        // The Helper reports the length it actually flushed; failing back to arithmetic would let the
        // server confirm bytes that were never written.
        return result.Offset ?? offset + bytes.Length;
    }

    public async Task<FileEntryDto> CommitAsync(PrivilegedFileAuthorizationSource source, string stagingPath, string destinationFileName, CancellationToken cancellationToken = default)
    {
        return await SendAsync<FileEntryDto>(new(PrivilegedOperationKind.FileUploadCommit,
            Path: stagingPath, FileName: destinationFileName, FileAuthorizationSource: source), stagingPath, cancellationToken);
    }

    public Task<long> StagingLengthAsync(PrivilegedFileAuthorizationSource source, string stagingPath, CancellationToken cancellationToken = default)
        => SendAsync<long>(new(PrivilegedOperationKind.FileGetStagingLength,
            Path: stagingPath, FileAuthorizationSource: source), stagingPath, cancellationToken);

    public Task<bool> DeleteStagingAsync(PrivilegedFileAuthorizationSource source, string stagingPath, CancellationToken cancellationToken = default)
        => SendAsync<bool>(new(PrivilegedOperationKind.FileDeleteStaging,
            Path: stagingPath, FileAuthorizationSource: source), stagingPath, cancellationToken);

    private static void EnsureSuccess(RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult result, string path)
    {
        if (!result.Success) throw ToException(result, path);
    }

    private async Task<T> SendAsync<T>(PrivilegedOperationRequest request, string path, CancellationToken cancellationToken)
    {
        var result = await runner.ExecuteAsync(request, cancellationToken);
        EnsureSuccess(result, path);
        try
        {
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64
                ?? throw new IOException("Privileged Helper returned no file result.")), RelaxKonOSJsonOptions.Default)!;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new IOException("Privileged Helper returned an invalid file result.", error);
        }
    }

    private static Exception ToException(PrivilegedOperationResult result, string path) => result.ExitCode switch
    {
        2 => new FileNotFoundException(result.Error ?? "File not found", path),
        69 => new InvalidOperationException(result.Error ?? "Privileged helper unavailable"),
        77 => new UnauthorizedAccessException(result.Error),
        _ => new IOException(result.Error ?? "Privileged file operation failed"),
    };

    private static async Task<MemoryStream> ReadContentAsync(Stream content, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer;
            if (buffer.Length + read > PrivilegedOperationProtocol.MaximumFileContentBytes)
            {
                await buffer.DisposeAsync();
                throw new IOException("Privileged file content exceeds the Helper limit.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }
}
