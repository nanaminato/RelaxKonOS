using Avalonia.Controls;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Squid.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.Squid;

/// <summary>Built-in Linux server HTTP/CONNECT gateway console.</summary>
public sealed class SquidApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.squid"), "Squid", "1.0.0", "⇥", "HTTP and HTTPS forward proxy gateway",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage],
        ServerRequirements: new ApplicationServerRequirements(Platforms: [ApplicationPlatformNames.Linux], Capabilities: [ServerCapabilities.GatewayProxy]),
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/proxy.png", InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        var client = (ISquidClient?)context.Services.GetService(typeof(ISquidClient));
        if (session?.State != AuthSessionState.Authenticated || client is null) return;
        var vm = new SquidViewModel(client);
        var view = new SquidWorkspace(vm);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.squid.display_name"), view,
            new Rect(90, 55, 960, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        vm.ConfirmApplyAsync = () => ConfirmAsync("squid.apply_title", "squid.apply_message");
        vm.ConfirmReloadAsync = () => ConfirmAsync("common.discard_title", "squid.reload_message");
        window.CanCloseAsync = () => vm.IsBusy || vm.HasPending || vm.HasUnsavedChanges
            ? ConfirmAsync("common.discard_title", "squid.close_message") : Task.FromResult(true);
        var login = session.CurrentSession;
        EventHandler<AuthSessionStateChangedEventArgs> sessionChanged = (_, _) =>
        {
            if (!ReferenceEquals(login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
                Avalonia.Threading.Dispatcher.UIThread.Post(vm.InvalidateSession);
        };
        session.StateChanged += sessionChanged;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= sessionChanged; vm.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            var read = context.Permissions.GetStatus(AppPermissions.ServerProxyRead);
            if (read == AppPermissionStatus.Undecided) read = await context.Permissions.RequestAsync(AppPermissions.ServerProxyRead);
            var manage = context.Permissions.GetStatus(AppPermissions.ServerProxyManage);
            if (manage == AppPermissionStatus.Undecided) manage = await context.Permissions.RequestAsync(AppPermissions.ServerProxyManage);
            vm.CanRead = read == AppPermissionStatus.Granted;
            vm.CanManage = manage == AppPermissionStatus.Granted && session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller;
            if (vm.CanRead) await vm.RefreshCommand.ExecuteAsync(null);
        }
        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get(title), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(LocalizedText.Get(message), value => { accepted = value; dialog.Close(value); })
            }, new Size(520, 260));
            return accepted;
        }
    }

    /// <summary>Used by the window-preview checks to render the real workspace without a window host.</summary>
    internal static Control CreateView(SquidViewModel vm) => new SquidWorkspace(vm);
}
