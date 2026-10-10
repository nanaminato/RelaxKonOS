using System.Reflection;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Certificates;

internal static class WebSiteScopeChecks
{
    public static void Run()
    {
        var auth = DispatchProxy.Create<IAuthSession, SiteEditorSession>();
        var session = (SiteEditorSession)(object)auth;
        var client = DispatchProxy.Create<IRemoteWebServerClient, SiteEditorClient>();
        var stub = (SiteEditorClient)(object)client;
        var broker = DispatchProxy.Create<IHostElevationBroker, DelayedSiteElevation>();
        var grant = (DelayedSiteElevation)(object)broker;
        var vm = Create(client, auth, broker);
        session.RebindTransport();
        Check(vm.SiteFactsAvailable && vm.SaveSiteCommand.CanExecute(null),
            "A transport rebind that preserves the authentication snapshot retains the editor.");
        var saving = vm.SaveSiteCommand.ExecuteAsync(null);
        session.ReplaceIdentity();
        grant.Permit.SetResult();
        PumpUntil(() => saving.IsCompleted);
        Check(stub.Calls == 0 && !vm.SiteFactsAvailable && vm.SelectedServer is null && vm.SiteName.Length == 0,
            "Identity change before elevation completes blocks the write and clears the old draft.");
        Check(!vm.SaveSiteCommand.CanExecute(null) && vm.SiteEditorTargetChanged,
            "An old editor cannot become writable under a new authentication snapshot with identical ids.");

        vm = Create(client, auth, DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>());
        var closed = 0;
        vm.CloseSiteEditorAsync = () => { closed++; return Task.CompletedTask; };
        saving = vm.SaveSiteCommand.ExecuteAsync(null);
        session.ReplaceIdentity();
        stub.Pending.SetResult(Site("saved", "first"));
        PumpUntil(() => saving.IsCompleted);
        Check(closed == 0 && vm.SelectedServer is null && vm.Sites.Count == 0 && vm.SiteStatusText.Resolve().Length == 0,
            "A late successful receipt cannot restore an old identity's data or close a new editor.");

        stub.Pending = new();
        broker = DispatchProxy.Create<IHostElevationBroker, DelayedSiteElevation>();
        grant = (DelayedSiteElevation)(object)broker;
        vm = Create(client, auth, broker);
        saving = vm.SaveSiteCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.SelectedServer! with { Id = "second" };
        grant.Permit.SetResult();
        PumpUntil(() => saving.IsCompleted);
        Check(stub.Calls == 1 && vm.SiteName == "draft" && vm.SiteEditorTargetChanged && !vm.SaveSiteCommand.CanExecute(null),
            "Changing instances invalidates the pending write and leaves the old draft read-only.");

        vm = Create(client, auth, DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>());
        vm.EndSiteEditing();
        var original = Site("existing", "first");
        vm.SelectedSite = original;
        vm.BeginSiteEditing();
        vm.SiteName = "edited";
        vm.SelectedSite = original with { Name = "remote change", UpdatedAt = original.UpdatedAt.AddMinutes(1) };
        Check(vm.SiteName == "edited", "A reread of the same site cannot replace local edits.");
        saving = vm.SaveSiteCommand.ExecuteAsync(null);
        Check(stub.Request!.ExpectedUpdatedAt == original.UpdatedAt,
            "An edited site submits the revision captured when its editor opened.");
        stub.Pending.SetException(new IOException("test read failure"));
        PumpUntil(() => saving.IsCompleted);

        var certificates = DispatchProxy.Create<IRemoteCertificateClient, PendingSiteCertificates>();
        var certificateReads = (PendingSiteCertificates)(object)certificates;
        vm = new WebServerManagerViewModel(client, certificates, auth, new FirewallTestPermissions(),
            DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>());
        var server = new WebServerDto("first", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated,
            "/nginx", "/nginx.conf", "test", DateTimeOffset.UtcNow, new(true, true, true));
        vm.SelectedServer = server;
        var opened = 0;
        vm.ShowSiteEditorAsync = _ => { opened++; return Task.CompletedTask; };
        var opening = vm.NewSiteCommand.ExecuteAsync(null);
        vm.SelectedServer = server with { Id = "second" };
        certificateReads.Pending.SetResult([]);
        PumpUntil(() => opening.IsCompleted);
        Check(opened == 0, "Changing instances while certificates load cannot open a new editor for the wrong instance.");
        certificateReads.Pending = new();
        vm.SelectedSite = Site("one", "second");
        opening = vm.EditSiteCommand.ExecuteAsync(null);
        vm.SelectedSite = Site("two", "second");
        certificateReads.Pending.SetResult([]);
        PumpUntil(() => opening.IsCompleted);
        Check(opened == 0, "Changing sites while certificates load cannot open the wrong edit target.");
        Console.WriteLine("Website scope checks passed.");
    }
    private static WebServerManagerViewModel Create(IRemoteWebServerClient client, IAuthSession auth, IHostElevationBroker broker)
    {
        var vm = new WebServerManagerViewModel(client, null!, auth, new FirewallTestPermissions(), broker);
        vm.SelectedServer = new("first", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated,
            "/nginx", "/nginx.conf", "test", DateTimeOffset.UtcNow, new(true, true, true));
        vm.SiteName = "draft";
        vm.SiteRootPath = "/var/www";
        vm.SiteBindings.Add(new("example.test", 80));
        vm.BeginSiteEditing();
        return vm;
    }
    private static WebServerSiteDto Site(string id, string server)
        => new(id, server, id, [new("example.test", 80)], "/var/www", true, [], null, false, true, false, DateTimeOffset.UtcNow);
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Site scope transition did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class DelayedSiteElevation : DispatchProxy
{
    public TaskCompletionSource Permit = new();
    private Func<Task<WebServerSiteDto?>>? _work;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        _work = (Func<Task<WebServerSiteDto?>>)args![2]!;
        return ExecuteAsync();
    }
    private async Task<WebServerSiteDto?> ExecuteAsync()
    {
        await Permit.Task;
        return await _work!();
    }
}

public class PendingSiteCertificates : DispatchProxy
{
    public TaskCompletionSource<IReadOnlyList<CertificateDto>> Pending = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.Name == "ListAsync" ? Pending.Task : throw new NotSupportedException(method.Name);
}
