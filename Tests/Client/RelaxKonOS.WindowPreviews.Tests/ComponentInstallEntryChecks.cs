using System.Net;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Apps.Git;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ComponentInstallEntryChecks
{
    public static void Run()
    {
        ComponentDownloadChecks.Run();
        foreach (var (id, service) in new[] { ("nginx", InstallationServiceId.Nginx), ("docker", InstallationServiceId.Docker),
            ("samba", InstallationServiceId.Smb), ("squid", InstallationServiceId.Squid),
            ("frpc", InstallationServiceId.Frp), ("frps", InstallationServiceId.Frp) })
        {
            var lifecycle = new EntryLifecycle(id);
            using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>()) { CanRead = true, CanManage = true };
            using var center = new ComponentCenterViewModel(runtime, new(), "https://test.invalid", Guid.NewGuid(),
                lifecycle: lifecycle, hostPlatform: "linux");
            using var handler = new EntryHandler();
            using var http = new HttpClient(handler);
            center.AttachPackageInstallation(id, new(new InstallationClient(http, DispatchProxy.Create<IAuthSession, EntrySession>()), null!, service,
                "relaxkonos.component-center", () => Task.CompletedTask, () => Task.FromResult<string?>(null)));
            center.SelectedNode = center.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == id);
            center.ResolvePackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(center.CanInstallPackage, id + " exposes the host-approved frozen installation in Component Center.");
            center.ConfirmPackageInstallAsync = _ => Task.FromResult(false);
            center.InstallPackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(handler.Calls == 0, id + " cancellation sends no installation request.");
            center.ConfirmPackageInstallAsync = _ => Task.FromResult(true);
            center.InstallPackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(handler.Calls == 1 && handler.Path == InstallationApiRoutes.Start(service, InstallationOperationKind.Install),
                id + " submits to its durable service installer.");
            using var wire = JsonDocument.Parse(handler.Body!);
            var frozen = wire.RootElement.GetProperty(id is "frpc" or "frps" ? "frozenArchive" : "frozenPackage");
            Check(frozen.GetProperty("componentId").GetString() == id
                && frozen.GetProperty("request").GetProperty("idempotencyKey").GetString() == handler.Key,
                id + " preserves the component identity and frozen idempotency key across HTTP.");
        }
        // Git has no installer of its own any more: on Linux it uses the same frozen distribution
        // package plan as every other system package, and on Windows this build only reports the Git
        // program that is already present.
        using var gitRuntime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>()) { CanRead = true, CanManage = true };
        using var gitCenter = new ComponentCenterViewModel(gitRuntime, new(), "https://test.invalid", Guid.NewGuid(),
            lifecycle: new EntryLifecycle("git"), hostPlatform: "linux");
        using var gitHandler = new EntryHandler();
        using var gitHttp = new HttpClient(gitHandler);
        gitCenter.GitEngine = new GitClientViewModel(null!) { IsGitAvailable = false };
        gitCenter.AttachPackageInstallation("git", new(new InstallationClient(gitHttp, DispatchProxy.Create<IAuthSession, EntrySession>()), null!, InstallationServiceId.Git,
            "relaxkonos.component-center", () => Task.CompletedTask, () => Task.FromResult<string?>(null)));
        gitCenter.SelectedNode = gitCenter.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "git");
        Check(typeof(ComponentCenterViewModel).GetProperty("InstallGitCommand") is null && !gitCenter.ShowGitInstallation,
            "Git exposes no install command of its own; the observation panel belongs to Windows only.");
        Check(gitCenter.ShowPackageFreeze && gitCenter.IsSystemPackage,
            "Linux Git uses the shared distribution package plan instead of a private installer.");
        gitCenter.ResolvePackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(gitCenter.CanInstallPackage, "The resolved Git package can be installed from Component Center.");
        gitCenter.ConfirmPackageInstallAsync = _ => Task.FromResult(false);
        gitCenter.InstallPackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(gitHandler.Calls == 0, "Git cancellation sends no request.");
        gitCenter.ConfirmPackageInstallAsync = _ => Task.FromResult(true);
        gitCenter.InstallPackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(gitHandler.Calls == 1 && gitHandler.Path == InstallationApiRoutes.Start(InstallationServiceId.Git, InstallationOperationKind.Install),
            "Git installs through the same durable installer as every other host package.");
        using (var windowsGit = new ComponentCenterViewModel(gitRuntime, new(), "https://test.invalid", Guid.NewGuid(),
            lifecycle: new EntryLifecycle("git"), hostPlatform: "windows"))
        {
            windowsGit.GitEngine = new GitClientViewModel(null!) { IsGitAvailable = false };
            windowsGit.SelectedNode = windowsGit.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "git");
            Check(!windowsGit.ShowPackageFreeze && windowsGit.ShowGitInstallation,
                "A Windows host reports the existing Git program instead of the Linux package plan.");
        }
        foreach (var width in new[] { 1060, 640 })
        {
            var view = new ComponentCenterWorkspace(gitCenter);
            var window = new Avalonia.Controls.Window { Width = width, Height = 800, Content = view };
            try
            {
                window.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var output = Path.Combine(AppContext.BaseDirectory, "current-nginx-previews");
                Directory.CreateDirectory(output);
                using var preview = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(width, 800));
                preview.Render(window); preview.Save(Path.Combine(output, "git-center-" + width + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            finally { window.Close(); }
        }
        foreach (var (type, command) in new[] { (typeof(GitClientViewModel), "InstallEngineCommand"),
            (typeof(RelaxKonOS.Client.Apps.FileServices.FileServicesViewModel), "InstallCommand"),
            (typeof(RelaxKonOS.Client.Apps.Squid.SquidViewModel), "InstallCommand"),
            (typeof(RelaxKonOS.Client.Apps.Tunnels.TunnelManagerViewModel), "InstallRuntimeCommand") })
            Check(type.GetProperty(command) is null, type.Name + " no longer exposes an application installation command.");
        var taskDialogRequests = 0;
        gitCenter.ShowTasksDialogAsync = () => { taskDialogRequests++; return Task.CompletedTask; };
        var draft = new ComponentChangeDraft("sing-box", "sing-box", "host", Guid.NewGuid(), "source", "release", "1.0", RuntimeChange.Install, true, false);
        gitCenter.Stage(draft);
        Check(taskDialogRequests == 1 && gitHandler.Calls == 1,
            "Creating a bulk draft requests its dialog without executing another host installation.");
        gitCenter.Stage(draft);
        Check(taskDialogRequests == 2 && gitCenter.Drafts.Count == 1 && gitHandler.Calls == 1,
            "Repeating the same draft reopens its task dialog without duplicating intent or host work.");
        gitCenter.OpenTasksCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(taskDialogRequests == 3 && gitCenter.Drafts.Count == 1,
            "Reopening the task dialog preserves the existing draft.");
        Console.WriteLine("Component installation entry checks passed (37 assertions).");
    }
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private sealed class EntryHandler : HttpMessageHandler
    {
        public int Calls; public string? Path; public string? Body; public string? Key;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Path = request.RequestUri!.AbsolutePath; Body = await request.Content!.ReadAsStringAsync(ct);
            Key = request.Headers.GetValues("Idempotency-Key").Single();
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
        }
    }
    private sealed class EntryLifecycle(string id) : IComponentLifecycleClient
    {
        private bool Pin => id is "nginx" or "frpc" or "frps";
        public Task<ComponentStateView> StateAsync(string componentId, CancellationToken ct) => Task.FromResult(new ComponentStateView(id,
            new(id, "host", 42, RuntimeOwnership.Absent, Pin ? ComponentSourceMode.ManagedArchive : ComponentSourceMode.SystemPackage,
                new("linux", "x64", null, null, Pin ? RuntimeInstallMode.Archive : RuntimeInstallMode.SystemPackage, "systemd"), null, false),
            Pin ? [new(ComponentSourceMode.ManagedArchive, "linux", true, null)]
                : [new(ComponentSourceMode.SystemPackage, "linux", true, null, "apt")], [],
            Pin ? [new("release", "1.0", "linux-x64", "https://releases.example/archive", new('a', 64), "tar.gz", 1024, [id])] : [], 5));
        public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
        {
            var state = await StateAsync(id, ct);
            return new(request, state.State, Pin ? ComponentFrozenTarget.FromPin(state.PinnedArchives!.Single())
                : ComponentFrozenTarget.FromPackage(new(id, "1.0", "amd64", "repo", "https://repo.example", "stable", "main", "https://repo.example/pkg.deb", new('a',64),1024, [])), [], []);
        }
    }
}
public class EntrySession : SiteEditorSession
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "GetAccessTokenAsync" ? Task.FromResult("token") : base.Invoke(method, args);
}
