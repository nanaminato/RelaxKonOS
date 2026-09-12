using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Built-in SMB administration entry point. Unsupported protocols deliberately have no card or feature flag.</summary>
public sealed class FileServicesApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.file-services"), "File Services", "1.0.0", "🗄", "Manage the host SMB control plane",
        [AppPermissions.ServerFileServicesRead, AppPermissions.ServerFileServicesManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFileServicesClient)) as IRemoteFileServicesClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("file_services.title"), new FileServicesLoginRequiredView(), new Rect(180, 160, 440, 160), Manifest.IconGlyph, false, false, false);
            return;
        }
        var vm = new FileServicesViewModel(client, context.Permissions);
        vm.Installation = InstallationPanel.Create(context, InstallationServiceId.Smb, "relaxkonos.file-services", () => vm.RefreshCommand.ExecuteAsync(null));
        var window = context.ShowWindow(LocalizedText.Get("file_services.title"), InstallationPanel.Wrap(new FileServicesWorkspace(vm), vm.Installation), new Rect(90, 80, 960, 720), Manifest.IconGlyph);
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        vm.RequestHostAdministratorPasswordAsync = () => context.WindowManager.ShowSystemDialogAsync<string?>(
            LocalizedText.Get("file_services.host_password"),
            dialog => new FileServicesPasswordDialogView(dialog, LocalizedText.Get("file_services.host_password_message")), new Size(460, 230));
        vm.RequestSambaPasswordAsync = () => FileServicesDialogs.RequestPasswordAsync(context, window, LocalizedText.Get("file_services.samba_password"));
        vm.ShowShareEditorAsync = editing => FileServicesDialogs.ShowShareEditorAsync(context, window, vm, editing);
        vm.ShowSharePathPickerAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string?>(window, LocalizedText.Get("file_services.select_folder"), dialog =>
            {
                var picker = new ExplorerViewModel(files, new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, PickerBounds(window));
        vm.ConfirmDeleteAsync = name => FileServicesDialogs.ConfirmDeleteAsync(context, window, name);
        _ = vm.StartAsync();
    }

    private static Rect PickerBounds(RelaxKonOS.WindowManager.ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        var width = Math.Min(820, Math.Max(480, bounds.Width - 40));
        var height = Math.Min(600, Math.Max(340, bounds.Height - 48));
        return new Rect(bounds.X + Math.Max(20, (bounds.Width - width) / 2),
            bounds.Y + Math.Max(24, (bounds.Height - height) / 2), width, height);
    }
}
