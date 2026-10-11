using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class WebSiteEditorChecks
{
    public static void Run()
    {
        var client = DispatchProxy.Create<IRemoteWebServerClient, SiteEditorClient>();
        var stub = (SiteEditorClient)(object)client;
        var vm = new WebServerManagerViewModel(client, null!, DispatchProxy.Create<IAuthSession, SiteEditorSession>(), new FirewallTestPermissions(),
            DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>());
        vm.SelectedServer = new("test-instance", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated,
            "/nginx", "/nginx.conf", "test", DateTimeOffset.UtcNow, new(true, true, true));
        vm.SiteName = "draft";
        vm.SiteRootPath = "/var/www";
        vm.SiteBindings.Add(new("example.test", 80));
        vm.SiteRoutes.Add(new("/api", "http://127.0.0.1:8080"));
        vm.ShowSiteSaveErrorAsync = _ => Task.CompletedTask;
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var owner = manager.Create(new WindowCreateOptions(new("test.websites"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        ModalDialog<bool>? handle = null;
        var result = manager.ShowDialogAsync<bool>(owner, "Site", dialog =>
        {
            handle = dialog;
            var type = typeof(WebServerManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.WebServers.Views.WebServerSiteDialogView")!;
            return (Control)Activator.CreateInstance(type, [vm, dialog])!;
        });
        vm.SiteRoutes[0].DisableBuffering = true;
        handle!.Cancel();
        Check(manager.Windows.Count == 3, "Editing a nested route requires discard confirmation.");
        Confirmation(manager).NoCommand.Execute(null);
        PumpUntil(() => manager.Windows.Count == 2);
        Check(!result.IsCompleted && vm.SiteRoutes[0].DisableBuffering, "Keeping changes retains nested route input.");

        var saving = vm.SaveSiteCommand.ExecuteAsync(null);
        Check(vm.IsSavingSite && !vm.SaveSiteCommand.CanExecute(null), "Site submission has a visible exclusive busy state.");
        vm.SaveSiteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.Calls == 1, "Repeated site save cannot dispatch a second request.");
        manager.Close(handle.Window!);
        Check(!result.IsCompleted && manager.Windows.Count == 2, "Window close is blocked during site submission.");
        vm.SiteName = "later-input";
        Check(stub.Request is { Name: "draft", Routes: [{ DisableBuffering: true }] },
            "The request freezes the submitted scalar fields and nested routes.");
        var refusalType = typeof(WebServerManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.WebServers.WebServerApiException")!;
        stub.Pending.SetException((Exception)Activator.CreateInstance(refusalType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            ["webserver.site.invalid", HttpStatusCode.BadRequest], null)!);
        PumpUntil(() => saving.IsCompleted);
        saving.GetAwaiter().GetResult();
        Check(!vm.IsSavingSite && !result.IsCompleted && vm.SiteName == "later-input" && vm.SiteRoutes[0].DisableBuffering,
            "A refused save unlocks the editor and retains the complete draft.");
        handle.Cancel();
        Confirmation(manager).YesCommand.Execute(null);
        PumpUntil(() => manager.Windows.Count == 1);
        Check(result.IsCompleted, "Confirmed site discard closes the editor.");
        manager.Close(owner);
        Console.WriteLine("Website editor checks passed.");
    }
    private static ConfirmDialogViewModel Confirmation(WindowManagerService manager)
        => (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Site editor transition did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class SiteEditorClient : DispatchProxy
{
    public TaskCompletionSource<WebServerSiteDto?> Pending = new();
    public UpsertWebServerSiteRequest? Request;
    public int Calls;
    public Func<string, Task<IReadOnlyList<WebServerSiteDto>?>> ReadSites = _ => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([]);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetStatusAsync" => Task.FromResult<WebServerStatusDto?>(null),
        "ListSitesAsync" => ReadSites((string)args![0]!),
        "UpsertSiteAsync" => Save(args),
        _ => throw new NotSupportedException(method.Name)
    };
    private Task<WebServerSiteDto?> Save(object?[]? args)
    {
        Request = (UpsertWebServerSiteRequest)args![1]!;
        Calls++;
        return Pending.Task;
    }
}
public class SiteEditorElevation : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => ((Delegate)args![2]!).DynamicInvoke();
}
