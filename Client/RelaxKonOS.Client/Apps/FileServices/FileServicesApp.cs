using RelaxKonOS.Client.Apps.FileServices.Views;
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
        var window = context.ShowWindow(LocalizedText.Get("file_services.title"), new FileServicesWorkspace(vm), new Rect(90, 80, 960, 720), Manifest.IconGlyph);
        vm.RequestHostAdministratorPasswordAsync = () => FileServicesDialogs.RequestPasswordAsync(context, window, LocalizedText.Get("file_services.host_password"));
        vm.RequestSambaPasswordAsync = () => FileServicesDialogs.RequestPasswordAsync(context, window, LocalizedText.Get("file_services.samba_password"));
        vm.ShowShareEditorAsync = editing => FileServicesDialogs.ShowShareEditorAsync(context, window, vm, editing);
        vm.ConfirmDeleteAsync = name => FileServicesDialogs.ConfirmDeleteAsync(context, window, name);
        _ = vm.StartAsync();
    }
}
