using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Headless;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Apps.WebServers.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.WebServers;

internal static class CurrentNginxChecks
{
    public static void Run()
    {
        var client = DispatchProxy.Create<IRemoteWebServerClient, CurrentNginxClient>();
        var host = (CurrentNginxClient)(object)client;
        var session = DispatchProxy.Create<IAuthSession, SiteEditorSession>();
        WebServerManagerViewModel Model(bool center) => new(client, null!, session, new FirewallTestPermissions(),
            DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>()) { IsComponentCenter = center };
        var manager = Model(false);
        var center = Model(true);
        manager.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        center.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(manager.SelectedServer?.Id == "first" && !manager.ShowInstanceSelection, "Linux manager resolves the host selection without a picker.");
        center.SelectedServer = host.Servers[1];
        center.SelectCurrentNginxCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Current == "second", "Component Center persists the explicit host selection.");
        manager.BeginSiteEditing();
        manager.SynchronizeCurrentNginxAsync().GetAwaiter().GetResult();
        Check(manager.SelectedServer?.Id == "second" && manager.SiteEditorTargetChanged && !manager.SaveSiteCommand.CanExecute(null),
            "Changing the current Nginx invalidates the previous site editor target.");
        manager.EndSiteEditing();
        Check(!manager.SelectCurrentNginxCommand.CanExecute(null), "The manager cannot set the host selection.");
        foreach (var width in new[] { 1060, 520 })
        {
            Capture(new WebServerInstancesPageView { DataContext = manager }, "manager-" + width, width);
            Capture(new CurrentNginxSelectionView { DataContext = center }, "selection-" + width, width);
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
        Console.WriteLine("Current Nginx desktop checks passed (7 assertions, 4 previews).");
    }
    private static void Capture(Control view, string name, int width)
    {
        var window = new Window { Width = width, Height = 760, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var output = Path.Combine(AppContext.BaseDirectory, "current-nginx-previews");
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
