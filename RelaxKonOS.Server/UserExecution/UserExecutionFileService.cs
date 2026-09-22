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
        var result = Run<FileReadResult>(UserExecutionOperationKind.FileRead, path: path);
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

    private T Run<T>(UserExecutionOperationKind operation, string? path = null, string? destinationPath = null, string? newName = null,
        string? fileName = null, bool overwrite = false, string? content = null, int? unixMode = null)
        => RunAsync<T>(operation, path, destinationPath, newName, fileName, overwrite, content, unixMode).GetAwaiter().GetResult();

    private async Task<T> RunAsync<T>(UserExecutionOperationKind operation, string? path = null, string? destinationPath = null, string? newName = null,
        string? fileName = null, bool overwrite = false, string? content = null, int? unixMode = null)
    {
        var principal = http.HttpContext?.User ?? throw new InvalidOperationException("User execution requires an authenticated HTTP request.");
        var context = contexts.Resolve(principal);
        var request = new UserExecutionRequest(context.Identity, operation, path, destinationPath, newName, fileName, overwrite, content, unixMode, Guid.NewGuid());
        if (mode.Mode == ServerMode.User)
        {
            var validation = new DirectUserExecutionService(mode).Validate(context, request);
            Throw(validation);
            return await DirectAsync<T>(operation, path, destinationPath, newName, fileName, overwrite, content, unixMode);
        }
        var result = await transport.ExecuteAsync(request, http.HttpContext?.RequestAborted ?? CancellationToken.None);
        Throw(result);
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64!), RelaxKonOSJsonOptions.Default)!; }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new InvalidOperationException("User-execution Helper returned an invalid result."); }
    }

    private async Task<T> DirectAsync<T>(UserExecutionOperationKind operation, string? path, string? destinationPath, string? newName,
        string? fileName, bool overwrite, string? content, int? unixMode) => (T)(object)(operation switch
    {
        UserExecutionOperationKind.FileGetSpecialLocations => direct.GetSpecialLocations(),
        UserExecutionOperationKind.FileListDirectory => direct.GetDirectory(path),
        UserExecutionOperationKind.FileGetInfo => direct.GetInfo(path!),
        UserExecutionOperationKind.FileRead => ReadDirect(path!),
        UserExecutionOperationKind.FileWrite => await direct.WriteFileAsync(path!, Bytes(content!)),
        UserExecutionOperationKind.FileGetProperties => direct.GetProperties(path!),
        UserExecutionOperationKind.FileSetUnixPermissions => direct.SetUnixPermissions(path!, unixMode!.Value),
        UserExecutionOperationKind.FileDelete => DeleteDirect(path!),
        UserExecutionOperationKind.FileRename => direct.Rename(path!, newName!),
        UserExecutionOperationKind.FileMove => direct.Move(path!, destinationPath!, overwrite),
        UserExecutionOperationKind.FileCopy => direct.Copy(path!, destinationPath!, overwrite),
        UserExecutionOperationKind.FileUpload => await direct.UploadAsync(path!, fileName!, Bytes(content!)),
        UserExecutionOperationKind.FileCreateDirectory => CreateDirect(path!),
        _ => throw new ArgumentException("Unsupported user-execution operation."),
    });

    private FileReadResult ReadDirect(string path)
    {
        var read = direct.OpenRead(path) ?? throw new FileNotFoundException("User-execution path not found.", path);
        using (read.Stream)
        using (var copy = new MemoryStream())
        {
            read.Stream.CopyTo(copy);
            if (copy.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new IOException("File content is too large.");
            return new(Convert.ToBase64String(copy.ToArray()), read.FileName, read.ContentType);
        }
    }
    private bool DeleteDirect(string path) { direct.Delete(path); return true; }
    private bool CreateDirect(string path) { direct.CreateDirectory(path); return true; }
    private static MemoryStream Bytes(string content) => new(Convert.FromBase64String(content), writable: false);
    private static async Task<string> ReadContentAsync(Stream content, CancellationToken cancellationToken)
    { await using var copy = new MemoryStream(); await content.CopyToAsync(copy, cancellationToken); if (copy.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new IOException("File content is too large."); return Convert.ToBase64String(copy.ToArray()); }
    private static void Throw(UserExecutionResult result)
    {
        if (result.Success) return;
        throw result.ProblemCode switch
        {
            UserExecutionProblemCode.AccessDenied => new UnauthorizedAccessException("Access denied for the authenticated OS user."),
            UserExecutionProblemCode.NotFound => new FileNotFoundException("User-execution path not found."),
            UserExecutionProblemCode.InvalidRequest or UserExecutionProblemCode.ContentTooLarge => new ArgumentException("Invalid user-execution file request."),
            UserExecutionProblemCode.Conflict => new IOException("User-execution file operation failed."),
            _ => new InvalidOperationException("User-execution Helper is unavailable."),
        };
    }
    private sealed record FileReadResult(string ContentBase64, string FileName, string ContentType);
}
