using RelaxKonOS.App.CodeEditor;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;

namespace RelaxKonOS.Client.Apps.CodeEditor;

internal class ExplorerEditorWorkspaceFiles(IExplorerClient files) : IEditorWorkspaceFiles
{
    public static IEditorWorkspaceFiles? Wrap(IExplorerClient? files) => files switch
    {
        IEditorFileWriter writer => new Conditional(files, writer),
        not null => new ExplorerEditorWorkspaceFiles(files),
        _ => null,
    };

    public async Task<ServerDirectory> GetDirectoryAsync(string path, CancellationToken ct)
    {
        var directory = await files.GetDirectoryAsync(path, ct);
        return new(directory.Path, directory.Name,
            directory.Directories.Select(item => new ServerDirectoryEntry(item.Path, item.Name, true))
                .Concat(directory.Files.Select(item => new ServerDirectoryEntry(item.Path, item.Name, false))).ToArray());
    }

    public Task<byte[]?> ReadFileAsync(string path, CancellationToken ct) => files.ReadFileAsync(path, ct);
    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct) => files.WriteFileAsync(path, content, ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => files.CreateDirectoryAsync(path, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => files.DeleteAsync(path, ct);
    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct) =>
        (await files.RenameAsync(path, newName, ct)).Path;

    private sealed class Conditional(IExplorerClient files, IEditorFileWriter writer)
        : ExplorerEditorWorkspaceFiles(files), IEditorFileWriter
    {
        public Task<string> WriteEditorFileAsync(string path, byte[] content, string? expectedVersion, CancellationToken ct) =>
            writer.WriteEditorFileAsync(path, content, expectedVersion, ct);
    }
}
