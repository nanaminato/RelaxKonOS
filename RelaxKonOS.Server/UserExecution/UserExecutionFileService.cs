using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Routes every request-scoped file operation through the effective OS user boundary.</summary>
public sealed class UserExecutionFileService(LocalFileService direct, IUserExecutionContextResolver contexts,
    IUserExecutionTransport transport, IServerModeResolver mode, IHttpContextAccessor http) : IFileService
{
    public IReadOnlyList<DriveDto> GetDrives() => direct.GetDrives();
    public IReadOnlyList<SpecialLocationDto> GetSpecialLocations(string? userHomeDirectory = null)
        => Run<IReadOnlyList<SpecialLocationDto>>(UserExecutionOperationKind.FileGetSpecialLocations);
    public DirectoryDto GetDirectory(string? path) => Run<DirectoryDto>(UserExecutionOperationKind.FileListDirectory, path: path);
    public FileSystemEntryDto? GetInfo(string path) => Run<FileSystemEntryDto?>(UserExecutionOperationKind.FileGetInfo, path: path);
    public (Stream Stream, string ContentType, string FileName)? OpenRead(string path)
    {
        var result = Run<DirectUserExecutionOperations.FileReadResult>(UserExecutionOperationKind.FileRead, path: path);
        return new MemoryStream(Convert.FromBase64String(result.ContentBase64), writable: false) is { } stream
            ? (stream, result.ContentType, result.FileName) : null;
    }
    public async Task<FileEntryDto> WriteFileAsync(string path, Stream content, CancellationToken cancellationToken = default)
        => await RunAsync<FileEntryDto>(UserExecutionOperationKind.FileWrite, path, content: await ReadContentAsync(content, cancellationToken));
    public FilePropertiesDto? GetProperties(string path) => Run<FilePropertiesDto?>(UserExecutionOperationKind.FileGetProperties, path: path);
    public FilePropertiesDto SetUnixPermissions(string path, int unixMode) => Run<FilePropertiesDto>(UserExecutionOperationKind.FileSetUnixPermissions, path: path, unixMode: unixMode);
    public void CreateDirectory(string path) => Run<bool>(UserExecutionOperationKind.FileCreateDirectory, path: path);
    public void Delete(string path) => Run<bool>(UserExecutionOperationKind.FileDelete, path: path);
    public FileSystemEntryDto Rename(string sourcePath, string newName) => Run<FileSystemEntryDto>(UserExecutionOperationKind.FileRename, path: sourcePath, newName: newName);
    public FileSystemEntryDto Move(string sourcePath, string destinationPath, bool overwrite) => Run<FileSystemEntryDto>(UserExecutionOperationKind.FileMove, path: sourcePath, destinationPath: destinationPath, overwrite: overwrite);
    public FileSystemEntryDto Copy(string sourcePath, string destinationPath, bool overwrite) => Run<FileSystemEntryDto>(UserExecutionOperationKind.FileCopy, path: sourcePath, destinationPath: destinationPath, overwrite: overwrite);
    public async Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default)
        => await RunAsync<FileEntryDto>(UserExecutionOperationKind.FileUpload, targetDirectoryPath, fileName: fileName, content: await ReadContentAsync(content, cancellationToken));

    // ---- Resumable upload staging ----------------------------------------------------------------
    // A chunk is carried as base64 in one bounded request, so the effective user writes the staging file
    // with the same permissions it will need for the destination. The session chunk size is chosen to keep
    // the encoded request below UserExecutionProtocol.MaximumFileContentBytes.

    public void CreateStagingFile(string stagingPath)
        => Run<bool>(UserExecutionOperationKind.FileCreateStaging, path: stagingPath);
    public async Task<long> AppendStagingAsync(string stagingPath, long offset, long expectedBytes, Stream content, CancellationToken cancellationToken = default)
        => await RunAsync<long>(UserExecutionOperationKind.FileAppendStaging, stagingPath, offset: offset,
            expectedBytes: expectedBytes, content: await ReadContentAsync(content, cancellationToken));
    public long StagingLength(string stagingPath)
    {
        try { return Run<long>(UserExecutionOperationKind.FileGetStagingLength, path: stagingPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or UserExecutionException or InvalidOperationException or TimeoutException or ArgumentException)
        { return -1; }
    }
    public bool DeleteStagingFile(string stagingPath)
    {
        try { return Run<bool>(UserExecutionOperationKind.FileDeleteStaging, path: stagingPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or UserExecutionException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            return false;
        }
    }
    public FileEntryDto CommitStagingFile(string stagingPath, string destinationPath)
        => Run<FileEntryDto>(UserExecutionOperationKind.FileCommitStaging, path: stagingPath, destinationPath: destinationPath);

    private T Run<T>(UserExecutionOperationKind operation, string? path = null, string? destinationPath = null, string? newName = null,
        string? fileName = null, bool overwrite = false, string? content = null, int? unixMode = null)
        => RunAsync<T>(operation, path, destinationPath, newName, fileName, overwrite, content, unixMode).GetAwaiter().GetResult();

    private async Task<T> RunAsync<T>(UserExecutionOperationKind operation, string? path = null, string? destinationPath = null, string? newName = null,
        string? fileName = null, bool overwrite = false, string? content = null, int? unixMode = null,
        long? offset = null, long? expectedBytes = null)
    {
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("User execution requires an authenticated HTTP request.");
        var context = contexts.Resolve(principal);
        var request = new UserExecutionRequest(context.Identity, operation, path, destinationPath, newName, fileName, overwrite,
            content, unixMode, offset, expectedBytes, OperationId: Guid.NewGuid());
        if (mode.Mode == ServerMode.User)
        {
            var validation = new DirectUserExecutionService(mode).Validate(context, request);
            Throw(validation);
            return await DirectAsync<T>(request);
        }
        var result = await transport.ExecuteAsync(request, http.HttpContext?.RequestAborted ?? CancellationToken.None);
        Throw(result);
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64!), RelaxKonOSJsonOptions.Default)!; }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new InvalidOperationException("User-execution Helper returned an invalid result."); }
    }

    /// <summary>
    /// User Mode has always executed in the Server's own process, where the process <em>is</em> the
    /// effective user. The operation mapping is shared with the local-identity backend so the two
    /// cannot drift; this path stays otherwise unchanged.
    /// </summary>
    private async Task<T> DirectAsync<T>(UserExecutionRequest request)
        => (T)(await DirectUserExecutionOperations.ExecuteAsync(direct, request))!;

    private static async Task<string> ReadContentAsync(Stream content, CancellationToken cancellationToken)
    { await using var copy = new MemoryStream(); await content.CopyToAsync(copy, cancellationToken); if (copy.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new IOException("File content is too large."); return Convert.ToBase64String(copy.ToArray()); }
    private static void Throw(UserExecutionResult result)
    {
        if (result.Success) return;
        var message = result.ProblemCode switch
        {
            UserExecutionProblemCode.AccessDenied => "Access denied for the authenticated OS user.",
            UserExecutionProblemCode.NotFound => "User-execution path not found.",
            UserExecutionProblemCode.InvalidRequest or UserExecutionProblemCode.ContentTooLarge => "Invalid user-execution file request.",
            UserExecutionProblemCode.Conflict => "User-execution file operation failed.",
            UserExecutionProblemCode.TimedOut => "User-execution file operation timed out.",
            // In-process execution is restricted to the Server's own account. That refusal is a
            // deployment choice the operator can fix, so it says so instead of sharing the generic
            // "unavailable" text with a missing Helper.
            UserExecutionProblemCode.IdentityNotExecutable => "This Server executes ordinary user operations only as its own OS account; run them as another account through the Helper.",
            _ => "User-execution Helper is unavailable.",
        };
        // Access, path and conflict failures describe the effective OS user's file operation.
        // Preserve their ordinary IFileService exception contracts so FileEndpoints can issue the
        // scoped elevation challenge (or its normal 404/409 response). Only a failed execution
        // boundary itself remains a UserExecutionException and therefore a 503.
        switch (result.ProblemCode)
        {
            case UserExecutionProblemCode.AccessDenied:
                throw new UnauthorizedAccessException(message);
            case UserExecutionProblemCode.NotFound:
                throw new FileNotFoundException(message);
            case UserExecutionProblemCode.InvalidRequest:
            case UserExecutionProblemCode.ContentTooLarge:
                throw new ArgumentException(message);
            case UserExecutionProblemCode.Conflict:
                throw new IOException(message);
        }
        throw new UserExecutionException(result.ProblemCode, message);
    }
}
