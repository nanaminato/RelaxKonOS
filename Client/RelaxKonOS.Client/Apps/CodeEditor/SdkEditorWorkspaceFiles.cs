using RelaxKonOS.App.CodeEditor;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.CodeEditor;

internal sealed class SdkEditorWorkspaceFiles(IServerFiles files) : IEditorWorkspaceFiles, IEditorFileWriter
{
    public async Task<ServerDirectory> GetDirectoryAsync(string path, CancellationToken ct)
    {
        var result = await files.ListDirectoryAsync(path, ct);
        RequireSuccess(result.Status);
        return result.Directory!;
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct)
    {
        var result = await files.ReadAsync(path, ct);
        RequireSuccess(result.Status);
        return result.Content;
    }

    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct) =>
        RequireSuccess(await files.WriteAsync(path, content, ct));

    public async Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        RequireSuccess(await files.CreateDirectoryAsync(path, ct));

    public async Task DeleteAsync(string path, CancellationToken ct) =>
        RequireSuccess(await files.DeleteAsync(path, ct));

    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct)
    {
        var result = await files.RenameAsync(path, newName, ct);
        RequireSuccess(result.Status);
        return result.Path!;
    }

    public async Task<string> WriteEditorFileAsync(string path, byte[] content, string? expectedVersion, CancellationToken ct)
    {
        var result = await files.WriteConditionalAsync(path, content, expectedVersion, ct);
        RequireSuccess(result.Status);
        return result.Version!;
    }

    private static void RequireSuccess(AppCapabilityResult status)
    {
        if (status == AppCapabilityResult.Succeeded) return;
        throw new InvalidOperationException(status == AppCapabilityResult.Unavailable
            ? LocalizedText.Get("explorer.error.not_signed_in")
            : status.ToString());
    }
}
