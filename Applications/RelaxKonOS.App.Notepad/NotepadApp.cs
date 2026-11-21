using System.Text.Json;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Notepad;

public sealed class NotepadApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    private static readonly IReadOnlyList<string> Extensions = LoadExtensions();

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.notepad"), "Notebook", "1.0.0", "📝",
        RequestedPermissions: [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite],
        SupportedFileExtensions: Extensions, SupportsExtensionlessFiles: true, SupportsTextFiles: true);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default) =>
        OpenEditorAsync(context, null, cancellationToken);

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default) =>
        OpenEditorAsync(context, path, cancellationToken);

    private async Task OpenEditorAsync(IExternalAppContext context, string? path, CancellationToken cancellationToken)
    {
        var preferences = context.TextEditorPreferences;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancellation = lifetime.Token;
        var labels = new NotepadText(context.SystemLanguage);
        var model = new NotepadViewModel(labels, ReadAsync, WriteAsync, preferences?.DefaultEncoding ?? "UTF-8", cancellation);
        IExternalAppWindowHandle owner;
        try
        {
            var view = new NotepadView { DataContext = model };
            owner = context.Windows.ShowWindow(model.Title, view, new Rect(160, 100, 820, 580), Manifest.IconGlyph);
            view.AttachWindowHeader(owner.Window);
            owner.Closed.Register(() =>
            {
                lifetime.Cancel();
                lifetime.Dispose();
                model.Dispose();
            });
        }
        catch
        {
            lifetime.Dispose();
            model.Dispose();
            throw;
        }

        owner.Window.CanCloseAsync = model.CanReplaceAsync;
        owner.Window.KeyDown += (_, e) =>
        {
            _ = WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control, model.NewDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('O'), RemoteKeyModifiers.Control, model.OpenDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control, model.SaveCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, model.SaveAsCommand);
        };

        model.RequestDiscardChangesAsync = () => owner.ConfirmAsync(labels["common.discard_title"],
            labels["notepad.discard_message"], labels["common.discard"]);
        model.RequestFileAsync = () => ChooseFileAsync(null);
        model.RequestSavePathAsync = ChooseFileAsync;
        if (preferences is not null)
        {
            model.SaveDefaultEncodingAsync = preferences.SetDefaultEncodingAsync;
            model.RequestEncodingActionAsync = () => preferences.PickEncodingActionAsync(owner);
            model.RequestEncodingAsync = () => preferences.PickEncodingAsync(owner, model.EncodingName);
        }
        model.RequestSettingsAsync = async () =>
        {
            try
            {
                await owner.ShowDialogAsync<bool>(labels["notepad.settings.title"], dialog =>
                {
                    model.CloseSettingsAction = () => dialog.Close(true);
                    return new NotepadSettingsView { DataContext = model };
                }, new Size(420, 300));
            }
            finally { model.CloseSettingsAction = null; }
        };

        if (path is not null) await model.OpenPathAsync(path);

        async Task<string?> ChooseFileAsync(string? fileName)
        {
            await RequireAsync(AppPermissions.ServerFilesRead, cancellation);
            if (fileName is not null) await RequireAsync(AppPermissions.ServerFilesWrite, cancellation);
            var result = await context.ServerFiles.PickFileAsync(owner, new(
                labels[fileName is null ? "notepad.open_remote_file" : "notepad.save_remote_file"], labels["notepad.text_file_filter"],
                Extensions.Select(extension => "*" + extension).ToArray(), IncludeExtensionlessFiles: true, SuggestedFileName: fileName));
            CheckResult(result.Status, fileName is null ? "notepad.status.connect_before_open" : "notepad.status.connect_before_save");
            return result.Path;
        }

        async Task<byte[]?> ReadAsync(string filePath, CancellationToken token)
        {
            await RequireAsync(AppPermissions.ServerFilesRead, token);
            var result = await context.ServerFiles.ReadAsync(filePath, token);
            CheckResult(result.Status, "notepad.status.connect_before_open");
            return result.Content;
        }

        async Task WriteAsync(string filePath, byte[] content, CancellationToken token)
        {
            await RequireAsync(AppPermissions.ServerFilesWrite, token);
            CheckResult(await context.ServerFiles.WriteAsync(filePath, content, token), "notepad.status.connect_before_save");
        }

        async Task RequireAsync(string permission, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var status = context.Permissions.GetStatus(permission);
            if (status == AppPermissionStatus.Undecided)
                status = await context.Permissions.RequestAsync(permission, token);
            if (status != AppPermissionStatus.Granted) throw new UnauthorizedAccessException();
        }

        void CheckResult(AppCapabilityResult status, string unavailableMessage)
        {
            if (status == AppCapabilityResult.PermissionDenied) throw new UnauthorizedAccessException();
            if (status != AppCapabilityResult.Succeeded) throw new IOException(labels[unavailableMessage]);
        }
    }

    private static IReadOnlyList<string> LoadExtensions()
    {
        using var stream = typeof(NotepadApp).Assembly.GetManifestResourceStream("Notepad.Manifest.json")!;
        using var manifest = JsonDocument.Parse(stream);
        return manifest.RootElement.GetProperty("supportedFileExtensions").Deserialize<string[]>()!;
    }
}
