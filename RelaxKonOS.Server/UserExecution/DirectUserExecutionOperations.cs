using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Files;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Runs one closed user-execution operation in the Server's own process.
/// </summary>
/// <remarks>
/// This is the single mapping from <see cref="UserExecutionOperationKind"/> to
/// <see cref="LocalFileService"/> used by both in-process paths — User Mode, which has always
/// executed directly, and <see cref="UserExecutionBackend.LocalIdentity"/>, which may only do so
/// while the effective identity is the Server's own. Sharing the mapping keeps the two from drifting
/// apart; it deliberately does not add any operation, and every caller must have established that
/// in-process execution is allowed before calling it.
/// </remarks>
internal static class DirectUserExecutionOperations
{
    public static async Task<object?> ExecuteAsync(LocalFileService direct, UserExecutionRequest request)
        => request.Operation switch
        {
            UserExecutionOperationKind.FileGetSpecialLocations => direct.GetSpecialLocations(),
            UserExecutionOperationKind.FileListDirectory => direct.GetDirectory(request.Path),
            UserExecutionOperationKind.FileGetInfo => direct.GetInfo(request.Path!),
            UserExecutionOperationKind.FileRead => ReadDirect(direct, request.Path!),
            UserExecutionOperationKind.FileWrite => await direct.WriteFileAsync(request.Path!, Bytes(request.ContentBase64!)),
            UserExecutionOperationKind.FileGetProperties => direct.GetProperties(request.Path!),
            UserExecutionOperationKind.FileSetUnixPermissions => direct.SetUnixPermissions(request.Path!, request.UnixMode!.Value),
            UserExecutionOperationKind.FileDelete => DeleteDirect(direct, request.Path!),
            UserExecutionOperationKind.FileRename => direct.Rename(request.Path!, request.NewName!),
            UserExecutionOperationKind.FileMove => direct.Move(request.Path!, request.DestinationPath!, request.Overwrite),
            UserExecutionOperationKind.FileCopy => direct.Copy(request.Path!, request.DestinationPath!, request.Overwrite),
            UserExecutionOperationKind.FileUpload => await direct.UploadAsync(request.Path!, request.FileName!, Bytes(request.ContentBase64!)),
            UserExecutionOperationKind.FileCreateDirectory => CreateDirect(direct, request.Path!),
            UserExecutionOperationKind.FileCreateStaging => CreateStagingDirect(direct, request.Path!),
            UserExecutionOperationKind.FileAppendStaging => await direct.AppendStagingAsync(request.Path!, request.Offset!.Value,
                request.ExpectedBytes!.Value, Bytes(request.ContentBase64!)),
            UserExecutionOperationKind.FileGetStagingLength => direct.StagingLength(request.Path!),
            UserExecutionOperationKind.FileDeleteStaging => DeleteStagingDirect(direct, request.Path!),
            UserExecutionOperationKind.FileCommitStaging => direct.CommitStagingFile(request.Path!, request.DestinationPath!),
            _ => throw new ArgumentException("Unsupported user-execution operation."),
        };

    private static FileReadResult ReadDirect(LocalFileService direct, string path)
    {
        var read = direct.OpenRead(path) ?? throw new FileNotFoundException("User-execution path not found.", path);
        using (read.Stream)
        using (var copy = new MemoryStream())
        {
            read.Stream.CopyTo(copy);
            if (copy.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new ContentTooLargeException();
            return new(Convert.ToBase64String(copy.ToArray()), read.FileName, read.ContentType);
        }
    }

    private static bool DeleteDirect(LocalFileService direct, string path) { direct.Delete(path); return true; }
    private static bool CreateDirect(LocalFileService direct, string path) { direct.CreateDirectory(path); return true; }
    private static bool CreateStagingDirect(LocalFileService direct, string path) { direct.CreateStagingFile(path); return true; }
    private static bool DeleteStagingDirect(LocalFileService direct, string path) => direct.DeleteStagingFile(path);
    private static MemoryStream Bytes(string content) => new(Convert.FromBase64String(content), writable: false);

    internal sealed record FileReadResult(string ContentBase64, string FileName, string ContentType);

    /// <summary>
    /// Derives from <see cref="IOException"/> so that existing callers which classify in-process
    /// failures as I/O errors keep their behaviour, while a caller that can produce a protocol result
    /// distinguishes "too large" from a generic I/O conflict.
    /// </summary>
    internal sealed class ContentTooLargeException() : IOException("File content is too large.");
}
