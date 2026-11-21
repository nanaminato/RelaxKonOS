using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.CodeEditor;

public interface IEditorWorkspaceFiles
{
    Task<ServerDirectory> GetDirectoryAsync(string path, CancellationToken ct);
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct);
    Task WriteFileAsync(string path, byte[] content, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task<string> RenameAsync(string path, string newName, CancellationToken ct);
}
