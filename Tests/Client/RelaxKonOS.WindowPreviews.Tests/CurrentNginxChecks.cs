using RelaxKonOS.AppSDK;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Headless;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Apps.WebServers.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;

internal static class CurrentNginxChecks
{
    public static void Run()
    {
        RunPlatform(HostPlatformKind.Linux);
        RunPlatform(HostPlatformKind.Windows);
        VerifyPackagedCapability();
        VerifyManagedUninstall();
    }

    private static void VerifyManagedUninstall()
    {
        var client = DispatchProxy.Create<IServerWebServers, CurrentNginxClient>();
        var host = (CurrentNginxClient)(object)client;
        var session = DispatchProxy.Create<IAuthSession, SiteEditorSession>();
        var model = new WebServerManagerViewModel(client, null!, new WebServerWindowConnection(session),
            HostPlatformKind.Windows, new FirewallTestPermissions(), null!) { IsComponentCenter = true };
        model.SelectedServer = host.Servers[0] with { Capabilities = new(true, true, true, CanUninstall: true) };
        Check(!model.UninstallManagedCommand.CanExecute(null), "Runtime uninstall requires a Component Center action.");
        var calls = 0;
        model.AttachManagedRuntimeActions(() => { }, () => { calls++; return Task.CompletedTask; });
        model.RequestManagedUninstallConfirmationAsync = () =>
        {
            model.SelectedServer = host.Servers[1] with { Capabilities = new(true, true, true, CanUninstall: true) };
            return Task.FromResult(true);
        };
        model.UninstallManagedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(calls == 0, "Changing the selected Nginx while confirming blocks runtime uninstall.");
        model.RequestManagedUninstallConfirmationAsync = () => Task.FromResult(true);
        model.UninstallManagedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(calls == 1, "Confirmed runtime uninstall dispatches through Component Center.");
    }

