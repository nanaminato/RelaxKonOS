using System.Reflection;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.WebServers;

internal static class WebSiteReadRecoveryChecks
{
    public static void Run()
    {
        var client = DispatchProxy.Create<IRemoteWebServerClient, SiteEditorClient>();
        var stub = (SiteEditorClient)(object)client;
        var vm = new WebServerManagerViewModel(client, null!, DispatchProxy.Create<IAuthSession, SiteEditorSession>(), new FirewallTestPermissions(),
            DispatchProxy.Create<IHostElevationBroker, SiteEditorElevation>());
        var server = new WebServerDto("first", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated,
            "/nginx", "/nginx.conf", "test", DateTimeOffset.UtcNow, new(true, true, true));
        vm.SelectedServer = server;
        var original = Site("original", "first");
        vm.Sites.Add(original);
        vm.SiteName = "draft";
        vm.SiteRootPath = "/var/www";
        vm.SiteBindings.Add(new("example.test", 80));
        vm.BeginSiteEditing();
        stub.ReadSites = _ => Task.FromException<IReadOnlyList<WebServerSiteDto>?>(new IOException("Read failed"));
        vm.RefreshSitesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.Sites.Contains(original) && vm.SiteName == "draft" && !vm.SiteFactsAvailable,
            "A failed site read retains the last list and draft while invalidating write facts.");
        Check(!vm.SaveSiteCommand.CanExecute(null) && !vm.DeleteSiteCommand.CanExecute(null),
            "Unavailable site facts disable writes.");
        stub.ReadSites = _ => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>(null);
        vm.RefreshSitesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.Sites.Contains(original) && vm.SiteStatusText.Resolve() == LocalizedText.Get("webservers.site.list_failed"),
            "A missing response is a read failure, not an empty list.");
        stub.ReadSites = _ => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([original]);
        vm.RefreshSitesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.SiteFactsAvailable && vm.SaveSiteCommand.CanExecute(null) && vm.SiteName == "draft",
            "Explicit refresh restores facts without replacing the draft.");

        var closed = 0;
        vm.CloseSiteEditorAsync = () => { closed++; return Task.CompletedTask; };
        stub.ReadSites = _ => Task.FromException<IReadOnlyList<WebServerSiteDto>?>(new IOException("Read failed"));
        var saving = vm.SaveSiteCommand.ExecuteAsync(null);
        var saved = Site("saved", "first") with { Name = "draft" };
        stub.Pending.SetResult(saved);
        PumpUntil(() => saving.IsCompleted);
        saving.GetAwaiter().GetResult();
        Check(closed == 1 && vm.Sites.Contains(saved) && vm.Sites.Contains(original)
            && vm.SelectedSite == saved && !vm.SiteFactsAvailable,
            "A successful receipt remains authoritative when the following refresh fails.");
        Check(vm.SiteStatusText.Resolve() == LocalizedText.Get("webservers.site.save_refresh_failed"),
            "Saved-but-refresh-failed feedback is preserved.");
        vm.SaveSiteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.Calls == 1, "A refresh failure cannot replay the confirmed save.");

        var delayed = new TaskCompletionSource<IReadOnlyList<WebServerSiteDto>?>();
        var second = Site("second-site", "second");
        stub.ReadSites = id => id == "first" ? delayed.Task : Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([second]);
        var reading = vm.RefreshSitesCommand.ExecuteAsync(null);
        vm.SelectedServer = server with { Id = "second" };
        delayed.SetResult([original]);
        PumpUntil(() => reading.IsCompleted);
        Check(vm.Sites.SequenceEqual([second]) && vm.SiteFactsAvailable,
            "A late read from the old instance cannot overwrite the new instance's facts.");
        Console.WriteLine("Website read recovery checks passed.");
    }
    private static WebServerSiteDto Site(string id, string server)
        => new(id, server, id, [new("example.test", 80)], "/var/www", true, [], null, false, true, false, DateTimeOffset.UtcNow);
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Site read recovery did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
