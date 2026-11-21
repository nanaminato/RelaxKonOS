using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.App.CodeEditor;

public sealed class SdkEditorWorkspaceFiles(IServerFiles files, Func<string, string> localize,
    IAppPermissionScope? permissions = null) : IEditorWorkspaceFiles, IEditorFileWriter
{
    public async Task<ServerDirectory> GetDirectoryAsync(string path, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesRead, ct);
        var result = await files.ListDirectoryAsync(path, ct);
        RequireSuccess(result.Status);
        return result.Directory!;
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesRead, ct);
        var result = await files.ReadAsync(path, ct);
        RequireSuccess(result.Status);
        return result.Content;
    }

    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesWrite, ct);
        RequireSuccess(await files.WriteAsync(path, content, ct));
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesWrite, ct);
        RequireSuccess(await files.CreateDirectoryAsync(path, ct));
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesWrite, ct);
        RequireSuccess(await files.DeleteAsync(path, ct));
    }

    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesWrite, ct);
        var result = await files.RenameAsync(path, newName, ct);
        RequireSuccess(result.Status);
        return result.Path!;
    }

    public async Task<string> WriteEditorFileAsync(string path, byte[] content, string? expectedVersion, CancellationToken ct)
    {
        await RequireGrantAsync(AppPermissions.ServerFilesWrite, ct);
        var result = await files.WriteConditionalAsync(path, content, expectedVersion, ct);
        if (result.Status == AppCapabilityResult.Conflict)
            throw new EditorWriteConflictException(localize("git.editor.external_changes"));
        RequireSuccess(result.Status);
        return result.Version!;
    }

    internal async Task RequireGrantAsync(string permission, CancellationToken ct)
    {
        if (permissions?.GetStatus(permission) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(permission, ct);
        if (permissions is not null && !permissions.IsGranted(permission))
            RequireSuccess(AppCapabilityResult.PermissionDenied);
    }

    private void RequireSuccess(AppCapabilityResult status)
    {
        if (status == AppCapabilityResult.Succeeded) return;
        throw new InvalidOperationException(status == AppCapabilityResult.Unavailable
            ? localize("explorer.error.not_signed_in")
            : status == AppCapabilityResult.PermissionDenied ? localize("code_editor.permission_denied") : status.ToString());
    }
}
