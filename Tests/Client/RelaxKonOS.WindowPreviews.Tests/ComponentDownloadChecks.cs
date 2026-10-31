using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ComponentDownloadChecks
{
    internal static void Run()
    {
        foreach (var architecture in new[] { "amd64", "arm64" })
        {
            using var handler = new HostReleaseHandler(architecture);
            using var http = new HttpClient(handler);
            var client = new ComponentLifecycleClient(http, DispatchProxy.Create<IAuthSession, EntrySession>());
            var found = client.ReleasesAsync("mihomo", true, default).GetAwaiter().GetResult();
            Check(found.Single().Rid == (architecture == "amd64" ? "linux-x64" : "linux-arm64") && handler.ReleaseRequests == 1,
                "Linux APT " + architecture + " state reaches official release discovery with the server RID");
        }
        foreach (var id in new[] { "nginx", "mihomo", "frpc", "frps" })
        {
            using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>()) { CanRead = true, CanManage = true };
            var releases = new OfficialEntryLifecycle();
            using var model = new ComponentCenterViewModel(runtime, new(), "https://test.invalid", Guid.NewGuid(), lifecycle: releases, hostPlatform: "windows");
            using var handler = new InstallHandler();
            using var http = new HttpClient(handler);
            var service = id switch { "nginx" => InstallationServiceId.Nginx, "mihomo" => InstallationServiceId.Mihomo, _ => InstallationServiceId.Frp };
            var download = new DownloadFactory();
            model.AttachPackageInstallation(id, new(new InstallationClient(http, DispatchProxy.Create<IAuthSession, EntrySession>(), download), null!, service,
                "relaxkonos.component-center", () => Task.CompletedTask, () => Task.FromResult<string?>(null)));
            model.SelectedNode = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == id);
            model.LoadOfficialReleasesAsync(false).GetAwaiter().GetResult();
            Check(model.ShowOfficialDownloads && !model.ShowPackageFreeze && !model.HasRuntimeDetails && model.OfficialReleases.Count == 2,
                id + " shows a multi-version selector without a signing-source setup or plan screen");
            model.SelectedOfficialRelease = model.OfficialReleases[1];
            var selected = model.OfficialVersion;
            Check(!model.OpenOfficialDownloadCommand.CanExecute(null), id + " disables download without a browser handler");
            string? openedUrl = null;
            model.OpenComponentDownloadAsync = url => { openedUrl = url; return Task.CompletedTask; };
            Complete(model.OpenOfficialDownloadCommand.ExecuteAsync(null));
            Check(openedUrl == model.SelectedOfficialRelease!.DownloadUrl, id + " opens the selected archive when resolved");
            model.SelectLocalComponentPackageAsync = _ => Task.FromResult<ComponentPackageSelection?>(new("uploaded-package", "local.zip"));
            model.SelectServerComponentPackageAsync = _ => Task.FromResult<ComponentPackageSelection?>(new("server-package", "/server/runtime.zip"));
            foreach (var method in new[] { ComponentDownloadMethod.ServerDownload, ComponentDownloadMethod.ClientDownload, ComponentDownloadMethod.LocalFile, ComponentDownloadMethod.ServerFile })
            {
                model.SelectedDownloadMethod = model.DownloadMethods.Single(choice => choice.Method == method);
                if (model.IsFileSource)
                {
                    Check(!model.CanInstallOfficial && model.SelectedPackageFile is null,
                        id + " file source requires explicit file selection before installation");
                    model.OfficialVersion = selected;
                    Complete(model.SelectPackageFileCommand.ExecuteAsync(null));
                    Check(model.SelectedPackageFile is not null && model.CanInstallOfficial && !model.ShowOnlineVersions,
                        id + " selected file is visible and enables installation without the online selector");
                }
                Complete(model.InstallOfficialCommand.ExecuteAsync(null));
                using var body = JsonDocument.Parse(handler.Body!);
                Check(handler.Path == InstallationApiRoutes.Start(service, InstallationOperationKind.Install)
                    && body.RootElement.GetProperty("version").GetString() == selected,
                    id + " " + method + " submits the user's selected version to the owning installer");
                Check(body.RootElement.GetProperty("fileReferenceId").GetString() == (method switch
                    { ComponentDownloadMethod.LocalFile => "uploaded-package", ComponentDownloadMethod.ServerFile => "server-package", ComponentDownloadMethod.ClientDownload => "desktop-package", _ => null }),
                    id + " " + method + " preserves the selected package reference");
            }
            var calls = handler.Calls;
            model.SelectServerComponentPackageAsync = _ => Task.FromResult<ComponentPackageSelection?>(null);
            model.SelectedDownloadMethod = model.DownloadMethods.Single(choice => choice.Method == ComponentDownloadMethod.LocalFile);
            model.SelectedDownloadMethod = model.DownloadMethods.Single(choice => choice.Method == ComponentDownloadMethod.ServerFile);
            Complete(model.SelectPackageFileCommand.ExecuteAsync(null));
            model.InstallOfficialCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(handler.Calls == calls, id + " cancelled file selection does not submit installation");
            var pendingFile = new TaskCompletionSource<ComponentPackageSelection?>();
            model.SelectServerComponentPackageAsync = _ => pendingFile.Task;
            var selecting = model.SelectPackageFileCommand.ExecuteAsync(null);
            model.SelectedDownloadMethod = model.DownloadMethods.Single(choice => choice.Method == ComponentDownloadMethod.LocalFile);
            pendingFile.SetResult(new("stale-server-file", "/server/stale.zip"));
            Complete(selecting);
            Check(model.SelectedPackageFile is null && !model.CanInstallOfficial,
                id + " ignores a file selection completed after switching the source");
            releases.Fail = true;
            model.LoadOfficialReleasesAsync(true).GetAwaiter().GetResult();
            Check(model.OfficialReleases.Count == 0 && !model.CanInstallOfficial, id + " failed refresh does not reuse an old selected version");
            foreach (var (failure, key) in new (Exception, string)[] {
                (new TaskCanceledException(), "timeout"),
                (new HttpRequestException("denied", null, HttpStatusCode.TooManyRequests), "rate_limited"),
                (new InvalidOperationException("component.platform_unsupported"), "unsupported"),
                (new InvalidDataException("component.state_unavailable"), "state_unavailable"),
                (new System.Text.Json.JsonException(), "failed") })
            {
                releases.Failure = failure;
                model.LoadOfficialReleasesAsync(true).GetAwaiter().GetResult();
                Check(model.OfficialDownloadNotice.Resolve() == RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.download." + key).Resolve(),
                    id + " reports " + key + " without displaying raw exception text");
            }
            releases.Failure = null;
            var officialPage = id switch
            {
                "nginx" => "https://nginx.org/en/download.html",
                "mihomo" => "https://github.com/MetaCubeX/mihomo/releases",
                _ => "https://github.com/fatedier/frp/releases"
            };
            Check(model.OpenOfficialDownloadCommand.CanExecute(null), id + " keeps manual download available after discovery fails");
            Complete(model.OpenOfficialDownloadCommand.ExecuteAsync(null));
            Check(openedUrl == officialPage && model.OfficialDownloadUrl == officialPage,
                id + " opens its official release page after discovery fails instead of a stale archive");
            model.OfficialVersion = selected;
            model.SelectServerComponentPackageAsync = _ => Task.FromResult<ComponentPackageSelection?>(new("server-package", "/server/runtime.zip"));
            Complete(model.SelectPackageFileCommand.ExecuteAsync(null));
            Check(model.CanInstallOfficial, id + " supports manual version and existing archive when the network is unavailable");
        }
    }
    internal static void Render(string output, string suffix)
    {
        using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>()) { CanRead = true, CanManage = true };
        using var model = new ComponentCenterViewModel(runtime, new(), "https://host.example", Guid.NewGuid(), lifecycle: new OfficialEntryLifecycle(), hostPlatform: "windows");
        model.AttachPackageInstallation("nginx", new(null!, null!, InstallationServiceId.Nginx, "preview", () => Task.CompletedTask, () => Task.FromResult<string?>(null)));
        model.SelectedNode = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "nginx");
        model.RefreshPackageInstallationAsync("nginx").GetAwaiter().GetResult();
        model.LoadOfficialReleasesAsync(false).GetAwaiter().GetResult();
        var view = new ComponentCenterWorkspace(model);
        var window = new Window { Width = 1060, Height = 800, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, suffix + "OfficialDownloads.png"));
        model.SelectedDownloadMethod = model.DownloadMethods.Single(choice => choice.Method == ComponentDownloadMethod.LocalFile);
        model.SelectedPackageFile = new("preview-file", "nginx-1.30.6.zip");
        model.OfficialVersion = "1.30.6";
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using (var fileFrame = new RenderTargetBitmap(new PixelSize(1060, 800)))
        { fileFrame.Render(window); fileFrame.Save(Path.Combine(output, suffix + "FileInstall.png")); }
        window.Close(); Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Installation test did not complete");
        task.GetAwaiter().GetResult();
    }
    private sealed class HostReleaseHandler(string architecture) : HttpMessageHandler
    {
        internal int ReleaseRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/state"))
                body = new ComponentStateView("mihomo", new("mihomo", "host", 1, RuntimeOwnership.Absent,
                    ComponentSourceMode.SystemPackage, new("linux", architecture, null, null, RuntimeInstallMode.SystemPackage, "systemd"), null, false), [], []);
            else
            {
                ReleaseRequests++;
                var rid = architecture == "amd64" ? "linux-x64" : "linux-arm64";
                body = new[] { RelaxKonOS.ManagedRuntimes.ComponentPackages.OfficialComponentReleases.Describe("mihomo", rid, "v1.19.30") };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(body, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)) });
        }
    }
    private sealed class InstallHandler : HttpMessageHandler
    {
        public string? Body, Path; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/package", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new InstallationFileReferenceDto("desktop-package", "runtime.zip", 3, DateTimeOffset.UtcNow.AddMinutes(20))) };
            Calls++; Path = request.RequestUri!.AbsolutePath; Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
        }
    }
    private sealed class DownloadFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DownloadHandler());
        private sealed class DownloadHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
internal sealed class OfficialEntryLifecycle : IComponentLifecycleClient, IComponentReleaseClient
{
    public bool Fail;
    public Exception? Failure;
    public Task<ComponentDownloadRelease[]> ReleasesAsync(string id, bool refresh, CancellationToken ct)
    {
        if (Fail) throw Failure ?? new HttpRequestException();
        return Task.FromResult(new[] { Release(id, id == "nginx" ? "1.30.6" : id == "mihomo" ? "v1.19.30" : "v0.71.0"),
            Release(id, id == "nginx" ? "1.28.3" : id == "mihomo" ? "v1.19.29" : "v0.70.0") });
    }
    private static ComponentDownloadRelease Release(string id, string version) => new(version, "win-x64", "https://nginx.org/download/nginx-" + version + ".zip", id + ".zip", "zip");
    public Task<ComponentStateView> StateAsync(string id, CancellationToken ct) => Task.FromResult(new ComponentStateView(id,
        new(id, "host", 1, RuntimeOwnership.Absent, ComponentSourceMode.ManagedArchive,
            new("windows", "x64", null, null, RuntimeInstallMode.Archive, "scm"), null, false), [], []));
    public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct) => throw new Exception("Official installation requested a signing catalog plan");
}
