using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.ImageMirrors;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Firewall;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.ImageMirrors;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.WebServer;
using RelaxKonOS.Server.Tailscale;

internal static class ModulePackageHttpChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool isolated = false, bool preinstalled = false, bool liveDocker = false)
    {
        if (isolated)
        {
            var report = check;
            check = (passed, message) => report(passed, "v2 third-party " + message);
        }
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "package-http-" + Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(root, "publish");
        var output = Path.Combine(root, "package");
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(output);
        try
        {
            var assembly = isolated ? "HostFixture.dll" : "FixtureModule.dll";
            var purpose = isolated ? "third-party" : "official";
            File.Copy(Path.Combine(AppContext.BaseDirectory, assembly), Path.Combine(publish, assembly));
            await File.WriteAllTextAsync(Path.Combine(publish, "asset.txt"), "http package asset");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keyPath = Path.Combine(root, "signing-key.pem");
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            var rid = RelaxKonOS.ModulePackaging.ModulePackagePlatform.CurrentRid;
            var now = DateTimeOffset.UtcNow;
            var releaseId = "fixture-http-1.0.0-" + rid;
            var packageOptions = new ModulePackageBuildOptions(publish, output, "fixture-http",
                "1.0.0", releaseId, "fixture", purpose, rid, assembly,
                isolated ? "HostFixture.FixtureHttpHostEntry" : "FixtureModule.FixtureInstalledModule", "fixture-build", new string('a', 40), "official", 1,
                "https://modules.example/", keyPath, "builder-key", 3600,
                HostApiMinimum: isolated ? "2.0.0" : "1.0.0", HostApiMaximumExclusive: isolated ? "3.0.0" : "2.0.0",
                Runtime: isolated ? "module-host" : "in-process",
                Privileges: isolated ? [ModuleCapabilityFixtures.Service("fixture.service", "restart")] : [],
                Ui: new("Sample Module", "", "", null, "UNLICENSED"));
            var built = await ModulePackageBuilder.BuildAsync(packageOptions, now);
            var incompatible = isolated ? null : await ModulePackageBuilder.BuildAsync(packageOptions with
            {
                Version = "2.0.0", ReleaseId = "fixture-http-2.0.0-" + rid,
            }, now);
            var dependent = isolated ? null : await ModulePackageBuilder.BuildAsync(packageOptions with
            {
                ModuleId = "fixture-dependent",
                ReleaseId = "fixture-dependent-1.0.0-" + rid,
                Dependencies = [new("fixture-http", "1.0.0", "2.0.0")],
                Ui = new("Dependent Module", "", "", null, "UNLICENSED"),
            }, now);
            var database = isolated ? null : await ModulePackageBuilder.BuildAsync(packageOptions with
            {
                ModuleId = ServerModules.DatabaseId,
                ReleaseId = "fixture-database-" + rid,
                Runtime = "module-host",
                HostApiMinimum = "2.4.0",
                HostApiMaximumExclusive = "3.0.0",
                Ui = new("Database Observation", "", "", null, "UNLICENSED"),
            }, now);
            ModulePackageBuildResult[] tailscale = [];
            ModulePackageBuildResult[] firewall = [];
            ModulePackageBuildResult[] git = [];
            ModulePackageBuildResult[] docker = [];
            ModulePackageBuildResult[] webServer = [];
            ModulePackageBuildResult[] certificates = [];
            ModulePackageBuildResult[] proxy = [];
            ModulePackageBuildResult? incompatibleGit = null;
            if (!isolated)
            {
                var tailscalePublish = Path.Combine(AppContext.BaseDirectory, "Modules", "Tailscale");
                var tailscaleOptions = packageOptions with
                {
                    PublishDirectory = tailscalePublish,
                    OutputDirectory = Path.Combine(root, "tailscale-package"),
                    ModuleId = "relaxkonos.tailscale",
                    ReleaseId = "tailscale-1.0.0-" + rid,
                    EntryAssembly = CollectibleTailscaleExecution.AssemblyFile,
                    EntryType = CollectibleTailscaleExecution.EntryType,
                    Capabilities = [ServerCapabilities.Tailscale],
                    Ui = new("Tailscale", "", "", null, "UNLICENSED"),
                };
                tailscale = [await ModulePackageBuilder.BuildAsync(tailscaleOptions, now),
                    await ModulePackageBuilder.BuildAsync(tailscaleOptions with
                    { Version = "2.0.0", ReleaseId = "tailscale-2.0.0-" + rid }, now)];
                var firewallOptions = tailscaleOptions with
                {
                    PublishDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "Firewall"),
                    OutputDirectory = Path.Combine(root, "firewall-package"),
                    ModuleId = ServerModules.FirewallId,
                    ReleaseId = "firewall-1.0.0-" + rid,
                    EntryAssembly = CollectibleFirewallExecution.AssemblyFile,
                    EntryType = CollectibleFirewallExecution.EntryType,
                    Capabilities = [ServerCapabilities.Firewall],
                    Ui = new("Firewall", "", "", null, "UNLICENSED"),
                };
                firewall = [await ModulePackageBuilder.BuildAsync(firewallOptions, now),
                    await ModulePackageBuilder.BuildAsync(firewallOptions with
                    { Version = "2.0.0", ReleaseId = "firewall-2.0.0-" + rid }, now)];
                var gitOptions = tailscaleOptions with
                {
                    PublishDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "Git"),
                    OutputDirectory = Path.Combine(root, "git-package"),
                    ModuleId = ServerModules.GitId,
                    ReleaseId = "git-1.0.0-" + rid,
                    EntryAssembly = CollectibleGitExecution.AssemblyFile,
                    EntryType = CollectibleGitExecution.EntryType,
                    Capabilities = [ServerCapabilities.Git],
                    CapabilityContractVersions = new Dictionary<string, string> { [ServerCapabilities.Git] = GitApiRoutes.CapabilityContractVersion },
                    Ui = new("Git", "", "", null, "UNLICENSED"),
                };
                git = [await ModulePackageBuilder.BuildAsync(gitOptions, now),
                    await ModulePackageBuilder.BuildAsync(gitOptions with
                    { Version = "2.0.0", ReleaseId = "git-2.0.0-" + rid }, now)];
                incompatibleGit = await ModulePackageBuilder.BuildAsync(gitOptions with
                { Version = "3.0.0", ReleaseId = "git-3.0.0-" + rid, CapabilityContractVersions = null }, now);
                var dockerOptions = tailscaleOptions with
                {
                    PublishDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "Docker"),
                    OutputDirectory = Path.Combine(root, "docker-package"),
                    ModuleId = ServerModules.DockerId,
                    ReleaseId = "docker-1.0.0-" + rid,
                    EntryAssembly = DockerComposeExecution.AssemblyFile,
                    EntryType = DockerComposeExecution.EntryType,
                    Capabilities = [ServerCapabilities.Docker],
                    Ui = new("Docker", "", "", null, "UNLICENSED"),
                };
                docker = [await ModulePackageBuilder.BuildAsync(dockerOptions, now),
                    await ModulePackageBuilder.BuildAsync(dockerOptions with
                    { Version = "2.0.0", ReleaseId = "docker-2.0.0-" + rid }, now)];
                var webOptions = dockerOptions with
                {
                    PublishDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "WebServer"),
                    OutputDirectory = Path.Combine(root, "webserver-package"),
                    ModuleId = ServerModules.WebServerId,
                    ReleaseId = "webserver-1.0.0-" + rid,
                    EntryAssembly = WebServerExecutionCode.AssemblyFile,
                    EntryType = WebServerExecutionCode.EntryType,
                    Dependencies = [new(ServerModules.CertificatesId, "0.0.0", "9999.0.0")],
                    Capabilities = [ServerCapabilities.WebServer],
                    Ui = new("Web Server", "", "", null, "UNLICENSED"),
                };
                webServer = [await ModulePackageBuilder.BuildAsync(webOptions, now),
                    await ModulePackageBuilder.BuildAsync(webOptions with
                    { Version = "2.0.0", ReleaseId = "webserver-2.0.0-" + rid }, now)];
                var certificateOptions = dockerOptions with
                {
                    PublishDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "Certificates"),
                    OutputDirectory = Path.Combine(root, "certificates-package"),
                    ModuleId = ServerModules.CertificatesId,
                    ReleaseId = "certificates-1.0.0-" + rid,
                    EntryAssembly = AcmeExecution.AssemblyFile,
                    EntryType = AcmeExecution.EntryType,
                    Capabilities = [ServerCapabilities.Certificates],
                    Ui = new("Certificates", "", "", null, "UNLICENSED"),
                };
                certificates = [await ModulePackageBuilder.BuildAsync(certificateOptions, now),
                    await ModulePackageBuilder.BuildAsync(certificateOptions with
                    { Version = "2.0.0", ReleaseId = "certificates-2.0.0-" + rid }, now)];
                var proxyOptions = dockerOptions with
                {
                    PublishDirectory = Path.Combine(AppContext.BaseDirectory, "Modules", "Proxy"),
                    OutputDirectory = Path.Combine(root, "proxy-package"),
                    ModuleId = ServerModules.ProxyId,
                    ReleaseId = "proxy-1.0.0-" + rid,
                    EntryAssembly = ProxyExecutionLoadContext.AssemblyFile,
                    EntryType = ProxyConnectionExecutionGate.OverviewEntryType,
                    Capabilities = [ServerCapabilities.Proxy, ServerCapabilities.GatewayProxy, ServerCapabilities.ProxyServer],
                    Ui = new("Proxy", "", "", null, "UNLICENSED"),
                };
                proxy = [await ModulePackageBuilder.BuildAsync(proxyOptions, now),
                    await ModulePackageBuilder.BuildAsync(proxyOptions with
                    { Version = "2.0.0", ReleaseId = "proxy-2.0.0-" + rid }, now)];
            }
            var envelope = dependent is null ? await File.ReadAllBytesAsync(built.CatalogPath)
                : (await ModulePackageBuilder.SignCatalogAsync(new ModulePackageCatalog(1, "official", 1, now,
                    now.AddHours(1), [Release(built), Release(dependent), Release(incompatible!), Release(database!),
                        .. tailscale.Select(Release), .. firewall.Select(Release), .. git.Select(Release),
                        .. docker.Select(Release), .. webServer.Select(Release), .. certificates.Select(Release), .. proxy.Select(Release), Release(incompatibleGit!)], [], []), "builder-key", keyPath)).Envelope;
            static ModulePackageRelease Release(ModulePackageBuildResult result) => new(result.Manifest,
                result.ManifestSha256, result.ArchiveSha256, result.ArchiveBytes,
                $"https://modules.example/{Path.GetFileName(result.ArchivePath)}");
            var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "fixture", purpose,
                "https://modules.example/", dependent is null ? ["fixture-http"]
                    : ["fixture-http", "fixture-dependent", ServerModules.DatabaseId, "relaxkonos.tailscale",
                        ServerModules.FirewallId, ServerModules.GitId, ServerModules.DockerId, ServerModules.WebServerId,
                        ServerModules.CertificatesId, ServerModules.ProxyId],
                new Dictionary<string, string> { ["builder-key"] = built.PublicKeySpkiBase64 }, 3600);
            await File.WriteAllTextAsync(Path.Combine(root, "appsettings.host.json"), JsonSerializer.Serialize(new
            {
                ModuleManagement = new { Authority = "managed" },
                ModulePackages = new { Sources = new Dictionary<string, ModulePackageTrustPolicy> { ["official"] = policy } },
            }, RelaxKonOSJsonOptions.Default));

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile("appsettings.host.json", optional: false, reloadOnChange: false);
            builder.Configuration["ModulePackages:Sources:official:CatalogUri"] = "https://modules.example/stable/catalog.json";
            string serviceId;
            using (var initialization = new ModuleManagementStore(builder.Configuration, root)) serviceId = initialization.ServiceId;
            var configuredSources = ModulePackageStore.Sources(root, builder.Configuration, builder.Environment, now);
            check(configuredSources.CatalogRevision == ModulePackageCatalogStore.InitialRevision
                && configuredSources.Sources is [{ SourceId: "official", Status: "refresh-required", Sequence: 0, CatalogUriConfigured: true }]
                && ModulePackageStore.Search(root, builder.Configuration, builder.Environment, null, null, 0, 25, now).Items.Count == 0,
                "a configured remote source is refreshable before first acceptance and exposes no unsigned releases");
            var context = ModulePackageHostConfiguration.Context(serviceId, builder.Configuration, builder.Environment);
            using (var catalog = new ModulePackageCatalogStore(root, serviceId))
                catalog.Accept(envelope, policy, context, ModulePackageCatalogStore.InitialRevision,
                    "package-http", "accept-package-http", now);

            if (preinstalled)
            {
                var offline = await InstalledModuleInstaller.InstallAsync(root, builder.Configuration,
                    builder.Environment, "official", built.ArchivePath, releaseId, now);
                check(ModuleTrustStore.Read(root, offline) is null, "offline installation does not grant code trust");
            }
            var installedCatalog = InstalledModuleCatalog.Load(root);
            using var management = new ModuleManagementStore(builder.Configuration, root, installedCatalog);
            var modules = preinstalled ? ServerModules.Create(builder.Configuration, installedCatalog)
                : ServerModules.CreateManaged(management.DesiredIds);
            builder.Services.AddSingleton(management);
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy,
                item => item.RequireAuthenticatedUser().RequireAssertion(context =>
                    ServerModuleManagementEndpoints.CanManage(context.User))));

            await using var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapServerModuleManagementEndpoints();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            if (database is not null)
            {
                var databaseOffers = await client.GetFromJsonAsync<ServerModuleStorePageDto>(
                    ServerModuleManagementRoutes.ModuleStoreSearch + "?q=" + ServerModules.DatabaseId, RelaxKonOSJsonOptions.Default);
                check(database.Manifest.Capabilities.Length == 0 && databaseOffers?.Items is
                    [{ ModuleId: ServerModules.DatabaseId, Capabilities: [ServerCapabilities.Database], RidCompatible: true }],
                    "the accepted official database offer advertises the Core-owned database capability");
            }

            foreach (var query in new[] { "fixture-http", "FixtureHttp", " sample module " })
            {
                var store = await client.GetFromJsonAsync<ServerModuleStorePageDto>(
                    ServerModuleManagementRoutes.ModuleStoreSearch + "?q=" + Uri.EscapeDataString(query), RelaxKonOSJsonOptions.Default);
                check(store?.Items.Count == (incompatible is null ? 1 : 2)
                    && store.Items.Any(item => item.SourceId == "official"
                        && item.ModuleId == "fixture-http" && item.ArtifactSha256 == built.ArchiveSha256),
                    $"module store search finds the accepted signed release for '{query}'");
            }
            var sources = await client.GetFromJsonAsync<ServerModuleStoreSourcePageDto>(
                ServerModuleManagementRoutes.ModuleStoreSources, RelaxKonOSJsonOptions.Default);
            check(sources?.Sources.Count == 1 && sources.Sources[0].SourceId == "official"
                && sources.Sources[0].Status == "current" && sources.Sources[0].ReleaseCount == (dependent is null ? 1 : 5 + tailscale.Length + firewall.Length + git.Length + docker.Length + webServer.Length + certificates.Length + proxy.Length),
                "module store source inventory reports the accepted catalog revision");

            using var downloadHandler = new PackageDownloadHandler(await File.ReadAllBytesAsync(built.ArchivePath));
            var downloaded = await ModulePackageDownloader.DownloadAsync(root, builder.Configuration, builder.Environment,
                "official", releaseId, now, downloadHandler);
            check(downloaded.Bytes == built.ArchiveBytes && downloaded.Sha256 == built.ArchiveSha256,
                "module store download writes the exact catalog artifact into staging");
            if (incompatibleGit is not null)
            {
                var gitOffers = await client.GetFromJsonAsync<ServerModuleStorePageDto>(
                    ServerModuleManagementRoutes.ModuleStoreSearch + "?q=" + ServerModules.GitId, RelaxKonOSJsonOptions.Default);
                check(gitOffers?.Items.Single(item => item.Version == "3.0.0").BlockedBy is
                    [{ ProblemCode: ServerModules.PackageModuleIdConflict }],
                    "the store blocks a correctly signed Git package without its required capability contract");
                try
                {
                    await ModulePackageDownloader.DownloadAsync(root, builder.Configuration, builder.Environment,
                        "official", incompatibleGit.Manifest.ReleaseId, now, downloadHandler);
                    check(false, "the blocked Git package must not download");
                }
                catch (ModulePackageException error) when (error.ProblemCode == ServerModules.PackageModuleIdConflict)
                {
                    check(true, "the downloader rejects the same package before using the delivery handler");
                }
                using var conflictingUpload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                    new ByteArrayContent(await File.ReadAllBytesAsync(incompatibleGit.ArchivePath)));
                conflictingUpload.EnsureSuccessStatusCode();
                var conflictingStage = (await conflictingUpload.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(RelaxKonOSJsonOptions.Default))!;
                using var conflictingPlan = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackagePlans,
                    new ServerModulePackageInstallRequest("official", incompatibleGit.Manifest.ReleaseId, conflictingStage.UploadId), RelaxKonOSJsonOptions.Default);
                using var conflictProblem = JsonDocument.Parse(await conflictingPlan.Content.ReadAsStringAsync());
                check(!conflictingPlan.IsSuccessStatusCode && conflictProblem.RootElement.GetProperty("problemCode").GetString() == ServerModules.PackageModuleIdConflict,
                    "a manual upload cannot bypass the same bundled module replacement check");
            }

            string? dependentApplyRoute = null;
            if (dependent is not null)
            {
                using var dependentUpload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                    new ByteArrayContent(await File.ReadAllBytesAsync(dependent.ArchivePath)));
                dependentUpload.EnsureSuccessStatusCode();
                var stagedDependent = (await dependentUpload.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(
                    RelaxKonOSJsonOptions.Default))!;
                using var dependentPreview = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackagePlans,
                    new ServerModulePackageInstallRequest("official", dependent.Manifest.ReleaseId, stagedDependent.UploadId),
                    RelaxKonOSJsonOptions.Default);
                dependentPreview.EnsureSuccessStatusCode();
                var frozenDependent = (await dependentPreview.Content.ReadFromJsonAsync<ServerModulePackagePlanDto>(
                    RelaxKonOSJsonOptions.Default))!;
                dependentApplyRoute = ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", frozenDependent.PlanId.ToString());
                using var premature = await client.PostAsync(dependentApplyRoute, null);
                using var problem = JsonDocument.Parse(await premature.Content.ReadAsStringAsync());
                check(!premature.IsSuccessStatusCode && InstalledModuleStore.Read(root).Count == 0
                    && problem.RootElement.GetProperty("problemCode").GetString() == "module-package-dependency-missing",
                    "a signed dependent package can be frozen before its predecessor, but cannot apply before installation");
            }

            var upload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                new ByteArrayContent(await File.ReadAllBytesAsync(built.ArchivePath)));
            upload.EnsureSuccessStatusCode();
            var uploaded = (await upload.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(RelaxKonOSJsonOptions.Default))!;
            check(uploaded.Bytes == built.ArchiveBytes && uploaded.Sha256 == built.ArchiveSha256,
                "HTTP upload stages the exact package bytes and digest");

            var autoResponse = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackageAutoPlan,
                new ServerModulePackageAutoPlanRequest(uploaded.UploadId), RelaxKonOSJsonOptions.Default);
            autoResponse.EnsureSuccessStatusCode();
            var autoPlan = (await autoResponse.Content.ReadFromJsonAsync<ServerModulePackagePlanDto>(RelaxKonOSJsonOptions.Default))!;
            check(autoPlan.ModuleId == "fixture-http" && autoPlan.Version == "1.0.0" && autoPlan.SourceId == "official"
                && autoPlan.ArtifactSha256 == built.ArchiveSha256,
                "HTTP auto plan resolves an uploaded archive through the accepted catalog without caller source hints");

            var planResponse = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackagePlans,
                new ServerModulePackageInstallRequest("official", releaseId, uploaded.UploadId), RelaxKonOSJsonOptions.Default);
            planResponse.EnsureSuccessStatusCode();
            var plan = (await planResponse.Content.ReadFromJsonAsync<ServerModulePackagePlanDto>(RelaxKonOSJsonOptions.Default))!;
            check(plan.ModuleId == "fixture-http" && plan.Version == "1.0.0" && plan.ArtifactSha256 == built.ArchiveSha256
                && plan.ManifestSha256 == built.ManifestSha256,
                "HTTP package plan binds the exact module release and archive identity");

            var planStatusRoute = ServerModuleManagementRoutes.PackagePlan.Replace("{planId:guid}", plan.PlanId.ToString());
            var statusBefore = await client.GetFromJsonAsync<ServerModulePackagePlanDto>(planStatusRoute, RelaxKonOSJsonOptions.Default);
            check(statusBefore?.PlanId == plan.PlanId && statusBefore.AppliedOperationId is null,
                "HTTP package plan status reads the original frozen plan without applying it");
            var otherPlanRoute = ServerModuleManagementRoutes.PackagePlan.Replace("{planId:guid}", autoPlan.PlanId.ToString());
            var storedPlans = ModulePackagePlanStore.Read(root).ToArray();
            var otherPlans = storedPlans.Select(item => item.PlanId == autoPlan.PlanId ? item with { Actor = "another-controller" } : item).ToArray();
            await File.WriteAllBytesAsync(ModulePackagePlanStore.PathFor(root),
                JsonSerializer.SerializeToUtf8Bytes(new ModulePackagePlanDocument(3, otherPlans), RelaxKonOSJsonOptions.Default));
            using (var forbidden = await client.GetAsync(otherPlanRoute))
                check(forbidden.StatusCode == System.Net.HttpStatusCode.Forbidden,
                    "HTTP package plan status refuses a different controller actor");
            var expiredPlans = storedPlans.Select(item => item.PlanId == autoPlan.PlanId
                ? item with { CreatedAt = now.AddMinutes(-20), ExpiresAt = now.AddMinutes(-10) } : item).ToArray();
            await File.WriteAllBytesAsync(ModulePackagePlanStore.PathFor(root),
                JsonSerializer.SerializeToUtf8Bytes(new ModulePackagePlanDocument(3, expiredPlans), RelaxKonOSJsonOptions.Default));
            var expiredStatus = await client.GetFromJsonAsync<ServerModulePackagePlanDto>(otherPlanRoute, RelaxKonOSJsonOptions.Default);
            check(expiredStatus?.PlanId == autoPlan.PlanId && expiredStatus.AppliedOperationId is null,
                "HTTP expired package plan remains readable without executing it");
            using (var expiredApply = await client.PostAsync(
                ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", autoPlan.PlanId.ToString()), null))
                check(expiredApply.StatusCode == System.Net.HttpStatusCode.Conflict,
                    "HTTP expired unapplied package plan cannot be executed");

            if (isolated)
            {
                check(plan.Runtime == "module-host" && plan.Privileges.Single().Kind == ModuleCapabilityCatalog.ServiceManage
                    && plan.Privileges.Single().Executable
                    && plan.Privileges.Single().Resources.Single().Contains("fixture.service", StringComparison.Ordinal),
                    "install preview exposes runtime and the structured declared capability scope");
                using var direct = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackageInstall,
                    new ServerModulePackageInstallRequest("official", releaseId, uploaded.UploadId), RelaxKonOSJsonOptions.Default);
                check(direct.StatusCode == System.Net.HttpStatusCode.Conflict
                    && InstalledModuleStore.Read(root).Count == (preinstalled ? 1 : 0),
                    "direct install cannot bypass frozen-plan code trust confirmation");
            }
            var applyResponse = await client.PostAsync(
                ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", plan.PlanId.ToString()), null);
            if (!applyResponse.IsSuccessStatusCode)
                throw new InvalidOperationException("Package apply failed: " + (int)applyResponse.StatusCode + " " + await applyResponse.Content.ReadAsStringAsync());
            var applied = (await applyResponse.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
            check(applied.Record.ModuleId == "fixture-http" && applied.Record.Version == "1.0.0",
                "HTTP package plan apply installs the frozen release");

            if (isolated)
            {
                var actual = InstalledModuleStore.Read(root).Single();
                check(actual.Runtime == "module-host" && ModuleTrustStore.Read(root, actual)?.Trusted == true,
                    "confirmed signed third-party installation writes exact code trust without loading code");
            }
            var installed = (await client.GetFromJsonAsync<ServerInstalledModulePageDto>(
                ServerModuleManagementRoutes.InstalledPackages, RelaxKonOSJsonOptions.Default))!;
            var operations = (await client.GetFromJsonAsync<ServerPackageOperationPageDto>(
                ServerModuleManagementRoutes.PackageOperations, RelaxKonOSJsonOptions.Default))!;
            check(installed.Items.Single().Active && installed.Items.Single().Version == "1.0.0"
                && operations.Items.Single().State == ModulePackageOperationState.AwaitingRestart,
                "HTTP install exposes installed and awaiting-restart facts without claiming effective");

            ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root), "package-http-generation",
                DateTimeOffset.UtcNow);
            check(ModulePackageOperationStore.Read(root).Single().State == ModulePackageOperationState.Succeeded,
                "startup verification marks the HTTP install succeeded only after the installed fact matches");
            var verified = (await client.GetFromJsonAsync<ServerPackageOperationPageDto>(
                ServerModuleManagementRoutes.PackageOperations, RelaxKonOSJsonOptions.Default))!;
            check(verified.Items.Single().State == ModulePackageOperationState.Succeeded,
                "startup verification result remains readable through the package operation API");
            var receiptRoute = ServerModuleManagementRoutes.PackageOperation.Replace("{operationId:guid}", applied.OperationId.ToString("D"));
            var receipt = await client.GetFromJsonAsync<ServerPackageOperationDto>(receiptRoute, RelaxKonOSJsonOptions.Default);
            check(receipt == verified.Items.Single(),
                "the exact receipt endpoint returns the same verified result as the operation history");
            using var missingReceipt = await client.GetAsync(ServerModuleManagementRoutes.PackageOperation
                .Replace("{operationId:guid}", Guid.NewGuid().ToString("D")));
            check(missingReceipt.StatusCode == HttpStatusCode.NotFound, "an unknown exact receipt returns HTTP 404");

            var replayResponse = await client.PostAsync(
                ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", plan.PlanId.ToString()), null);
            replayResponse.EnsureSuccessStatusCode();
            var replay = (await replayResponse.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
            check(replay.OperationId == applied.OperationId && replay.Record.Version == "1.0.0",
                "HTTP package plan apply is idempotent after the upload has been consumed");
            var statusAfter = await client.GetFromJsonAsync<ServerModulePackagePlanDto>(planStatusRoute, RelaxKonOSJsonOptions.Default);
            check(statusAfter?.AppliedOperationId == applied.OperationId,
                "HTTP package plan status resolves the original receipt after a lost response");
            check(ModulePackagePlanStore.MarkApplied(root, plan.PlanId, applied.OperationId, now.AddHours(3)).AppliedOperationId == applied.OperationId,
                "accepted package completion can persist its receipt after the preview expiry");
            if (dependentApplyRoute is not null)
            {
                using var applyDependent = await client.PostAsync(dependentApplyRoute, null);
                if (!applyDependent.IsSuccessStatusCode)
                    throw new InvalidOperationException("Dependent package apply failed: " + await applyDependent.Content.ReadAsStringAsync());
                var installedDependent = (await applyDependent.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(
                    RelaxKonOSJsonOptions.Default))!;
                check(installedDependent.Record.ModuleId == "fixture-dependent"
                    && installedDependent.Record.Requires.SequenceEqual(["fixture-http"]),
                    "the same frozen dependent plan installs once its signed predecessor has been installed");
            }
            if (incompatible is not null)
            {
                using var updateUpload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                    new ByteArrayContent(await File.ReadAllBytesAsync(incompatible.ArchivePath)));
                updateUpload.EnsureSuccessStatusCode();
                var uploadedUpdate = (await updateUpload.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(
                    RelaxKonOSJsonOptions.Default))!;
                using var updatePreview = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackagePlans,
                    new ServerModulePackageInstallRequest("official", incompatible.Manifest.ReleaseId, uploadedUpdate.UploadId),
                    RelaxKonOSJsonOptions.Default);
                updatePreview.EnsureSuccessStatusCode();
                var update = (await updatePreview.Content.ReadFromJsonAsync<ServerModulePackagePlanDto>(RelaxKonOSJsonOptions.Default))!;
                using var denied = await client.PostAsync(ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", update.PlanId.ToString()), null);
                using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
                check(!denied.IsSuccessStatusCode
                    && problem.RootElement.GetProperty("problemCode").GetString() == "module-package-dependency-version"
                    && InstalledModuleStore.Read(root).Single(record => record.ModuleId == "fixture-http").Version == "1.0.0",
                    "module upgrade respects the version range of an installed dependent and preserves the active version");
            }
            var retainedPlans = ModulePackagePlanStore.Read(root).Select(item => item with
                { CreatedAt = now.AddHours(-3), ExpiresAt = now.AddHours(-2) }).ToArray();
            await File.WriteAllBytesAsync(ModulePackagePlanStore.PathFor(root),
                JsonSerializer.SerializeToUtf8Bytes(new ModulePackagePlanDocument(3, retainedPlans), RelaxKonOSJsonOptions.Default));
            var expiredApplied = await client.GetFromJsonAsync<ServerModulePackagePlanDto>(planStatusRoute, RelaxKonOSJsonOptions.Default);
            check(expiredApplied?.AppliedOperationId == applied.OperationId,
                "HTTP expired applied plan retains the original operation reference");
            using var uploadAgain = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                new ByteArrayContent(await File.ReadAllBytesAsync(built.ArchivePath)));
            uploadAgain.EnsureSuccessStatusCode();
            var uploadedAgain = (await uploadAgain.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(RelaxKonOSJsonOptions.Default))!;
            using var createAgain = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackageAutoPlan,
                new ServerModulePackageAutoPlanRequest(uploadedAgain.UploadId), RelaxKonOSJsonOptions.Default);
            createAgain.EnsureSuccessStatusCode();
            var afterPrune = ModulePackagePlanStore.Read(root);
            check(afterPrune.Any(item => item.PlanId == plan.PlanId && item.AppliedOperationId == applied.OperationId)
                && afterPrune.All(item => item.PlanId != autoPlan.PlanId),
                "new plan creation prunes expired unexecuted plans but retains applied receipt references");
            if (tailscale.Length != 0)
            {
                await VerifyExecutionPackageVersionsAsync<InstalledTailscaleModule>(client, root, tailscale, check);
                await VerifyExecutionPackageVersionsAsync<InstalledFirewallModule>(client, root, firewall, check);
                await VerifyExecutionPackageVersionsAsync<InstalledGitModule>(client, root, git, check);
                await VerifyExecutionPackageVersionsAsync<InstalledDockerModule>(client, root, docker, check);
                await VerifyExecutionPackageVersionsAsync<InstalledCertificateModule>(client, root, certificates, check);
                await VerifyExecutionPackageVersionsAsync<InstalledWebServerModule>(client, root, webServer, check);
                await VerifyExecutionPackageVersionsAsync<InstalledProxyModule>(client, root, proxy, check);
                var proxyHost = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
                var proxyModules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.ProxyId }, InstalledModuleCatalog.Load(root));
                proxyModules.ConfigureServices(proxyHost, ServerModuleContext.Create(proxyHost.Configuration, proxyHost.Environment, "memory"));
                await using (var restoredProxyHost = proxyHost.Build())
                    check(!ReferenceEquals(restoredProxyHost.Services.GetRequiredService<ProxyConnectionExecutionGate>(), ProxyConnectionExecutionGate.Shared),
                        "a fresh Core host validates restored Proxy overview, subscription and direct publication executors from the installed package");
                await VerifyRestoredWebServerAsync(root, check);
                var dockerHost = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
                var mirrorOwner = Guid.NewGuid();
                var mirrors = new InMemoryImageMirrorRepository();
                var selectedMirror = mirrors.Create(new RelaxKonOS.Server.Domain.ImageMirror
                { UserId = mirrorOwner, Target = ImageMirrorTarget.Docker, Name = "Acceptance", Endpoint = "mirror.example:8443" });
                check(mirrors.Select(mirrorOwner, ImageMirrorTarget.Docker, selectedMirror.Id), "the Docker fixture selects a mirror for its test owner");
                dockerHost.Services.AddSingleton<IImageMirrorRepository>(mirrors);
                dockerHost.Services.AddSingleton<IPrivilegedOperationTransport, DisabledPrivilegedOperationTransport>();
                dockerHost.Services.AddSingleton<DockerStackOperationStore>();
                dockerHost.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher, NoOperationalEvents>();
                dockerHost.Services.AddSingleton(System.Reflection.DispatchProxy.Create<IDockerEngineService, GitRepositoryAssemblyChecks.RejectTransport>());
                var dockerModules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.DockerId }, InstalledModuleCatalog.Load(root));
                dockerModules.ConfigureServices(dockerHost, ServerModuleContext.Create(dockerHost.Configuration, dockerHost.Environment, "memory"));
                await using (var composeHost = dockerHost.Build())
                await using (var composeScope = composeHost.Services.CreateAsyncScope())
                {
                    var compose = composeScope.ServiceProvider.GetRequiredService<IDockerComposeService>();
                    var preview = await compose.PreviewAsync(new("package-check", "services:\n  web:\n    image: nginx:alpine\n"));
                    check(preview.Services.Single().Service == "web", "a fresh Core host parses a Compose preview using the restored signed execution package");
                    var engine = composeScope.ServiceProvider.GetRequiredService<IDockerEngineControlService>();
                    var restart = await engine.ApplyAsync("restart", confirmed: false);
                    check(!restart.Success && restart.ProblemCode == DockerEngineProblem.ConfirmationRequired,
                        "the restored signed Docker package refuses an unconfirmed Engine restart");
                    var mirrorResolver = composeScope.ServiceProvider.GetRequiredService<IDockerImageMirrorResolver>();
                    check(mirrorResolver.Resolve(mirrorOwner, "docker.io/nginx:alpine") == "mirror.example:8443/library/nginx:alpine"
                        && mirrorResolver.Resolve(mirrorOwner, "ghcr.io/example/app:tag") == "ghcr.io/example/app:tag"
                        && mirrorResolver.Resolve(Guid.NewGuid(), "nginx:alpine") == "nginx:alpine",
                        "the restored signed Docker package rewrites only the owner's eligible Docker Hub reference");
                    if (liveDocker)
                        await VerifyLiveComposeAsync(compose, composeHost.Services.GetRequiredService<DockerStackOperationCoordinator>(),
                            composeHost.Services.GetRequiredService<DockerStackOperationStore>(), check);
                }
                using var selectSecond = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackageRollback,
                    new ServerModulePackageRollbackRequest(ServerModules.GitId, "2.0.0"), RelaxKonOSJsonOptions.Default);
                selectSecond.EnsureSuccessStatusCode();
                var revocation = await ModulePackageBuilder.SignCatalogAsync(new ModulePackageCatalog(1, "official", 2, now,
                    now.AddHours(1), [Release(git[1])], [], [git[0].ArchiveSha256]), "builder-key", keyPath);
                using (var catalogs = new ModulePackageCatalogStore(root, serviceId, takeHostLock: false))
                    catalogs.Accept(revocation.Envelope, policy, context, ModulePackageCatalogStore.Read(root, serviceId).Revision,
                        "package-http", "revoke-git-archive", now);
                using var rejectRollback = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackageRollback,
                    new ServerModulePackageRollbackRequest(ServerModules.GitId, "1.0.0"), RelaxKonOSJsonOptions.Default);
                using var revokedProblem = JsonDocument.Parse(await rejectRollback.Content.ReadAsStringAsync());
                check(!rejectRollback.IsSuccessStatusCode
                    && revokedProblem.RootElement.GetProperty("problemCode").GetString() == "module-package-release-revoked"
                    && InstalledModuleCatalog.Load(root).OfType<InstalledGitModule>().Single().InstalledVersion == "2.0.0",
                    "HTTP rollback rejects a revoked archived digest and preserves the current installed target");
                var selected = (await selectSecond.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
                ModulePackageStartupGuard.Begin(root, builder.Configuration, "failed-signed-startup");
                ModulePackageStartupGuard.Begin(root, builder.Configuration, "recover-signed-startup");
                check(InstalledModuleStore.Read(root).Single(record => record.ModuleId == ServerModules.GitId) is
                    { Version: "2.0.0", Quarantined: true }
                    && ModulePackageOperationStore.Read(root).Single(operation => operation.OperationId == selected.OperationId)
                        .ProblemCode == "module-package-operation-startup-rollback-failed-quarantined",
                    "startup recovery quarantines the failed target instead of restoring the revoked prior release");
                ModulePackageStartupGuard.Complete(root);
            }
            await app.StopAsync();
            management.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task VerifyRestoredWebServerAsync(string root, Action<bool, string> check)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Services.AddSingleton(new CertificateOptions { DirectoryUrl = "invalid-directory" });
        builder.Services.AddSingleton<FileHttp01ChallengeStore>();
        builder.Services.AddSingleton<CertificateMetadataRepository>();
        var installed = InstalledModuleCatalog.Load(root);
        var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.WebServerId, ServerModules.CertificatesId }, installed);
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        modules.ConfigureServices(builder, context);
        await using var host = builder.Build();
        var execution = host.Services.GetRequiredService<WebServerExecutionCode>();
        check(!ReferenceEquals(execution, WebServerExecutionCode.Shared), "the restored Web Server module registers its installed execution loader");
        var installedAcme = host.Services.GetRequiredService<AcmeExecution>();
        check(!ReferenceEquals(installedAcme, AcmeExecution.Shared),
            "the Web Server dependency starts with its own verified signed Certificates execution package");
        var acme = host.Services.GetRequiredService<IAcmeService>();
        var directory = new Uri("https://fixture.invalid/dir");
        var identity = new AcmeOrderIdentity(directory, new(directory, "/account"), "fixture", new(directory, "/order"));
        check(await acme.ObserveOrderAsync(identity, default) == "unavailable" && installedAcme.CurrentCode is { IsAlive: true },
            "production ACME service activation uses the restored signed package and rejects an invalid CA directory");
        var ports = new WebServerReloadProbe();
        using var lease = execution.Acquire<INginxReloadExecutor>("RelaxKonOS.Server.WebServer.NginxReloadExecutor", ports);
        check(await lease.Executor.ReloadAsync(default) == "" && ports.Confirmed,
            "the restored signed Web Server executor confirms an observed reload through its narrow port");
        ports.Confirmed = false;
        ports.DirectSuccess = false;
        check(await lease.Executor.ReloadAsync(default) == NginxReloadOutcome.RecoveryRequired && !ports.Confirmed,
            "the signed reload executor requests recovery when completion cannot be verified");
        var certificate = InstalledModuleStore.Read(root).Single(record => record.ModuleId == ServerModules.CertificatesId);
        var guarded = ServerModules.CreateManaged(new HashSet<string> { ServerModules.WebServerId, ServerModules.CertificatesId },
            [.. installed.Where(module => module.Id != certificate.ModuleId), new InstalledCertificateModule(certificate with { Quarantined = true }, root)]);
        var health = await guarded.CheckHealthAsync(root, default);
        check(!guarded.IsEnabled(ServerModules.CertificatesId) && !guarded.IsEnabled(ServerModules.WebServerId)
            && !guarded.Capabilities.Contains(ServerCapabilities.Certificates) && !guarded.Capabilities.Contains(ServerCapabilities.WebServer)
            && health.Modules.Single(module => module.ModuleId == ServerModules.WebServerId).ProblemCode == "module-package-dependency-quarantined",
            "a quarantined Certificates package blocks the dependent Web Server without advertising either capability");
    }

    private sealed class WebServerReloadProbe : INginxReloadPorts
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public bool UsesSystemPackage => false;
        public bool Confirmed { get; set; }
        public bool DirectSuccess { get; set; } = true;
        public Task ReportDispatchAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportResponseAsync(bool confirmed)
        {
            Confirmed = confirmed;
            return Task.CompletedTask;
        }
        public Task<PrivilegedOperationResult> RunTrackedAsync(Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The direct reload must not use the system package Helper port.");
        public Task<bool> RunDirectAsync(CancellationToken cancellationToken) => Task.FromResult(DirectSuccess);
        public string ToReloadProblem(PrivilegedProblemCode problem) => throw new InvalidOperationException();
    }

    private static async Task VerifyLiveComposeAsync(IDockerComposeService compose, DockerStackOperationCoordinator coordinator,
        DockerStackOperationStore store, Action<bool, string> check)
    {
        var project = "rk-signed-" + Guid.NewGuid().ToString("N");
        const string definition = """
            services:
              probe:
                image: debian:stable-slim
                pull_policy: never
                command: ["sleep", "120"]
            """;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        const string actor = "signed-package-acceptance";
        var key = Guid.NewGuid().ToString("N");
        var request = new DockerStackDeployRequest(new(project, definition), DockerStackValidation.DefinitionVersion(project, definition));
        await coordinator.StartAsync(budget.Token);
        try
        {
            var started = await coordinator.DeployAsync(request, actor, key, budget.Token);
            var applied = await WaitForReceiptAsync(started.OperationId, budget.Token);
            check(applied is { State: DockerStackOperationState.Succeeded, Services: [{ Service: "probe", State: "running" }] },
                "the coordinator records successful deployment and the real running container in the signed package receipt");
            var replay = await coordinator.DeployAsync(request, actor, key, budget.Token);
            check(replay.OperationId == applied.OperationId && replay.State == applied.State,
                "the same deployment key replays the durable receipt");
            var pendingRemoval = await coordinator.ApplyActionAsync(project, DockerStackOperationKind.Delete, true, actor,
                Guid.NewGuid().ToString("N"), budget.Token);
            var removed = await WaitForReceiptAsync(pendingRemoval.OperationId, budget.Token);
            check(removed is { State: DockerStackOperationState.Succeeded, Services.Count: 0 },
                "the coordinator records successful deletion with an empty Engine observation");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await coordinator.StopAsync(cleanup.Token);
            var result = await compose.ApplyActionAsync(project, DockerStackOperationKind.Delete, confirmed: true, cleanup.Token);
            var remaining = await compose.ListServicesAsync(project, cleanup.Token);
            check((result.Success || result.ProblemCode == DockerStackProblem.NotFound) && remaining.Count == 0,
                "the live signed-package test removes its isolated project containers");
        }

        async Task<DockerStackOperationDto> WaitForReceiptAsync(Guid id, CancellationToken ct)
        {
            while (true)
            {
                if (store.Get(id)?.Operation is { } receipt && !DockerStackOperationStore.Active(receipt)) return receipt;
                await Task.Delay(50, ct);
            }
        }
    }

    private static async Task VerifyExecutionPackageVersionsAsync<TModule>(HttpClient client, string root,
        ModulePackageBuildResult[] packages, Action<bool, string> check) where TModule : IInstalledModuleInfo
    {
        var moduleId = packages[0].Manifest.ModuleId;
        var offers = await client.GetFromJsonAsync<ServerModuleStorePageDto>(ServerModuleManagementRoutes.ModuleStoreSearch
            + "?q=" + moduleId, RelaxKonOSJsonOptions.Default);
        var available = offers?.Items.Where(offer => offer.BlockedBy.Count == 0).ToArray();
        check(available?.Length == packages.Length && available.All(offer => offer.ModuleId == moduleId
            && offer.RidCompatible && offer.Capabilities.SequenceEqual(packages[0].Manifest.Capabilities)),
            $"HTTP advertises both signed {moduleId} versions with their declared capability");
        foreach (var package in packages)
        {
            using var upload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                new ByteArrayContent(await File.ReadAllBytesAsync(package.ArchivePath)));
            upload.EnsureSuccessStatusCode();
            var staged = (await upload.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(RelaxKonOSJsonOptions.Default))!;
            using var preview = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackagePlans,
                new ServerModulePackageInstallRequest("official", package.Manifest.ReleaseId, staged.UploadId), RelaxKonOSJsonOptions.Default);
            preview.EnsureSuccessStatusCode();
            var plan = (await preview.Content.ReadFromJsonAsync<ServerModulePackagePlanDto>(RelaxKonOSJsonOptions.Default))!;
            using var applied = await client.PostAsync(ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", plan.PlanId.ToString()), null);
            applied.EnsureSuccessStatusCode();
            var result = (await applied.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
            var receipt = await ReadReceiptAsync(result.OperationId);
            check(result.Record.ArtifactSha256 == package.ArchiveSha256 && receipt.Version == package.Manifest.Version
                && receipt.Action == (package == packages[0] ? "install" : "update")
                && receipt.State == ModulePackageOperationState.AwaitingRestart,
                $"HTTP accepts the signed {moduleId} {package.Manifest.Version} plan and records a restart-bound receipt");
        }

        using var rollback = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackageRollback,
            new ServerModulePackageRollbackRequest(packages[0].Manifest.ModuleId, packages[0].Manifest.Version), RelaxKonOSJsonOptions.Default);
        rollback.EnsureSuccessStatusCode();
        var restored = (await rollback.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
        var rollbackReceipt = await ReadReceiptAsync(restored.OperationId);
        check(restored.Record.ArtifactSha256 == packages[0].ArchiveSha256 && rollbackReceipt is
            { Action: "rollback", State: ModulePackageOperationState.AwaitingRestart, PreviousVersion: "2.0.0", Version: "1.0.0" }
            && InstalledModuleCatalog.Load(root).OfType<TModule>().Single().InstalledVersion == "1.0.0",
            $"HTTP rollback selects the original immutable {moduleId} archive for the next Core start");

        async Task<ServerPackageOperationDto> ReadReceiptAsync(Guid operationId) =>
            (await client.GetFromJsonAsync<ServerPackageOperationDto>(ServerModuleManagementRoutes.PackageOperation
                .Replace("{operationId:guid}", operationId.ToString("D")), RelaxKonOSJsonOptions.Default))!;
    }

    private sealed class PackageDownloadHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
    }
}
