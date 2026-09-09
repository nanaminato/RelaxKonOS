using RelaxKonOS.Client.Services.WorkspaceSettings;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using RelaxKonOS.Client.Apps;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Client.Apps.Notepad;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Client.Apps.Welcome;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.DesktopRestore;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.WindowLayout;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using VirtualSystemDriveService = RelaxKonOS.Client.Services.VirtualSystemDrive.VirtualSystemDrive;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;
using RelaxKonOS.Shell;

namespace RelaxKonOS.Client.Services;

/// <summary>Composes the client-side DI container and registers built-in applications.</summary>
public static class Bootstrapper
{
    public static async Task<IServiceProvider> BuildAsync(Application app)
    {
        var services = new ServiceCollection();

        // ThemeService applies resources to the live Avalonia application instance.
        // Register the startup instance explicitly because it is not created by DI.
        services.AddSingleton<Application>(app);
        var windowManager = new WindowManagerService();
        services.AddSingleton(windowManager);
        services.AddSingleton<IWindowManager>(windowManager);
        services.AddSingleton<LocalLanguageStore>();
        services.AddSingleton<LoginNotificationPreferenceStore>();
        services.AddSingleton<DesktopWelcomePreferenceStore>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellSettings>();
        services.AddSingleton<ShellPreferenceStore>();
        services.AddSingleton<ShellCatalog>();
        services.AddSingleton<IShellCatalog>(sp => sp.GetRequiredService<ShellCatalog>());
        services.AddSingleton<DesktopShellOverlayService>();
        services.AddSingleton<ShellRuntime>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<LoginLocalizationService>();
        services.AddSingleton<ISystemLanguage>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddTransient<AcceptLanguageHandler>();
        services.AddSingleton<ApplicationManager>(sp =>
            new ApplicationManager(sp.GetRequiredService<IWindowManager>(), sp));
        services.AddSingleton<IAppActivationService>(sp => sp.GetRequiredService<ApplicationManager>());
        services.AddSingleton<VirtualSystemDriveService>();
        services.AddSingleton<IBuiltInApplicationFactoryRegistry, BuiltInApplicationRegistry>();
        services.AddSingleton<BuiltInDescriptorSeeder>();
        services.AddSingleton<ApplicationCatalogScanner>();
        services.AddSingleton<ShortcutStore>();
        services.AddSingleton<IAutomationRunner, AutomationRunner>();
        services.AddSingleton<IAutomationNotificationSink, DiagnosticAutomationNotificationSink>();
        services.AddSingleton<ShortcutActivationRouter>();

        // Auth（登录模块）：typed HttpClient + 仅内存认证会话 + 登录视图模型。
        services.AddHttpClient<IRelaxKonOSClient, RelaxKonOSClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "auth"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<ITerminalSettingsClient, TerminalSettingsClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "terminal-settings"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddSingleton<IRememberedSessionStore, RememberedSessionStore>();
        services.AddSingleton<IAuthSession, AuthSession>();
        services.AddTransient<AuthenticatedHttpHandler>();
        services.AddSingleton<ApplicationCompatibilityService>();
        services.AddSingleton<IApplicationCompatibilityEvaluator>(sp => sp.GetRequiredService<ApplicationCompatibilityService>());
        services.AddSingleton<IApplicationCompatibilityNotifier>(sp => sp.GetRequiredService<ApplicationCompatibilityService>());
        services.AddSingleton<LoginViewModel>();

        // Explorer（文件管理器）：typed HttpClient（JWT from IAuthSession）+ 应用注册。
        services.AddHttpClient<RelaxKonOS.Client.Apps.Explorer.IExplorerClient, RelaxKonOS.Client.Apps.Explorer.ExplorerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "explorer"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddSingleton(sp =>
        {
            var session = sp.GetRequiredService<IAuthSession>();
            var center = new RelaxKonOS.Client.Apps.Explorer.Models.ExplorerOperationCenter(sp.GetRequiredService<RelaxKonOS.Client.Apps.Explorer.IExplorerClient>())
            {
                SessionKey = () => session.State == AuthSessionState.Authenticated
                    ? $"{session.ServerUrl}/{session.CurrentUser?.Id}/{session.CurrentWorkspace?.Id}/{session.CurrentDevice?.Id}" : null,
            };
            session.StateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(center.SessionChanged);
            return center;
        });
        services.AddSingleton<RelaxKonOS.Client.Apps.Explorer.IRemoteFileClipboard, RelaxKonOS.Client.Apps.Explorer.RemoteFileClipboard>();

        // Browser（浏览器）：typed HttpClient（JWT from IAuthSession）+ 应用注册。
        // NativeWebView 用平台原生引擎（Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），网页内容走客户端网络；
        // Server 仅持久化书签与历史记录（按用户隔离）。
        services.AddHttpClient<RelaxKonOS.Client.Apps.Browser.IBrowserClient, RelaxKonOS.Client.Apps.Browser.BrowserClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "browser"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();

        // TaskManager（任务管理器）：typed HttpClient（JWT from IAuthSession，与 Browser/Explorer 同模式）。
        // 拉取服务端采集的宿主 OS 资源占用（CPU/内存/磁盘/网络/GPU）与进程列表；结束进程权限不足提示需在宿主 OS 提权。
        services.AddHttpClient<RelaxKonOS.Client.Apps.TaskManager.ITaskManagerClient, RelaxKonOS.Client.Apps.TaskManager.TaskManagerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "task-manager"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddTransient<RelaxKonOS.Client.Apps.TaskManager.PerformanceStream>();
        services.AddHttpClient<RelaxKonOS.Client.Apps.Docker.IRemoteDockerClient, RelaxKonOS.Client.Apps.Docker.RemoteDockerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "docker"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<RelaxKonOS.Client.Apps.ProcessGuardian.IProcessGuardianClient, RelaxKonOS.Client.Apps.ProcessGuardian.ProcessGuardianClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "process-guardian"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<RelaxKonOS.Client.Apps.Firewall.IRemoteFirewallClient, RelaxKonOS.Client.Apps.Firewall.RemoteFirewallClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "firewall"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        // Certificates（证书管理器）与 WebServers（Web 服务器管理器）：typed HttpClient（JWT from IAuthSession，与 Firewall 同模式）。
        // 长时操作通过 Idempotency-Key + 操作轮询跟踪；私钥/ACME 账户与 shell 文本均不通过 HTTP 暴露。
        services.AddHttpClient<RelaxKonOS.Client.Apps.Certificates.IRemoteCertificateClient, RelaxKonOS.Client.Apps.Certificates.RemoteCertificateClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "certificates"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<RelaxKonOS.Client.Apps.WebServers.IRemoteWebServerClient, RelaxKonOS.Client.Apps.WebServers.RemoteWebServerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "webservers"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<RelaxKonOS.Client.Apps.Tunnels.IRemoteTunnelClient, RelaxKonOS.Client.Apps.Tunnels.RemoteTunnelClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "tunnels"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<RelaxKonOS.Client.Apps.Proxy.IProxyRepository, RelaxKonOS.Client.Apps.Proxy.RemoteProxyRepository>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "proxy"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<RelaxKonOS.Client.Apps.Git.IRemoteGitClient, RelaxKonOS.Client.Apps.Git.RemoteGitClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "git"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();

        // Settings（设置中心）：typed HttpClient（JWT from IAuthSession，与 Browser/Explorer 同模式）。
        // 偏好持久化到服务端 Workspace（/workspaces/{id}/preferences），多设备共享。
        // Host writes must not pass through an authentication handler that can replay requests.
        services.AddHttpClient<HostSettings.IHostTimeService, HostSettings.HostTimeService>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<IWorkspaceSettingsService, WorkspaceSettingsService>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "settings"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<IImageMirrorClient, ImageMirrorClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "image-mirrors"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<IWallpaperClient, WorkspaceWallpaperClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "wallpaper"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<IWindowLayoutClient, WindowLayoutClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "window-layout"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddSingleton<WindowLayoutStore>();
        services.AddSingleton<DefaultAppRegistry>();
        services.AddSingleton<IUriSchemeDefaultResolver>(sp => sp.GetRequiredService<DefaultAppRegistry>());
        services.AddSingleton<IUriSchemeRoutingUi, UriSchemeRoutingUi>();
        services.AddSingleton<IAppActivationDiagnostics, UriSchemeRoutingDiagnostics>();
        services.AddSingleton<WallpaperService>();
        services.AddSingleton<TextEditorEncodingSettings>();
        services.AddSingleton<ITextFileSniffer, TextFileSniffer>();
        services.AddSingleton<IAppPermissionManager, JsonAppPermissionManager>();
        services.AddSingleton<IAppPermissionRequestService, AppPermissionRequestService>();
        services.AddSingleton<IAppDataManager, AppDataManager>();
        services.AddSingleton<DeveloperModeService>();
        // The session must be resolved only when diagnostics are used. Resolving it while an
        // auth HttpClient handler is constructed would recursively construct that same client.
        services.AddSingleton<NetworkDiagnosticsService>(sp => new NetworkDiagnosticsService(
            sp.GetRequiredService<DeveloperModeService>(),
            () => sp.GetRequiredService<IAuthSession>()));
        services.AddSingleton<NetworkInspectorWindowService>();
        services.AddHttpClient<IAppCapabilityClient, AppCapabilityClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "capabilities"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<IAppSettingsClient, AppSettingsClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "app-settings"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddHttpClient<RelaxKonOS.Client.Apps.Registry.IRegistryClient, RelaxKonOS.Client.Apps.Registry.RegistryClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "registry"))
            .AddHttpMessageHandler<AcceptLanguageHandler>()
            .AddRelaxKonOSAuthentication();
        services.AddSingleton<ISettingsNavigation, SettingsNavigationService>();
        services.AddSingleton<ExternalAppContextFactory>();
        services.AddSingleton<DeveloperPackageManager>();
        services.AddSingleton<AppPackageInstallerService>();
        services.AddSingleton<DeveloperBridgeService>();
        // Port forwarding owns local ssh processes and a device-local, non-secret settings file.
        // It is intentionally not part of Workspace preference synchronization.
        services.AddSingleton<RelaxKonOS.Client.Apps.PortForwarding.PortForwardingSettingsStore>();
        services.AddSingleton<RelaxKonOS.Client.Apps.PortForwarding.IPortForwardingService, RelaxKonOS.Client.Apps.PortForwarding.PortForwardingService>();
        // PreferencesSync 监听登录态，登录后把服务端偏好应用到 ShellSettings + DefaultAppRegistry。
        services.AddSingleton<PreferencesSync>();
        services.AddSingleton<WorkspacePreferencesEditor>();

        // Built-in applications.
        services.AddSingleton<WelcomeApp>();
        services.AddSingleton<NotepadApp>();
        services.AddSingleton<CodeEditorApp>();
        services.AddSingleton<ImageViewerApp>();
        services.AddSingleton<SettingsApp>();
        services.AddSingleton<TerminalApp>();
        services.AddSingleton<IDesktopRestoreParticipant, TerminalDesktopRestoreParticipant>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Explorer.ExplorerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Browser.BrowserApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.PortForwarding.PortForwardingApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.TaskManager.TaskManagerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Docker.DockerManagerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.ProcessGuardian.ProcessGuardianApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Firewall.FirewallApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Certificates.CertificateManagerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.WebServers.WebServerManagerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Tunnels.TunnelManagerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Proxy.ProxyManagerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Git.GitClientApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.AppInstaller.AppInstallerApp>();
        services.AddSingleton<RelaxKonOS.Client.Apps.Registry.RegistryApp>();

        services.AddSingleton<DesktopShellViewModel>(sp =>
        {
            var wm = sp.GetRequiredService<WindowManagerService>();
            var apps = sp.GetRequiredService<ApplicationManager>();
            var settings = sp.GetRequiredService<ShellSettings>();
            var localization = sp.GetRequiredService<LocalizationService>();
            var session = sp.GetRequiredService<IAuthSession>();
            Action shutdown = () =>
            {
                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            };
            return new DesktopShellViewModel(
                wm, apps, settings, localization, session, shutdown,
                sp.GetRequiredService<DesktopRestoreOrchestrator>(),
                sp.GetRequiredService<RelaxKonOS.Client.Apps.Explorer.IExplorerClient>(),
                sp.GetRequiredService<RelaxKonOS.Client.Apps.Explorer.IRemoteFileClipboard>(),
                sp.GetRequiredService<DefaultAppRegistry>(),
                sp.GetRequiredService<IWorkspaceSettingsService>(),
                sp.GetRequiredService<IAppActivationDiagnostics>(),
                sp.GetRequiredService<ITextFileSniffer>(),
                sp.GetRequiredService<PreferencesSync>(),
                sp.GetRequiredService<DesktopWelcomePreferenceStore>(),
                sp.GetRequiredService<ShortcutStore>(),
                sp.GetRequiredService<ShortcutActivationRouter>());
        });

        services.AddSingleton<DesktopRestoreOrchestrator>();

        var provider = services.BuildServiceProvider();

        // Create both language services before their respective windows and package contexts.
        // The login service is intentionally independent from the workspace language service.
        provider.GetRequiredService<LocalizationService>();
        provider.GetRequiredService<LoginLocalizationService>();

        windowManager.LayoutStore = provider.GetRequiredService<WindowLayoutStore>();

        // Discovery repairs the observable descriptor mirror, compares every file with the
        // compiled registry, then registers only Host-selected factories through ApplicationManager.
        var manager = provider.GetRequiredService<ApplicationManager>();
        await provider.GetRequiredService<ApplicationCatalogScanner>()
            .ScanAndRegisterBuiltInsAsync(manager);

        // Development packages follow the same runtime registry as built-in applications.
        await provider.GetRequiredService<DeveloperPackageManager>().LoadInstalledAsync();
        provider.GetRequiredService<DeveloperBridgeService>();

        // Build the desktop / start menu entries.
        provider.GetRequiredService<DesktopShellViewModel>().PopulateDesktop();

        // Eagerly start preferences sync so it catches the login StateChanged event and
        // applies server-side preferences to the shell as soon as the workspace connects.
        provider.GetRequiredService<PreferencesSync>();

        return provider;
    }
}
