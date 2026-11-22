using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.FileServices.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.FileServices;

public sealed class FileServicesPackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.file-services"), "File Services", "1.0.0", "🗄",
        "Manage the host SMB control plane",
        [AppPermissions.ServerFileServicesRead, AppPermissions.ServerFileServicesManage,
            AppPermissions.ServerComponentsRead, AppPermissions.ServerFilesRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.FileServices]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => FileServicesLocalization.Resolve(language, key);
        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent || context.ServerFiles.IsSshSession)
        {
            context.Windows.ShowWindow(Label("file_services.title"),
                new TextBlock { Text = Label("file_services.login_required"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) },
                new Rect(180, 160, 440, 160), Manifest.IconGlyph, false, false, false);
            return;
        }

        if (context.Permissions.GetStatus(AppPermissions.ServerFileServicesRead) == AppPermissionStatus.Undecided)
            await context.Permissions.RequestAsync(AppPermissions.ServerFileServicesRead, cancellationToken);
        if (context.Permissions.IsGranted(AppPermissions.ServerFileServicesRead)
            && context.Permissions.GetStatus(AppPermissions.ServerFileServicesManage) == AppPermissionStatus.Undecided)
            await context.Permissions.RequestAsync(AppPermissions.ServerFileServicesManage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var model = new FileServicesViewModel(context.FileServices, context.Permissions, () => connection.IsCurrent, Label);
        IExternalAppWindowHandle? handle = null;
        var workspace = new FileServicesWorkspace(model,
            () => context.ComponentDependencies?.CreateSettingsView(handle!) ?? new ContentControl());
        var owner = context.Windows.ShowWindow(Label("file_services.title"), workspace, new Rect(90, 80, 960, 720), Manifest.IconGlyph);
        handle = owner;
        var closed = owner.Closed;
        workspace.AttachWindowHeader(owner.Window);

        if (context.FileServicesAdministratorPrompt is { } prompt)
            model.RequestHostAdministratorCredentialsAsync = error => prompt.RequestAsync(owner, error);
        model.RequestSambaPasswordAsync = () => owner.ShowDialogAsync<string?>(Label("file_services.samba_password"),
            dialog => new FileServicesPasswordDialogView(dialog, Label, null), new Size(460, 160));
        model.ConfirmDeleteAsync = name => owner.ShowDialogAsync<bool>(Label("file_services.delete"),
            dialog => new FileServicesDeleteDialogView(name, dialog, Label), new Size(440, 220));
        model.ShowShareEditorAsync = ShowShareEditorAsync;

        void Dispatch(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
            void Apply() { if (!closed.IsCancellationRequested) action(); }
        }

        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => Dispatch(() =>
        {
            owner.Window.Title = Label("file_services.title");
            model.RefreshLocalization();
        });

        language.LanguageChanged += LanguageChanged;
        var observation = connection.ObserveInvalidation(() => Dispatch(model.InvalidateWindowSession));
        closed.Register(() =>
        {
            observation.Dispose();
            language.LanguageChanged -= LanguageChanged;
        });
        await model.StartAsync();

        async Task ShowShareEditorAsync(bool editing)
        {
            try
            {
                var title = Label(editing ? "file_services.share_edit_title" : "file_services.share_new_title");
                await owner.ShowDialogAsync<bool>(title, dialog =>
                {
                    model.ConfirmSharePathAsync = path => dialog.ShowDialogAsync<bool>(Label("file_services.path_warning_title"),
                        warning => new FileServicesPathWarningDialogView(path, warning, Label));
                    model.ShowSharePathPickerAsync = async () =>
                    {
                        if (context.Permissions.GetStatus(AppPermissions.ServerFilesRead) == AppPermissionStatus.Undecided)
                            await context.Permissions.RequestAsync(AppPermissions.ServerFilesRead, closed);
                        if (dialog.Window is not { } modal) return null;
                        var picked = await context.ServerFiles.PickAsync(owner,
                            new(Label("file_services.select_folder"), ServerFilePickerMode.SelectFolder), modal);
                        return picked.Status == AppCapabilityResult.Succeeded ? picked.Path : null;
                    };
                    return new FileServicesShareDialogView(model, dialog, editing, (draft, busy, changed) =>
                        draft.CanCancelAsync = async () =>
                        {
                            if (busy()) return false;
                            if (!changed()) return true;
                            return await owner.ConfirmNestedAsync(draft, Label("common.discard_title"),
                                Label("common.discard_message"), Label("common.discard")) && !busy();
                        });
                }, new Size(720, 700));
            }
            finally
            {
                model.ConfirmSharePathAsync = null;
                model.ShowSharePathPickerAsync = null;
            }
        }
    }
}
