using Size = RelaxKonOS.Core.Primitives.Size;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.ProxyServer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ProxyServer;

public sealed class ProxyServerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.proxy-server"), "Proxy Server", "1.0.0", "⇄", "sing-box proxy server",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerProxyServerExport],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.ProxyServer]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        var client = (IProxyServerClient?)context.Services.GetService(typeof(IProxyServerClient));
        if (session?.State != AuthSessionState.Authenticated || client is null) return;
        var vm = new ProxyServerViewModel(client);
        var view = new ProxyServerWorkspace { DataContext = vm };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.proxy-server.display_name"), view, new Rect(90, 55, 1040, 780), Manifest.IconGlyph);
        vm.ConfirmApplyAsync = () => ConfirmAsync("proxy_server.apply_warning");
        window.CanCloseAsync = () => vm.IsBusy || vm.HasUnsavedChanges ? ConfirmAsync("proxy_server.close_warning") : Task.FromResult(true);
        var login = session.CurrentSession;
        EventHandler<AuthSessionStateChangedEventArgs> changed = (_, _) =>
        {
            if (!ReferenceEquals(login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
                Avalonia.Threading.Dispatcher.UIThread.Post(vm.InvalidateSession);
        };
        session.StateChanged += changed;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= changed; vm.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            var read = await PermissionAsync(AppPermissions.ServerProxyRead);
            if (!read) return;
            await vm.RefreshCommand.ExecuteAsync(null);
            var controller = session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller;
            if (!controller || !ReferenceEquals(login, session.CurrentSession)) return;
            vm.CanManage = await PermissionAsync(AppPermissions.ServerProxyManage);
            vm.CanExport = await PermissionAsync(AppPermissions.ServerProxyServerExport);
        }
        async Task<bool> PermissionAsync(string permission)
        {
            var status = context.Permissions.GetStatus(permission);
            if (status == AppPermissionStatus.Undecided) status = await context.Permissions.RequestAsync(permission);
            return status == AppPermissionStatus.Granted && ReferenceEquals(login, session.CurrentSession);
        }
        async Task<bool> ConfirmAsync(string key)
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("application.relaxkonos.proxy-server.display_name"), dialog => new ConfirmDialogView
            { DataContext = new ConfirmDialogViewModel(LocalizedText.Get(key), value => { accepted = value; dialog.Close(value); }) }, new Size(520, 260));
            return accepted;
        }
    }
}
