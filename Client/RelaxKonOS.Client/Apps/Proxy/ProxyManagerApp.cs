using RelaxKonOS.App.ProxyManager;
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
using RelaxKonOS.App.ProxyManager.Views;
using RelaxKonOS.Client.Apps.TaskManager;

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
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerProxyTunManage,
            AppPermissions.ServerComponentsRead], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Proxy]), InstancePolicy: ApplicationInstancePolicy.SingleWindow, IconPath: "avares://RelaxKonOS.App.ProxyManager/Assets/proxy.png");

    public override void Activate(AppContext context)
    {
        var language = (LocalizationService)context.Services.GetService(typeof(LocalizationService))!;
        string Localize(string key, string fallback) => ProxyManagerLocalization.Resolve(language, key, fallback);
        string Label(string key) => Localize(key, key);
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var repository = context.Services.GetService(typeof(IServerProxy)) as IServerProxy;
        if (session is null || repository is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(Label("application.relaxkonos.proxy.display_name"), new TextBlock { Text = Label("proxy.login_required"), Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false); return;
        }
        var systemMonitor = context.Services.GetService(typeof(ITaskManagerClient)) as ITaskManagerClient;
        // Do not start a proxy operation with the initial Undecided permission snapshot.
        // ApplicationManager presents prompts after Activate returns, so this workspace owns
        // the first request and waits for its decision before enabling server actions.
        var dependency = RelaxKonOS.Client.Apps.ComponentCenter.ComponentDependencySettingsViewModel.Create(context, "relaxkonos.proxy");
        var vm = new ProxyManagerViewModel(repository, Localize, canManage: false, canManageTun: false)
        {
            ReadNetworkAddressesAsync = systemMonitor is null ? null : systemMonitor.GetNetworkAddressesAsync,
            FormatPrivilegedHelperProblem = code => PrivilegedHelperProblemText.TryFormat(code, out var text) ? text : null,
        };
        var view = new ProxyManagerWorkspace(vm)
        {
            CreateDependencySettings = () =>
            {
                _ = dependency.RefreshAsync();
                return new ComponentCenter.ComponentDependencySettingsView { DataContext = dependency };
            },
        };
        var window = context.ShowWindow(Label("application.relaxkonos.proxy.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        RelaxKonOS.WindowManager.ModalDialog<bool>? activeEditor = null;
        vm.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        vm.RequestSystemProxySubscriptionDownloadAsync = async () =>
        {
            var useSystemProxy = false;
            await context.ShowDialogAsync<bool?>(window, Label("proxy.subscription_system_proxy.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    Label("proxy.subscription_system_proxy.message"),
                    result => { useSystemProxy = result; dialog.Close(result); },
                    Label("proxy.subscription_system_proxy.confirm")),
            }, new RelaxKonOS.Core.Primitives.Size(460, 220));
            return useSystemProxy;
        };
        vm.RequestPortChangeRestartAsync = async () =>
        {
            var restartNow = false;
            await context.ShowDialogAsync<bool?>(window, Label("proxy.port_restart.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    Label("proxy.port_restart.message"),
                    result => { restartNow = result; dialog.Close(result); },
                    Label("proxy.port_restart.now"),
                    Label("proxy.port_restart.later")),
            }, new RelaxKonOS.Core.Primitives.Size(460, 220));
            return restartNow;
        };
        vm.ShowRuntimeSubscriptionWindow = () =>
        {
            _ = context.ShowDialogAsync<bool>(window, Label("proxy.subscription_content"), dialog =>
                new ProxySubscriptionContentDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(840, 560));
        };
        vm.OpenNetworkSettingsDialogAsync = async isTun =>
        {
            vm.SelectNetworkSettingsPageForDialog(isTun);
            await vm.LoadSystemProxyHostOptionsAsync();
            await context.ShowDialogAsync<bool>(window, vm.NetworkSettingsDialogTitle, dialog =>
                new ProxyNetworkSettingsDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(600, isTun ? 700 : 650));
        };

        void RefreshLanguage(object? sender, SystemLanguageChangedEventArgs args)
        {
            window.Title = vm.Labels("application.relaxkonos.proxy.display_name");
            vm.RefreshLanguage();
        }
        language.LanguageChanged += RefreshLanguage;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed;
            language.LanguageChanged -= RefreshLanguage;
            dependency.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAfterPermissionDecisionAsync();

        async Task InitializeAfterPermissionDecisionAsync()
        {
            var read = await RequestIfUndecidedAsync(AppPermissions.ServerProxyRead);
            var manage = await RequestIfUndecidedAsync(AppPermissions.ServerProxyManage);
            var tun = await RequestIfUndecidedAsync(AppPermissions.ServerProxyTunManage);
            vm.SetPermissions(manage, tun);

            // A declined read grant still permits the workspace to render its normal API
            // authorization result, but it never enables a mutation solely because the prompt
            // happened to complete after activation.
            _ = read;
            await vm.StartAsync();
        }

        Task<TResult?> ShowOwnedAsync<TResult>(string title, Func<RelaxKonOS.WindowManager.ModalDialog<TResult>, Avalonia.Controls.Control> factory, RelaxKonOS.Core.Primitives.Size size)
            => activeEditor is { } editor ? editor.ShowDialogAsync(title, factory, size) : context.ShowDialogAsync(window, title, factory, size);

        async Task<bool> RequestIfUndecidedAsync(string permission)
        {
            var decision = context.Permissions.GetStatus(permission);
            if (decision == AppPermissionStatus.Undecided)
                decision = await context.Permissions.RequestAsync(permission);
            return decision == AppPermissionStatus.Granted;
        }

    }
}
