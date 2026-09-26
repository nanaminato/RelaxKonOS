using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Routes every request-scoped file operation through the effective OS user boundary.</summary>
public sealed class UserExecutionFileService(LocalFileService direct, IUserExecutionContextResolver contexts,
    IUserExecutionTransport transport, IServerModeResolver mode, IHttpContextAccessor http,
    IHostFileAuthorizationService authorizations, IPrivilegedFileService privileged) : IFileService
{
    public IReadOnlyList<DriveDto> GetDrives() => direct.GetDrives();
    public IReadOnlyList<SpecialLocationDto> GetSpecialLocations(string? userHomeDirectory = null)
    {
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("An authenticated HTTP request is required.");
        if (mode.Mode == ServerMode.System && IsRootSession(principal))
        {
            try
            {
                var home = userHomeDirectory ?? "/root";
                RequireRootAuthorization(principal, FileElevationCapability.Read, [home]);
                return privileged.GetSpecialLocationsAsync(PrivilegedFileAuthorizationSource.HostRoot,
                    home, http.HttpContext?.RequestAborted ?? CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
            { throw HostFileExecutionException.From(error); }
        }
        return Run<IReadOnlyList<SpecialLocationDto>>(UserExecutionOperationKind.FileGetSpecialLocations);
    }
    public DirectoryDto GetDirectory(string? path) => mode.Mode == ServerMode.System && string.IsNullOrWhiteSpace(path)
        ? direct.GetDirectory(path) : Run<DirectoryDto>(UserExecutionOperationKind.FileListDirectory, path: path);
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
        if (mode.Mode == ServerMode.System && IsRootSession(principal))
            return await RunPrivilegedAsync<T>(
                RequireRootAuthorization(principal, Capability(operation), TargetPaths(operation, path, destinationPath, newName)), operation, path,
                destinationPath, newName, fileName, overwrite, content, unixMode, offset, expectedBytes);
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
        if (!result.Success && result.ProblemCode == UserExecutionProblemCode.AccessDenied)
        {
            PrivilegedFileAuthorizationSource? source;
            try { source = authorizations.Authorize(principal, Capability(operation),
                TargetPaths(operation, path, destinationPath, newName)); }
            catch (InvalidOperationException error) { throw HostFileExecutionException.From(error); }
            catch (UnauthorizedAccessException) { throw new HostFileExecutionException(403, "identity-changed", "Host identity is no longer valid."); }
            if (source is { } granted)
                return await RunPrivilegedAsync<T>(granted, operation, path, destinationPath,
                    newName, fileName, overwrite, content, unixMode, offset, expectedBytes);
        }
        Throw(result);
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64!), RelaxKonOSJsonOptions.Default)!; }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new InvalidOperationException("User-execution Helper returned an invalid result."); }
    }

    private static FileElevationCapability Capability(UserExecutionOperationKind operation) => operation switch
    {
        UserExecutionOperationKind.FileWrite or UserExecutionOperationKind.FileSetUnixPermissions => FileElevationCapability.Write,
        UserExecutionOperationKind.FileCreateDirectory => FileElevationCapability.CreateDirectory,
        UserExecutionOperationKind.FileDelete => FileElevationCapability.Delete,
        UserExecutionOperationKind.FileRename => FileElevationCapability.Rename,
        UserExecutionOperationKind.FileMove => FileElevationCapability.Move,
        UserExecutionOperationKind.FileCopy => FileElevationCapability.Copy,
        UserExecutionOperationKind.FileUpload or UserExecutionOperationKind.FileCreateStaging
            or UserExecutionOperationKind.FileAppendStaging or UserExecutionOperationKind.FileCommitStaging
            or UserExecutionOperationKind.FileDeleteStaging => FileElevationCapability.Upload,
        _ => FileElevationCapability.Read,
    };

    private bool IsRootSession(System.Security.Claims.ClaimsPrincipal principal)
    {
        try { return authorizations.IsRoot(principal); }
        catch (UnauthorizedAccessException)
        { throw new HostFileExecutionException(403, "identity-changed", "Host identity is no longer valid."); }
    }

    private PrivilegedFileAuthorizationSource RequireRootAuthorization(
        System.Security.Claims.ClaimsPrincipal principal, FileElevationCapability capability, string[] paths)
    {
        try
        {
            return authorizations.Authorize(principal, capability, paths) == PrivilegedFileAuthorizationSource.HostRoot
                ? PrivilegedFileAuthorizationSource.HostRoot
                : throw new HostFileExecutionException(403, "access-denied", "Root file authorization is unavailable.");
        }
        catch (InvalidOperationException error) { throw HostFileExecutionException.From(error); }
        catch (UnauthorizedAccessException) { throw new HostFileExecutionException(403, "identity-changed", "Host identity is no longer valid."); }
    }

    private static string[] TargetPaths(UserExecutionOperationKind operation, string? path,
        string? destinationPath, string? newName)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var target = operation == UserExecutionOperationKind.FileRename && !string.IsNullOrWhiteSpace(newName)
            ? Path.Combine(Path.GetDirectoryName(path)!, newName) : destinationPath;
        return target is null ? [path] : [path, target];
    }

    private async Task<T> RunPrivilegedAsync<T>(PrivilegedFileAuthorizationSource source,
        UserExecutionOperationKind operation, string? path, string? destinationPath, string? newName,
        string? fileName, bool overwrite, string? content, int? unixMode, long? offset, long? expectedBytes)
    {
        var ct = http.HttpContext?.RequestAborted ?? CancellationToken.None;
        using var bytes = content is null ? null : new MemoryStream(Convert.FromBase64String(content), writable: false);
        try
        {
            if (operation == UserExecutionOperationKind.FileAppendStaging && bytes?.Length != expectedBytes)
                throw new ArgumentException("Staging chunk length does not match the declared length.");
            object? result = operation switch
            {
            UserExecutionOperationKind.FileListDirectory => await privileged.ListDirectoryAsync(source, path!, ct),
            UserExecutionOperationKind.FileGetInfo => await privileged.GetInfoAsync(source, path!, ct),
            UserExecutionOperationKind.FileRead => await ReadPrivilegedAsync(source, path!, ct),
            UserExecutionOperationKind.FileWrite => await privileged.WriteAsync(source, path!, bytes!, ct),
            UserExecutionOperationKind.FileGetProperties => await privileged.GetPropertiesAsync(source, path!, ct),
            UserExecutionOperationKind.FileSetUnixPermissions => await privileged.SetUnixPermissionsAsync(source, path!, unixMode!.Value, ct),
            UserExecutionOperationKind.FileDelete => await DeletePrivilegedAsync(source, path!, ct),
            UserExecutionOperationKind.FileRename => await privileged.RenameAsync(source, path!, newName!, ct),
            UserExecutionOperationKind.FileMove => await privileged.MoveAsync(source, path!, destinationPath!, overwrite, ct),
            UserExecutionOperationKind.FileCopy => await privileged.CopyAsync(source, path!, destinationPath!, overwrite, ct),
            UserExecutionOperationKind.FileUpload => await privileged.UploadAsync(source, path!, fileName!, bytes!, ct),
            UserExecutionOperationKind.FileCreateDirectory => await CreatePrivilegedAsync(source, path!, ct),
            UserExecutionOperationKind.FileCreateStaging => await CreateStagingPrivilegedAsync(source, path!, ct),
            UserExecutionOperationKind.FileAppendStaging => await privileged.AppendChunkAsync(source, path!, offset!.Value, bytes!, ct),
            UserExecutionOperationKind.FileGetStagingLength => await privileged.StagingLengthAsync(source, path!, ct),
            UserExecutionOperationKind.FileDeleteStaging => await privileged.DeleteStagingAsync(source, path!, ct),
            UserExecutionOperationKind.FileCommitStaging => await CommitPrivilegedAsync(source, path!, destinationPath!, ct),
            _ => throw new ArgumentException("Unsupported privileged file operation."),
            };
            return (T)result!;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        { throw HostFileExecutionException.From(error); }
    }

    private async Task<DirectUserExecutionOperations.FileReadResult> ReadPrivilegedAsync(
        PrivilegedFileAuthorizationSource source, string path, CancellationToken ct)
    {
        var read = await privileged.OpenReadAsync(source, path, ct);
        using (read.Stream)
        using (var copy = new MemoryStream())
        {
            await read.Stream.CopyToAsync(copy, ct);
            return new(Convert.ToBase64String(copy.ToArray()), read.FileName, "application/octet-stream");
        }
    }

    private async Task<FileEntryDto> CommitPrivilegedAsync(PrivilegedFileAuthorizationSource source,
        string staging, string destination, CancellationToken ct)
    {
        if (!string.Equals(Path.GetDirectoryName(staging), Path.GetDirectoryName(destination), StringComparison.Ordinal))
            throw new ArgumentException("Staging destination must remain in the same directory.");
        return await privileged.CommitAsync(source, staging, Path.GetFileName(destination), ct);
    }

    private async Task<bool> DeletePrivilegedAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken ct)
    { await privileged.DeleteAsync(source, path, ct); return true; }
    private async Task<bool> CreatePrivilegedAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken ct)
    { await privileged.CreateDirectoryAsync(source, path, ct); return true; }
    private async Task<bool> CreateStagingPrivilegedAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken ct)
    { await privileged.CreateStagingAsync(source, path, ct); return true; }

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
            // The identity itself may not be used for ordinary operations (root, a system account, an
            // unverifiable profile). Resolution reports the precise cause; this is the fallback text for
            // a transport that refuses the same code, so both say the same thing and both name the way out.
            UserExecutionProblemCode.IdentityNotEligible => "This Server cannot execute ordinary file, terminal or Git operations as the authenticated OS account. Sign in with a regular host account (uid 1000 or higher).",
            // Eligible identity, refused boundary: in-process execution is restricted to the Server's own
            // account, and that refusal is a deployment choice the operator can fix or work around.
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
