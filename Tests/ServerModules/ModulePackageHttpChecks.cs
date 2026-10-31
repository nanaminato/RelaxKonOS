using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Startup;

internal static class ModulePackageHttpChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "package-http-" + Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(root, "publish");
        var output = Path.Combine(root, "package");
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(output);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FixtureModule.dll"), Path.Combine(publish, "FixtureModule.dll"));
            await File.WriteAllTextAsync(Path.Combine(publish, "asset.txt"), "http package asset");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keyPath = Path.Combine(root, "signing-key.pem");
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            var rid = RuntimeInformation.RuntimeIdentifier;
            var now = DateTimeOffset.UtcNow;
            var releaseId = "fixture-http-1.0.0-" + rid;
            var built = await ModulePackageBuilder.BuildAsync(new ModulePackageBuildOptions(publish, output, "fixture-http",
                "1.0.0", releaseId, "fixture", "official", rid, "FixtureModule.dll",
                "FixtureModule.FixtureInstalledModule", "fixture-build", new string('a', 40), "official", 1,
                "https://modules.example/", keyPath, "builder-key", 3600), now);
            var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "fixture", "official",
                "https://modules.example/", ["fixture-http"],
                new Dictionary<string, string> { ["builder-key"] = built.PublicKeySpkiBase64 }, 3600);
            await File.WriteAllTextAsync(Path.Combine(root, "appsettings.host.json"), JsonSerializer.Serialize(new
            {
                ModuleManagement = new { Authority = "managed", Administrators = new[] { ModuleTestAuthentication.Admin } },
                ModulePackages = new { Sources = new Dictionary<string, ModulePackageTrustPolicy> { ["official"] = policy } },
            }, RelaxKonOSJsonOptions.Default));

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile("appsettings.host.json", optional: false, reloadOnChange: false);
            string serviceId;
            using (var initialization = new ModuleManagementStore(builder.Configuration, root)) serviceId = initialization.ServiceId;
            var context = ModulePackageHostConfiguration.Context(serviceId, builder.Configuration, builder.Environment);
            using (var catalog = new ModulePackageCatalogStore(root, serviceId))
                catalog.Accept(File.ReadAllBytes(built.CatalogPath), policy, context, ModulePackageCatalogStore.InitialRevision,
                    "package-http", "accept-package-http", now);

            using var management = new ModuleManagementStore(builder.Configuration, root);
            var modules = ServerModules.CreateManaged(management.DesiredIds);
            builder.Services.AddSingleton(management);
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy,
                item => item.RequireAuthenticatedUser().RequireAssertion(context =>
                    ServerModuleManagementEndpoints.CanManage(context.User, builder.Configuration))));

            await using var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapServerModuleManagementEndpoints();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);

            var upload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload,
                new ByteArrayContent(await File.ReadAllBytesAsync(built.ArchivePath)));
            upload.EnsureSuccessStatusCode();
            var uploaded = (await upload.Content.ReadFromJsonAsync<ServerModulePackageUploadDto>(RelaxKonOSJsonOptions.Default))!;
            check(uploaded.Bytes == built.ArchiveBytes && uploaded.Sha256 == built.ArchiveSha256,
                "HTTP upload stages the exact package bytes and digest");

            var planResponse = await client.PostAsJsonAsync(ServerModuleManagementRoutes.PackagePlans,
                new ServerModulePackageInstallRequest("official", releaseId, uploaded.UploadId), RelaxKonOSJsonOptions.Default);
            planResponse.EnsureSuccessStatusCode();
            var plan = (await planResponse.Content.ReadFromJsonAsync<ServerModulePackagePlanDto>(RelaxKonOSJsonOptions.Default))!;
            check(plan.ModuleId == "fixture-http" && plan.Version == "1.0.0" && plan.ArtifactSha256 == built.ArchiveSha256
                && plan.ManifestSha256 == built.ManifestSha256,
                "HTTP package plan binds the exact module release and archive identity");

            var applyResponse = await client.PostAsync(
                ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", plan.PlanId.ToString()), null);
            applyResponse.EnsureSuccessStatusCode();
            var applied = (await applyResponse.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
            check(applied.Record.ModuleId == "fixture-http" && applied.Record.Version == "1.0.0",
                "HTTP package plan apply installs the frozen release");

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

            var replayResponse = await client.PostAsync(
                ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", plan.PlanId.ToString()), null);
            replayResponse.EnsureSuccessStatusCode();
            var replay = (await replayResponse.Content.ReadFromJsonAsync<ServerModulePackageInstallResultDto>(RelaxKonOSJsonOptions.Default))!;
            check(replay.OperationId == applied.OperationId && replay.Record.Version == "1.0.0",
                "HTTP package plan apply is idempotent after the upload has been consumed");
            await app.StopAsync();
            management.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
