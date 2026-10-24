using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Apps.Proxy.Views;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.Proxy;

/// <summary>Built-in host-global proxy workspace. All actions traverse the RelaxKonOS API.</summary>
public sealed class ProxyManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.proxy"), "Proxy Manager", "1.0.0", "⇄", "Manage the host Proxy runtime and recovery state",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerProxyTunManage], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Proxy]), InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var repository = context.Services.GetService(typeof(IProxyRepository)) as IProxyRepository;
        if (session is null || repository is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.proxy.display_name"), new TextBlock { Text = LocalizedText.Get("proxy.login_required"), Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false); return;
        }
        var systemMonitor = context.Services.GetService(typeof(ITaskManagerClient)) as ITaskManagerClient;
        // Do not start a proxy operation with the initial Undecided permission snapshot.
        // ApplicationManager presents prompts after Activate returns, so this workspace owns
        // the first request and waits for its decision before enabling server actions.
        var vm = new ProxyManagerViewModel(repository, canManage: false, canManageTun: false, systemMonitor);
        // Runtime installation, update and removal belong to the managed-runtime page: it negotiates a
        // concrete catalog release, freezes a Host plan and executes through the shared installation ledger.
        var runtime = context.Services.GetService(typeof(IMihomoRuntimeCatalogClient)) is IMihomoRuntimeCatalogClient catalog
            ? new ProxyServerRuntimeViewModel(catalog) : null;
        var view = runtime is null ? new ProxyManagerWorkspace(vm) : new ProxyManagerWorkspace(vm, runtime);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.proxy.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        RelaxKonOS.WindowManager.ModalDialog<bool>? activeEditor = null;
        view.SetRuntimeEditor((title, editor) => RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.PresentAsync(context, window, title, editor, () => runtime?.IsBusy == true, active => activeEditor = active));
        vm.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        if (runtime is not null)
        {
            runtime.ConfirmAsync = async (title, message) => await ConfirmAsync(title.Resolve(), message.Resolve());
            runtime.PasswordPromptAsync = () => ShowOwnedAsync<string?>(
                LocalizedText.Get("installation.elevation_title"),
                dialog => new FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")),
                new RelaxKonOS.Core.Primitives.Size(460, 230));
        }
        vm.RequestSystemProxySubscriptionDownloadAsync = async () =>
        {
            var useSystemProxy = false;
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get("proxy.subscription_system_proxy.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Get("proxy.subscription_system_proxy.message"),
                    result => { useSystemProxy = result; dialog.Close(result); },
                    LocalizedText.Get("proxy.subscription_system_proxy.confirm")),
            }, new RelaxKonOS.Core.Primitives.Size(460, 220));
            return useSystemProxy;
        };
        vm.RequestPortChangeRestartAsync = async () =>
        {
            var restartNow = false;
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get("proxy.port_restart.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Get("proxy.port_restart.message"),
                    result => { restartNow = result; dialog.Close(result); },
                    LocalizedText.Get("proxy.port_restart.now"),
                    LocalizedText.Get("proxy.port_restart.later")),
            }, new RelaxKonOS.Core.Primitives.Size(460, 220));
            return restartNow;
        };
        vm.ShowRuntimeSubscriptionWindow = () =>
        {
            _ = context.ShowDialogAsync<bool>(window, LocalizedText.Get("proxy.subscription_content"), dialog =>
                new ProxySubscriptionContentDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(840, 560));
        };
        vm.OpenNetworkSettingsDialogAsync = async isTun =>
        {
            vm.SelectNetworkSettingsPageForDialog(isTun);
            await vm.LoadSystemProxyHostOptionsAsync();
            await context.ShowDialogAsync<bool>(window, vm.NetworkSettingsDialogTitle, dialog =>
                new ProxyNetworkSettingsDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(600, isTun ? 700 : 650));
        };
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed;
            runtime?.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAfterPermissionDecisionAsync();

        async Task InitializeAfterPermissionDecisionAsync()
        {
            var read = await RequestIfUndecidedAsync(AppPermissions.ServerProxyRead);
            var manage = await RequestIfUndecidedAsync(AppPermissions.ServerProxyManage);
            var tun = await RequestIfUndecidedAsync(AppPermissions.ServerProxyTunManage);
            vm.SetPermissions(manage, tun);
            if (runtime is not null) { runtime.CanRead = read; runtime.CanManage = read && manage; }

            // A declined read grant still permits the workspace to render its normal API
            // authorization result, but it never enables a mutation solely because the prompt
            // happened to complete after activation.
            _ = read;
            await vm.StartAsync();
        }

        Task<TResult?> ShowOwnedAsync<TResult>(string title, Func<RelaxKonOS.WindowManager.ModalDialog<TResult>, Avalonia.Controls.Control> factory, RelaxKonOS.Core.Primitives.Size size)
            => activeEditor is { } editor ? editor.ShowDialogAsync(title, factory, size) : context.ShowDialogAsync(window, title, factory, size);

        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await ShowOwnedAsync<bool>( title, dialog => new ConfirmDialogView
            { DataContext = new ConfirmDialogViewModel(message, value => { accepted = value; dialog.Close(value); }) },
                new RelaxKonOS.Core.Primitives.Size(560, 280));
            return accepted;
        }

        async Task<bool> RequestIfUndecidedAsync(string permission)
        {
            var decision = context.Permissions.GetStatus(permission);
            if (decision == AppPermissionStatus.Undecided)
                decision = await context.Permissions.RequestAsync(permission);
            return decision == AppPermissionStatus.Granted;
        }

    }
}
