using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.AspNetCore.Authentication;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Startup;

internal static class JointDeploymentAcceptance
{
    private const string ModuleId = "fixture-module";
    private const string AppIdText = "relaxkonos.welcome";

    public static void Run(string archive)
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var task = RunAsync(Path.GetFullPath(archive));
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Joint deployment acceptance did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string archive)
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "test-data");
        var root = Directory.CreateDirectory(Path.Combine(parent, "joint-deployment-" + Guid.NewGuid().ToString("N"))).FullName;
        var publish = Directory.CreateDirectory(Path.Combine(root, "module-publish")).FullName;
        var official = Directory.CreateDirectory(Path.Combine(root, "OfficialApps")).FullName;
        var serverRoot = Directory.CreateDirectory(Path.Combine(root, "server")).FullName;
        var packagePath = Path.Combine(official, AppIdText + ".roapp");
        TestHost? host = null;
        try
        {
            await PrepareDesktopArchiveAsync(archive, packagePath);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FixtureModule.dll"), Path.Combine(publish, "FixtureModule.dll"));
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = Path.Combine(root, "fixture-signing-key.pem");
            await File.WriteAllTextAsync(key, signer.ExportPkcs8PrivateKeyPem());
            var now = DateTimeOffset.UtcNow;
            var rid = ModulePackagePlatform.CurrentRid;
            var build = await ModulePackageBuilder.BuildAsync(new(publish, Path.Combine(root, "module-package"), ModuleId,
                "1.0.0", "joint-fixture-1.0.0-" + rid, "fixture", "official", rid, "FixtureModule.dll",
                "FixtureModule.FixtureInstalledModule", "joint-fixture", new string('a', 40), "official", 1,
                "https://modules.example/", key, "fixture-key", 3600,
                Ui: new("Joint Fixture", "", "", null, "UNLICENSED")), now);
            File.Delete(key);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleManagement:Authority"] = "managed",
                ["ModulePackages:Sources:official:SchemaVersion"] = "1",
                ["ModulePackages:Sources:official:SourceId"] = "official",
                ["ModulePackages:Sources:official:Revision"] = "1",
                ["ModulePackages:Sources:official:Enabled"] = "true",
                ["ModulePackages:Sources:official:PublisherId"] = "fixture",
                ["ModulePackages:Sources:official:Purpose"] = "official",
                ["ModulePackages:Sources:official:ArtifactOrigin"] = "https://modules.example/",
                ["ModulePackages:Sources:official:AuthorizedModuleIds:0"] = ModuleId,
                ["ModulePackages:Sources:official:PublicKeysSpki:fixture-key"] = build.PublicKeySpkiBase64,
                ["ModulePackages:Sources:official:MaximumValiditySeconds"] = "3600",
            }).Build();

            string serviceId;
            using (var store = new ModuleManagementStore(config, serverRoot)) serviceId = store.ServiceId;
            using (var catalog = new ModulePackageCatalogStore(serverRoot, serviceId))
                catalog.Accept(await File.ReadAllBytesAsync(build.CatalogPath), ModulePackageHostConfiguration.Source(config, "official"),
                    new ModulePackageContext(serviceId, rid, ModulePackageHostConfiguration.HostApiVersion, false),
                    ModulePackageCatalogStore.InitialRevision,
                    ModuleTestAuthentication.Admin, "joint-accept", now);

            host = await TestHost.StartAsync(serverRoot, config);
            var auth = DispatchProxy.Create<IAuthSession, SessionProxy>();
            var session = (SessionProxy)(object)auth;
            session.Url = host.Url;
            using var factory = new FixtureHttpFactory(await File.ReadAllBytesAsync(build.ArchivePath));
            using var services = new ServiceCollection().BuildServiceProvider();
            var windows = new RelaxKonOS.WindowManager.WindowManager();
            var applications = new ApplicationManager(windows, services);
            var drive = new VirtualSystemDrive(Path.Combine(root, "desktop-drive"));
            var permissions = DispatchProxy.Create<IAppPermissionManager, PermissionProxy>();
            var packages = new DeveloperPackageManager(applications, null!, windows, permissions, new Diagnostics(), drive, official);
            await packages.LoadInstalledAsync();
            using var trust = new ComponentCatalogSourceStore(Path.Combine(root, "component-trust"), "joint-fixture");
            var installer = new AppPackageInstallerService(packages, null!, trust);
            var journalPath = Path.Combine(root, "journal");
            JointAppDeploymentService Service() => new(factory, auth, installer, packages, applications, new(journalPath));
            var candidate = await installer.InspectLocalAsync(packagePath);
            var joint = Service();
            var review = await joint.PrepareAsync(candidate, [], default);
            Require(review.Plan.PreparedSteps.Length == 1 && review.Plan.OrderedReleases[0].Package.Id == ModuleId,
                "the desktop plans the missing signed module before the real desktop archive");
            await ExpectRestartAsync(joint, review);
            Require(!applications.IsRegistered(new AppId(AppIdText)) && InstalledModuleStore.Read(serverRoot).Single().ModuleId == ModuleId,
                "the first execution installs the signed module and leaves the desktop package pending");

            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            joint = Service();
            review = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Missing pending deployment.");
            using (var cancellation = new CancellationTokenSource())
            {
                var entered = factory.HoldNextEnableApply();
                var execution = joint.ExecuteAsync(review, cancellation.Token);
                await entered.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                try
                {
                    await execution.WaitAsync(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("Expected the blocked enable request to be cancelled.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Require(factory.SubmittedEnableApplies == 0 && !applications.IsRegistered(new AppId(AppIdText)),
                "cancelling an in-flight enable request leaves the application pending without submitting enable");
            factory.DropNextEnableResponse();
            await ExpectInterruptedEnableAsync(joint, review);
            Require(factory.EnablePreviewKeys.Distinct(StringComparer.Ordinal).Count() == 2,
                "retry after an interrupted enable requests a fresh server preview");
            await ExpectRestartAsync(joint, review);
            Require(factory.EnablePreviewKeys.Count == 2 && factory.SubmittedEnableApplies == 1,
                "a lost enable response is reconciled from the pending server state without submitting enable again");
            Require(!applications.IsRegistered(new AppId(AppIdText)), "the desktop stays pending while module enable awaits restart");

            await host.DisposeAsync();
            host = null;
            host = await TestHost.StartAsync(serverRoot, config);
            session.Url = host.Url;
            joint = Service();
            review = await joint.FindPendingAsync(candidate, default) ?? throw new InvalidOperationException("Missing deployment after enable.");
            var installed = await joint.ExecuteAsync(review, default);
            using var ready = new HttpClient { BaseAddress = new Uri(host.Url) };
            ready.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            var health = await ready.GetFromJsonAsync<ServerModuleHealthPageDto>(
                ServerModuleManagementRoutes.ModuleHealth, RelaxKonOSJsonOptions.Default);
            Require(installed.Id == AppIdText && applications.IsRegistered(new AppId(AppIdText))
                && health?.Modules.Single(item => item.ModuleId == ModuleId) is { Enabled: true, Version: "1.0.0", Status: "healthy" }
                && ModulePackageOperationStore.Read(serverRoot).Count == 1
                && await ready.GetStringAsync("/api/v1.0/fixture-module/ping") == "pong"
                && await ready.GetStringAsync("/fixture-core") == "ready"
                && await joint.FindPendingAsync(candidate, default) is null,
                "the resumed deployment verifies the running module, Core and desktop application registration");
            await packages.UninstallAsync(AppIdText);

            // Use the original desktop archive as a local prerequisite without a server dependency.
            File.Copy(archive, packagePath, overwrite: true);
            await using (var file = File.OpenRead(packagePath))
                await File.WriteAllTextAsync(packagePath + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
            await installer.InstallAsync(await installer.InspectLocalAsync(packagePath));
            var offlineArchive = Path.Combine(root, "dependent.roapp");
            await PrepareDesktopArchiveAsync(archive, offlineArchive, "fixture.offline",
                new DeploymentPackageKey(DeploymentPackageKind.Application, AppIdText));
            session.Online = false;
            var requests = factory.Requests;
            var offlineCandidate = await installer.InspectLocalAsync(offlineArchive);
            var offline = await joint.PrepareAsync(offlineCandidate, [], default);
            var offlineApp = await joint.ExecuteAsync(offline, default);
            Require(offlineApp.Id == "fixture.offline" && factory.Requests == requests
                && offline.Plan.OrderedReleases[0].Satisfied,
                "a real desktop package with a satisfied client-only dependency installs offline without module API calls");
            var original = packages.FindInstalled(AppIdText);
            await RewriteDesktopArchiveAsync(archive, packagePath, manifest => manifest["version"] = "2.0.0");
            var blocked = false;
            try { await installer.InstallAsync(await installer.InspectLocalAsync(packagePath)); }
            catch (InvalidOperationException error) when (error.Message.Contains("fixture.offline", StringComparison.Ordinal))
            {
                blocked = true;
            }
            Require(blocked && packages.FindInstalled(AppIdText) == original,
                "an incompatible replacement preserves the app version required by another installed app");
            await packages.UninstallAsync("fixture.offline");
            var upgraded = await installer.InstallAsync(await installer.InspectLocalAsync(packagePath));
            Require(upgraded.Version == "2.0.0", "removing the consuming app lets the same replacement proceed");
            await packages.UninstallAsync(AppIdText);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (Path.GetDirectoryName(root) == parent)
            {
                // Windows can retain the app's loaded package files until the process exits.
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static Task PrepareDesktopArchiveAsync(string source, string path,
        string appId = AppIdText, DeploymentPackageKey? dependency = null) =>
        RewriteDesktopArchiveAsync(source, path, manifest =>
        {
            var package = dependency ?? new DeploymentPackageKey(DeploymentPackageKind.ServerModule, ModuleId);
            manifest["id"] = appId;
            manifest["dependencies"] = new JsonObject
            {
                ["required"] = new JsonArray(new JsonObject
                {
                    ["package"] = new JsonObject { ["kind"] = (int)package.Kind, ["id"] = package.Id },
                    ["minimumVersion"] = "1.0.0",
                    ["maximumVersionExclusive"] = "2.0.0",
                }),
            };
        });

    private static async Task RewriteDesktopArchiveAsync(string source, string path, Action<JsonNode> edit)
    {
        File.Copy(source, path, overwrite: true);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var input = entry.Open()) manifest = JsonNode.Parse(input)!;
            edit(manifest);
            entry.Delete();
            using var output = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            await output.WriteAsync(manifest.ToJsonString());
        }
        await using var file = File.OpenRead(path);
        await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
    }

    private static async Task ExpectRestartAsync(JointAppDeploymentService service, JointAppDeploymentService.Review review)
    {
        try { await service.ExecuteAsync(review, default); }
        catch (InvalidOperationException error) when (error.Message == "deployment-module-AwaitingRestart") { return; }
        throw new InvalidOperationException("The installation should wait for a server restart.");
    }

    private static async Task ExpectInterruptedEnableAsync(JointAppDeploymentService service, JointAppDeploymentService.Review review)
    {
        try { await service.ExecuteAsync(review, default); }
        catch (HttpRequestException error) when (error.Message == "fixture-enable-apply-interrupted") { return; }
        throw new InvalidOperationException("Expected the enable request to be interrupted.");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class TestHost(WebApplication app, ModuleManagementStore store) : IAsyncDisposable
    {
        public string Url => app.Urls.Single();

        public static async Task<TestHost> StartAsync(string root, IConfiguration config)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root, EnvironmentName = Environments.Development, Args = [],
            });
            builder.Logging.ClearProviders();
            builder.Configuration.AddConfiguration(config);
            var installed = InstalledModuleCatalog.Load(root);
            var store = new ModuleManagementStore(builder.Configuration, root, installed);
            var modules = ServerModules.CreateManaged(store.DesiredIds, installed);
            var online = installed.Where(module => modules.IsEnabled(module.Id)).ToArray();
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            foreach (var module in online) module.ConfigureServices(builder, context);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(context);
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy,
                policy => policy.RequireAuthenticatedUser().RequireAssertion(user => ServerModuleManagementEndpoints.CanManage(user.User))));
            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet(ServerApiRoutes.Modules, async (HttpContext http, CancellationToken ct) => Results.Ok(
                    (await modules.DescribeAsync(http.RequestServices, context, ct))
                        .Select(module => ServerModuleManagementEndpoints.ForActor(module, http.User)))).RequireAuthorization();
                app.MapGet("/fixture-core", () => "ready");
                app.MapServerModuleManagementEndpoints();
                foreach (var module in online) module.MapEndpoints(app, context);
                app.Urls.Add("http://127.0.0.1:0");
                await app.StartAsync();
                store.VerifyStartup(modules.EnabledIds);
                ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root),
                    store.ProcessGeneration, DateTimeOffset.UtcNow);
                return new(app, store);
            }
            catch { await app.DisposeAsync(); store.Dispose(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            store.Dispose();
        }
    }

    private sealed class FixtureHttpFactory(byte[] signedArchive) : IHttpClientFactory, IDisposable
    {
        private readonly ArchiveUploadHandler handler = new(signedArchive);
        private HttpClient? http;
        public IReadOnlyList<string> EnablePreviewKeys => handler.EnablePreviewKeys;
        public int SubmittedEnableApplies => handler.SubmittedEnableApplies;
        public int Requests => handler.Requests;
        public Task HoldNextEnableApply()
        {
            handler.EnableEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return handler.EnableEntered.Task;
        }
        public void DropNextEnableResponse() => handler.DropEnableResponse = true;
        public HttpClient CreateClient(string name) => http ??= new(handler);
        public void Dispose() => http?.Dispose();
    }

    // Use the real server staging endpoint in place of the external HTTPS artifact host.
    private sealed class ArchiveUploadHandler(byte[] signedArchive) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        public List<string> EnablePreviewKeys { get; } = [];
        public TaskCompletionSource? EnableEntered { get; set; }
        public bool DropEnableResponse { get; set; }
        public int SubmittedEnableApplies { get; private set; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            request.Headers.Add("test-user", ModuleTestAuthentication.Admin);
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == ServerModuleManagementRoutes.Plans)
            {
                var plan = await request.Content!.ReadFromJsonAsync<ServerModulePlanRequest>(
                    RelaxKonOSJsonOptions.Default, cancellationToken);
                if (plan?.Action == ServerModuleAction.Enable) EnablePreviewKeys.Add(plan.IdempotencyKey);
            }
            if (request.Method == HttpMethod.Post
                && path?.StartsWith(ServerModuleManagementRoutes.Plans + "/", StringComparison.Ordinal) == true
                && path.EndsWith("/apply", StringComparison.Ordinal))
            {
                if (EnableEntered is { } entered)
                {
                    EnableEntered = null;
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                var drop = DropEnableResponse;
                DropEnableResponse = false;
                SubmittedEnableApplies++;
                var applied = await base.SendAsync(request, cancellationToken);
                if (!drop) return applied;
                using (applied) applied.EnsureSuccessStatusCode();
                throw new HttpRequestException("fixture-enable-apply-interrupted");
            }
            if (request.RequestUri?.AbsolutePath != ServerModuleManagementRoutes.ModuleStoreDownload)
                return await base.SendAsync(request, cancellationToken);
            using var upload = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri, ServerModuleManagementRoutes.PackageUpload));
            upload.Headers.Add("test-user", ModuleTestAuthentication.Admin);
            upload.Content = new ByteArrayContent(signedArchive);
            return await base.SendAsync(upload, cancellationToken);
        }
    }

    public class SessionProxy : DispatchProxy
    {
        private readonly Guid actor = Guid.Parse(ModuleTestAuthentication.Admin);
        private SessionDto login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default, default,
            SessionStatus.Active, Guid.Parse(ModuleTestAuthentication.Admin), "test", default);
        private string url = "";
        public bool Online { get; set; } = true;
        public string Url
        {
            get => url;
            set
            {
                url = value;
                login = login with { Id = Guid.NewGuid() };
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_State" => Online ? AuthSessionState.Authenticated : AuthSessionState.Unauthenticated,
            "get_ServiceId" => Online ? "local-joint-test" : null,
            "get_EffectiveBaseUrl" => Url,
            "get_CurrentSession" => Online ? login : null,
            "get_CurrentUser" => Online ? new UserDto(actor, "controller", HostPlatformKind.Windows, "fixture", default, null) : null,
            "get_CurrentServer" => null,
            _ => throw new NotSupportedException(method?.Name),
        };
    }

    public class PermissionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "Clear" ? null : throw new NotSupportedException(method?.Name);
    }

    private sealed class Diagnostics : RelaxKonOS.AppSDK.IAppActivationDiagnostics
    {
        public void Record(string message) => Console.WriteLine(message);
    }
}
