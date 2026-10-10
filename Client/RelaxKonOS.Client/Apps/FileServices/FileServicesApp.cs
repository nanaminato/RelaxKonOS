using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Built-in SMB administration entry point. Unsupported protocols deliberately have no card or feature flag.</summary>
public sealed class FileServicesApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.file-services"), "File Services", "1.0.0", "🗄", "Manage the host SMB control plane",
        [AppPermissions.ServerFileServicesRead, AppPermissions.ServerFileServicesManage], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.FileServices]), InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFileServicesClient)) as IRemoteFileServicesClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("file_services.title"), new FileServicesLoginRequiredView(), new Rect(180, 160, 440, 160), Manifest.IconGlyph, false, false, false);
            return;
        }
        var windowService = session.ServiceId;
        var windowUser = session.CurrentUser;
        var windowSession = session.CurrentSession;
        bool IsWindowSessionCurrent() => session.State == AuthSessionState.Authenticated
            && session.ServiceId == windowService && windowUser is not null && windowSession is not null
            && ReferenceEquals(session.CurrentUser, windowUser) && ReferenceEquals(session.CurrentSession, windowSession);
        var vm = new FileServicesViewModel(client, context.Permissions, IsWindowSessionCurrent);
        vm.Installation = InstallationPanel.Create(context, InstallationServiceId.Smb, "relaxkonos.file-services", () => vm.RefreshCommand.ExecuteAsync(null));
        var workspace = new FileServicesWorkspace(vm);
        void SessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
        {
            void Check() { if (!IsWindowSessionCurrent()) vm.InvalidateWindowSession(); }
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Check();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Check);
        }
        workspace.AttachedToVisualTree += (_, _) =>
        {
            session.StateChanged -= SessionChanged;
            session.StateChanged += SessionChanged;
            if (!IsWindowSessionCurrent()) vm.InvalidateWindowSession();
        };
        workspace.DetachedFromVisualTree += (_, _) => session.StateChanged -= SessionChanged;
        var window = context.ShowWindow(LocalizedText.Get("file_services.title"), InstallationPanel.Wrap(workspace, vm.Installation), new Rect(90, 80, 960, 720), Manifest.IconGlyph);
        vm.RequestHostAdministratorCredentialsAsync = async error =>
        {
            var memory = UsageMemoryStore.Capture(context);
            var credentials = await context.WindowManager.ShowSystemDialogAsync<HostAdministratorCredentials?>(
                LocalizedText.Get("file_services.host_password"),
                dialog => new HostAdministratorCredentialsDialogView(dialog, LocalizedText.Get("file_services.host_password_message"), error,
                    memory.Administrator ?? string.Empty), new Size(460, 250));
            return memory.IsCurrent ? credentials : null;
        };
        vm.RequestSambaPasswordAsync = () => FileServicesDialogs.RequestPasswordAsync(context, window, LocalizedText.Get("file_services.samba_password"));
        vm.ShowShareEditorAsync = editing => FileServicesDialogs.ShowShareEditorAsync(context, window, vm, editing);
        vm.ConfirmDeleteAsync = name => FileServicesDialogs.ConfirmDeleteAsync(context, window, name);
        _ = vm.StartAsync();
    }

}
