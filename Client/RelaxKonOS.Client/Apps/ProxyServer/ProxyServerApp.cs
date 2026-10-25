using Size = RelaxKonOS.Core.Primitives.Size;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.Client.Apps.ProxyServer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ProxyServer;

/// <summary>Built-in sing-box proxy server console. Publishing always runs on the Server, never in the client.</summary>
public sealed class ProxyServerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.proxy-server"), "Proxy Server", "1.0.0", "⇄", "sing-box proxy server",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerProxyServerExport],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.ProxyServer]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow,
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/proxy-server.png");

    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        var client = (IProxyServerClient?)context.Services.GetService(typeof(IProxyServerClient));
        if (session?.State != AuthSessionState.Authenticated || client is null) return;
        var vm = new ProxyServerViewModel(client);
        var view = new ProxyServerWorkspace(vm);
        view.OpenComponentCenter = () =>
        {
            if (session.State == AuthSessionState.Authenticated && session.ServiceId is { } host)
                context.Activations.Activate(RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterNavigation.Component(
                    host, Manifest.Id.Value, "sing-box"));
        };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.proxy-server.display_name"), view,
            new Rect(80, 45, 1060, 800), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        RelaxKonOS.WindowManager.ModalDialog<bool>? activeEditor = null;
        view.ShowEditorAsync = (title, editor) => RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.PresentAsync(context, window, title, editor, () => vm.IsBusy, active => activeEditor = active);

        vm.ConfirmAsync = (title, message) => ConfirmAsync(title.Resolve(), message.Resolve());
        window.CanCloseAsync = () => vm.IsBusy || vm.HasUnsavedChanges
            ? ConfirmAsync(LocalizedText.Get("common.discard_title"), LocalizedText.Get("proxy_server.close_warning"))
            : Task.FromResult(true);
        var login = session.CurrentSession;
        EventHandler<AuthSessionStateChangedEventArgs> sessionChanged = (_, _) =>
        {
            if (!ReferenceEquals(login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    vm.InvalidateSession();
                });
        };
        session.StateChanged += sessionChanged;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= sessionChanged;
            vm.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            var read = await PermissionAsync(AppPermissions.ServerProxyRead);
            vm.CanRead = read;
            if (!read) return;
            await vm.RefreshCommand.ExecuteAsync(null);
            // A host that reports the capability as unsupported can never accept a mutation.
            if (vm.Snapshot?.Capabilities.Supported != true) return;
            var controller = session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller;
            if (!controller || !ReferenceEquals(login, session.CurrentSession)) return;
            vm.CanManage = await PermissionAsync(AppPermissions.ServerProxyManage);
            vm.CanExport = await PermissionAsync(AppPermissions.ServerProxyServerExport);
            if (vm.SelectedCredential is null && vm.Credentials.Count > 0) vm.SelectedCredential = vm.Credentials[0];
        }
        async Task<bool> PermissionAsync(string permission)
        {
            var status = context.Permissions.GetStatus(permission);
            if (status == AppPermissionStatus.Undecided) status = await context.Permissions.RequestAsync(permission);
            return status == AppPermissionStatus.Granted && ReferenceEquals(login, session.CurrentSession);
        }
        Task<TResult?> ShowOwnedAsync<TResult>(string title, Func<RelaxKonOS.WindowManager.ModalDialog<TResult>, Avalonia.Controls.Control> factory, RelaxKonOS.Core.Primitives.Size size)
            => activeEditor is { } editor ? editor.ShowDialogAsync(title, factory, size) : context.ShowDialogAsync(window, title, factory, size);

        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await ShowOwnedAsync<bool>( title, dialog => new ConfirmDialogView
            { DataContext = new ConfirmDialogViewModel(message, value => { accepted = value; dialog.Close(value); }) }, new Size(560, 280));
            return accepted;
        }
    }

    /// <summary>Used by the window-preview checks to render the real workspace without a window host.</summary>
    internal static Avalonia.Controls.Control CreateView(ProxyServerViewModel vm) => new ProxyServerWorkspace(vm);
}