    private static void VerifyPackagedCapability()
    {
        var app = new AppId("relaxkonos.webservers");
        var permissions = new CertificateCapabilityChecks.Permission(app.Value);
        var client = DispatchProxy.Create<IServerWebServers, CurrentNginxClient>();
        var host = (CurrentNginxClient)(object)client;
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "web-server-memory.json"));
        var broker = DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>();
        var elevation = (SiteEditorElevation)(object)broker;
        var capability = new ServerWebServersCapability(app, permissions, new ExternalServerConnection(memory.Capture(auth)), client, broker);
        Reject<UnauthorizedAccessException>(() => capability.ListAsync(default).GetAwaiter().GetResult());
        permissions.Granted.Add(AppPermissions.ServerWebServersRead);
        Check(capability.ListAsync(default).GetAwaiter().GetResult().Count == 2, "The SDK permits the granted web-server read.");
        Reject<UnauthorizedAccessException>(() => capability.IntegrateCandidateAsync("external", null!, default).GetAwaiter().GetResult());
        Check(host.IntegrationCalls == 0, "SDK read permission alone cannot integrate a web server.");
        permissions.Granted.Add(AppPermissions.ServerWebServersManage);
        elevation.BeforeDispatch = (grant, target) =>
            Check(grant is HostElevationCapability.NginxConfigurationWrite && target is "external",
                "SDK integration requests the existing candidate-scoped host authorization.");
        capability.IntegrateCandidateAsync("external", null!, default).GetAwaiter().GetResult();
        Reject<UnauthorizedAccessException>(() => capability.SelectCurrentNginxAsync(new("second"), default).GetAwaiter().GetResult());
        Check(host.IntegrationCalls == 1 && host.Current == "first", "SDK management leaves current Nginx selection with Component Center.");
        elevation.BeforeDispatch = (_, _) => session.ServiceId = "http://replacement.invalid";
        Reject<InvalidOperationException>(() => capability.IntegrateCandidateAsync("external", null!, default).GetAwaiter().GetResult());
        Check(host.IntegrationCalls == 1, "Switching servers during SDK authorization prevents the write.");
        Reject<InvalidOperationException>(() => capability.ListAsync(default).GetAwaiter().GetResult());

        static void Reject<T>(Action call) where T : Exception
        {
            try { call(); }
            catch (T) { return; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
    }
    private static void RunPlatform(HostPlatformKind platform)
    {
        var client = DispatchProxy.Create<IServerWebServers, CurrentNginxClient>();
        var host = (CurrentNginxClient)(object)client;
        var session = DispatchProxy.Create<IAuthSession, SiteEditorSession>();
        ((SiteEditorSession)(object)session).Platform = platform;
        WebServerManagerViewModel Model(bool center) => new(client, null!, new WebServerWindowConnection(session), platform, new FirewallTestPermissions(),
            DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>()) { IsComponentCenter = center };
        var manager = Model(false);
        var center = Model(true);
        manager.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        center.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(manager.SelectedServer?.Id == "first" && !manager.ShowInstanceSelection, platform + " manager resolves the host selection without a picker.");
        center.SelectedServer = host.Servers[1];
        center.SelectCurrentNginxCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Current == "second", "Component Center persists the explicit host selection.");
        manager.BeginSiteEditing();
        manager.SynchronizeCurrentNginxAsync().GetAwaiter().GetResult();
        Check(manager.SelectedServer?.Id == "second" && manager.SiteEditorTargetChanged && !manager.SaveSiteCommand.CanExecute(null),
            "Changing the current Nginx invalidates the previous site editor target.");
        manager.EndSiteEditing();
        Check(typeof(WebServerManagerViewModel).GetProperty("InstallManagedCommand") is null,
            platform + " manager has no application installation command.");
        Check(!manager.SelectCurrentNginxCommand.CanExecute(null), "The manager cannot set the host selection.");
        foreach (var width in new[] { 1060, 520 })
        {
            Capture(new WebServerInstancesPageView { DataContext = manager }, platform + "-manager-" + width, width);
            Capture(new CurrentNginxSelectionView { DataContext = center }, platform + "-selection-" + width, width);
        }
        center.SelectedIntegrationCandidate = new("external", "nginx", WebServerType.Nginx, "/external/nginx", "/external/nginx.conf", "1.28", DateTimeOffset.UtcNow);
        center.RequestIntegrationConfirmationAsync = () => Task.FromResult(true);
        center.IntegrateCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.IntegrationCalls == 1, "Component Center integration uses the domain flow without an installation panel.");
        host.Current = null;
        manager.SynchronizeCurrentNginxAsync().GetAwaiter().GetResult();
        Check(manager.SelectedServer is null && !manager.NewSiteCommand.CanExecute(null), "An unavailable selection disables site creation.");
        host.Fail = true;
        manager.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(manager.SelectedServer is null && manager.Servers.Count == 0, "A failed selection read clears old management targets.");
        Console.WriteLine("Current Nginx desktop checks passed (8 assertions, 4 previews).");
    }
    private static void Capture(Control view, string name, int width)
    {
        var window = new Window { Width = width, Height = 760, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var output = Path.Combine(System.AppContext.BaseDirectory, "current-nginx-previews");
            Directory.CreateDirectory(output);
            using var image = new RenderTargetBitmap(new PixelSize(width, 760));
            image.Render(window); image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
        }
        finally { window.Close(); }
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
public class CurrentNginxClient : SiteEditorClient
{
    public string? Current = "first";
    public bool Fail;
    public int IntegrationCalls;
    public WebServerDto[] Servers = [
        new("first", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated, "/usr/sbin/nginx", "/etc/nginx/nginx.conf", "1.26", DateTimeOffset.UtcNow, new(true, true, true)),
        new("second", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated, "/opt/nginx", "/opt/nginx.conf", "1.28", DateTimeOffset.UtcNow, new(true, true, true))];
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "IntegrateCandidateAsync" => Integrate(),
        "ListAsync" => Task.FromResult<IReadOnlyList<WebServerDto>>(Servers),
        "ListIntegrationCandidatesAsync" => Task.FromResult<IReadOnlyList<WebServerIntegrationCandidateDto>>([]),
        "GetCurrentNginxAsync" => Fail ? Task.FromException<CurrentNginxDto>(new IOException("Unavailable")) : Task.FromResult(new CurrentNginxDto(Current)),
        "SelectCurrentNginxAsync" => Select((CurrentNginxDto)args![0]!),
        _ => base.Invoke(method, args)
    };
    private Task<WebServerOperationDto?> Integrate() { IntegrationCalls++; return Task.FromResult<WebServerOperationDto?>(null); }
    private Task<CurrentNginxDto> Select(CurrentNginxDto request) { Current = request.ServerId; return Task.FromResult(request); }
}
