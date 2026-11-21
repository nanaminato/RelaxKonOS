using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.ProxyManager.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.SystemMonitor;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.ProxyManager;

public sealed class ProxyManagerPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.proxy.display_name";
    private const string HelperKey = "common.problem.privileged_helper_unavailable";

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.proxy"),
        "Proxy Manager", "1.0.0", "⇄", "Manage the host Proxy runtime and recovery state",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerProxyTunManage,
            AppPermissions.ServerComponentsRead, AppPermissions.ServerMetricsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Proxy]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow,
        IconPath: "avares://RelaxKonOS.App.ProxyManager/Assets/proxy.png");

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Localize(string key, string fallback) => ProxyManagerLocalization.Resolve(language, key, fallback);
        string Label(string key) => Localize(key, key);
        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent)
        {
            context.Windows.ShowWindow(Label(TitleKey),
                new TextBlock { Text = Label("proxy.login_required"), Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var permissions = context.Permissions;
        var model = new ProxyManagerViewModel(context.Proxy, Localize, false, false)
        {
            ReadNetworkAddressesAsync = async token =>
            {
                if (!connection.IsCurrent || !permissions.IsGranted(AppPermissions.ServerMetricsRead)) return [];
                var snapshot = await context.ServerMonitor.GetPerformanceInfoAsync(token);
                return snapshot.Data?.Networks.SelectMany(network => network.Addresses
                    .Where(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => new NetworkAddressDto(network.Name, address, "IPv4"))).ToArray() ?? [];
            },
            FormatPrivilegedHelperProblem = problem => IsHelperUnavailable(problem) ? Label(HelperKey) : null,
        };
        IExternalAppWindowHandle? owner = null;
        Control? dependencySettings = null;
        var view = new ProxyManagerWorkspace(model)
        {
            CreateDependencySettings = () => owner is null ? null
                : dependencySettings ??= context.ComponentDependencies?.CreateSettingsView(owner),
        };
        var window = context.Windows.ShowWindow(Label(TitleKey), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        owner = window;
        view.AttachWindowHeader(window.Window);
        using var initialize = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Closed);
        var initializationToken = initialize.Token;

        model.RequestSystemProxySubscriptionDownloadAsync = () => window.ConfirmAsync(
            Label("proxy.subscription_system_proxy.title"), Label("proxy.subscription_system_proxy.message"),
            Label("proxy.subscription_system_proxy.confirm"));
        model.RequestPortChangeRestartAsync = () => window.ShowDialogAsync<bool>(Label("proxy.port_restart.title"), dialog =>
        {
            var restart = new Button { Content = Label("proxy.port_restart.now"), Classes = { "primary" } };
            var later = new Button { Content = Label("proxy.port_restart.later") };
            restart.Click += (_, _) => dialog.Close(true);
            later.Click += (_, _) => dialog.Cancel();
            return new StackPanel
            {
                Margin = new Thickness(22), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = Label("proxy.port_restart.message"), TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8, Children = { later, restart } },
                },
            };
        }, new Size(460, 220));
        model.ShowPrivilegedHelperUnavailableAsync = _ => window.ShowDialogAsync<bool>(Label(HelperKey + ".title"), dialog =>
        {
            var close = new Button { Content = Label("common.close"), Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => dialog.Close(true);
            return new StackPanel
            {
                Margin = new Thickness(22), Spacing = 18,
                Children = { new TextBlock { Text = Label(HelperKey), TextWrapping = TextWrapping.Wrap }, close },
            };
        }, new Size(560, 270));
        model.ShowRuntimeSubscriptionWindow = async () =>
        {
            try
            {
                await window.ShowDialogAsync<bool>(Label("proxy.subscription_content"),
                    dialog => new ProxySubscriptionContentDialogView(model, dialog), new Size(840, 560));
            }
            finally { model.RuntimeSubscriptionText = ""; }
        };
        model.OpenNetworkSettingsDialogAsync = async isTun =>
        {
            if (window.Closed.IsCancellationRequested || !connection.IsCurrent) return;
            model.SelectNetworkSettingsPageForDialog(isTun);
            if (!isTun)
            {
                if (permissions.GetStatus(AppPermissions.ServerMetricsRead) == AppPermissionStatus.Undecided)
                    await permissions.RequestAsync(AppPermissions.ServerMetricsRead, window.Closed);
                await model.LoadSystemProxyHostOptionsAsync();
            }
            if (!window.Closed.IsCancellationRequested && connection.IsCurrent)
                await window.ShowDialogAsync<bool>(model.NetworkSettingsDialogTitle,
                    dialog => new ProxyNetworkSettingsDialogView(model, dialog), new Size(600, isTun ? 700 : 650));
        };

        void RefreshLanguage(object? sender, SystemLanguageChangedEventArgs args) => Dispatch(() =>
        {
            if (window.Closed.IsCancellationRequested) return;
            window.Window.Title = Label(TitleKey);
            model.RefreshLanguage();
        });
        language.LanguageChanged += RefreshLanguage;
        var observation = connection.ObserveInvalidation(() => Dispatch(model.InvalidateSession));
        window.Closed.Register(() =>
        {
            language.LanguageChanged -= RefreshLanguage;
            observation.Dispose();
            Dispatch(model.InvalidateSession);
        });

        var read = await RequestAsync(AppPermissions.ServerProxyRead);
        var manage = read && await RequestAsync(AppPermissions.ServerProxyManage);
        var tun = read && await RequestAsync(AppPermissions.ServerProxyTunManage);
        if (initializationToken.IsCancellationRequested || !connection.IsCurrent) return;
        model.SetPermissions(manage && connection.IsController, tun && connection.IsController);
        await model.StartAsync();

        async Task<bool> RequestAsync(string permission)
        {
            if (initializationToken.IsCancellationRequested || !connection.IsCurrent) return false;
            if (permissions.GetStatus(permission) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(permission, initializationToken);
            return !initializationToken.IsCancellationRequested && connection.IsCurrent && permissions.IsGranted(permission);
        }
    }

    private static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private static bool IsHelperUnavailable(string? problem) => problem is not null
        && (problem.Equals(ProxyProblemCodes.PrivilegedOperationUnavailable, StringComparison.OrdinalIgnoreCase)
            || problem.Equals("privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase)
            || problem.EndsWith("/privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase));
}
