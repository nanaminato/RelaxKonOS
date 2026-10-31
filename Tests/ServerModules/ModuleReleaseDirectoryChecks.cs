using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Startup;

internal static class ModuleReleaseDirectoryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "module-release-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ModuleManagement:Authority"] = "managed" }).Build();
            string serviceId;
            using (var initial = new ModuleManagementStore(configuration, root)) serviceId = initial.ServiceId;
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var otherRid = rid == "linux-arm64" ? "win-x64" : "linux-arm64";
            var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "relaxkon", "official", "https://unreachable.invalid/",
                [ServerModules.FirewallId, "relaxkonos.future"], new Dictionary<string, string> { ["key-1"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }, 3600);
            byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ModulePackageJson.Options);
            ModulePackageRelease Release(int version, string targetRid, string apiMinimum = "1.0.0", string apiMaximum = "2.0.0", bool dependency = false)
            {
                var m = new ModulePackageManifest(2, "server-module", ServerModules.FirewallId, "release-" + version, version + ".0.0",
                    "relaxkon", "official", targetRid, apiMinimum, apiMaximum, "server/" + targetRid + "/Module.dll", "Module.Entry",
                    "build-" + version, new string('a', 40), dependency ? [new(ServerModules.GitId, "1.0.0", "2.0.0")] : [], ["firewall"],
                    [new("server/" + targetRid + "/Module.dll", 1, new string('b', 64))], "in-process", [], new("Fixture", "", "", null, "MIT"));
                return new(m, Convert.ToHexStringLower(SHA256.HashData(Json(m))), version.ToString("x64"), 512, "https://unreachable.invalid/release-" + version + ".rkmodule");
            }
            var now = DateTimeOffset.UtcNow;
            var catalog = new ModulePackageCatalog(1, policy.SourceId, 1, now.AddMinutes(-1), now.AddMinutes(20),
                [Release(2, rid), Release(1, rid, "2.0.0", "3.0.0", true), Release(4, rid), Release(3, otherRid)], ["release-4"], []);
            var payload = Json(catalog);
            var signature = key.SignData(Encoding.UTF8.GetBytes("RelaxKonOS.ServerModuleCatalog.v1\n").Concat(payload).ToArray(),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            using (var acceptance = new ModulePackageCatalogStore(root, serviceId))
                acceptance.Accept(Json(new SignedModulePackageCatalog(1, "key-1", Convert.ToBase64String(payload), Convert.ToBase64String(signature))),
                    policy, new(serviceId, rid, "1.0.0", false), ModulePackageCatalogStore.InitialRevision, "test-actor", "directory", now);
            await File.WriteAllBytesAsync(Path.Combine(root, "appsettings.host.json"), Json(new { ModulePackages = new { Sources =
                new Dictionary<string, ModulePackageTrustPolicy> { [policy.SourceId] = policy } } }));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile("appsettings.host.json", false, false).AddInMemoryCollection(new Dictionary<string, string?>
                { ["ModuleManagement:Authority"] = "managed" });
            using var management = new ModuleManagementStore(builder.Configuration, root);
            var modules = ServerModules.CreateManaged(management.DesiredIds);
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            builder.Services.AddSingleton(management); builder.Services.AddSingleton(modules); builder.Services.AddSingleton(context);
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy, p => p.RequireAuthenticatedUser()));
            await using var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapServerModuleManagementEndpoints();
            app.Urls.Add("http://127.0.0.1:0"); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            string Route(string moduleId = ServerModules.FirewallId, string sourceId = "official", string query = "") =>
                ServerModuleManagementRoutes.Releases + "?moduleId=" + Uri.EscapeDataString(moduleId) + "&sourceId=" + Uri.EscapeDataString(sourceId) + query;
            check((await client.GetAsync(Route())).StatusCode == HttpStatusCode.Unauthorized, "module releases require authentication before reading trust state");
            client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Observer);
            var observedFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "host.lock").ToArray();
            var hashes = observedFiles.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var first = (await client.GetFromJsonAsync<ServerModuleReleasePageDto>(Route(query: "&limit=2"), RelaxKonOSJsonOptions.Default))!;
            check(first.ServiceId == serviceId && first.SourceId == "official" && first.ModuleId == ServerModules.FirewallId
                && first.CatalogSequence == 1 && first.PayloadSha256 == Convert.ToHexStringLower(SHA256.HashData(payload))
                && first.ObservationScope == "signed-catalog" && first.HostRid == rid && first.HostApiVersion == "1.0.0",
                "module releases bind server source exact signed directory and Host context");
            check(first.Items.Select(r => r.Version).SequenceEqual(["4.0.0", "3.0.0"]) && first.NextOffset == 2,
                "module releases paginate immutable directory in descending numeric version order");
            check(!first.PackageActionsSupported && first.BlockedBy.Any(b => b.ProblemCode == "module-packaging-bundled")
                && first.Items.All(r => !r.ArtifactVerified && r.BlockedBy.Any(b => b.ProblemCode == "module-package-artifact-verification-required")),
                "module releases never claim archive verification installation or independent built-in updates");
            check(first.Items[0].TrustState == "revoked" && first.Items[0].BlockedBy.Any(b => b.ProblemCode == "module-package-release-revoked")
                && !first.Items[1].RidCompatible && first.Items[1].BlockedBy.Any(b => b.ProblemCode == "module-package-rid-mismatch"),
                "module releases expose revoked and foreign RID entries with explicit distinct blockers");
            var second = (await client.GetFromJsonAsync<ServerModuleReleasePageDto>(Route(query: "&offset=2&limit=2&expectedCatalogRevision=" + first.CatalogRevision), RelaxKonOSJsonOptions.Default))!;
            check(second.NextOffset is null && second.CatalogRevision == first.CatalogRevision && second.Items[0].ReleaseId == "release-2"
                && second.Items[0].TrustState == "catalog-verified" && second.Items[0].RidCompatible && second.Items[0].HostApiCompatible,
                "module releases continuation pins exact catalog revision and exposes compatible signed metadata");
            check(!second.Items[1].HostApiCompatible && second.Items[1].Dependencies.Single().ModuleId == ServerModules.GitId
                && second.Items[1].BlockedBy.Any(b => b.ProblemCode == "module-package-dependency-verification-required"),
                "module releases preserve dependency ranges without claiming dependency or Host API readiness");
            check((await client.GetAsync(Route(query: "&offset=2"))).StatusCode == HttpStatusCode.BadRequest
                && (await client.GetAsync(Route(query: "&offset=2&expectedCatalogRevision=stale"))).StatusCode == HttpStatusCode.Conflict,
                "module releases reject unpinned or stale continuation instead of mixing directory generations");
            check((await client.GetAsync(Route(query: "&limit=101"))).StatusCode == HttpStatusCode.BadRequest
                && (await client.GetAsync(Route(query: "&offset=-1"))).StatusCode == HttpStatusCode.BadRequest,
                "module releases enforce bounded pagination");
            check((await client.GetAsync(Route(sourceId: "https://attacker.invalid"))).StatusCode == HttpStatusCode.BadRequest
                && (await client.GetAsync(Route(sourceId: "unknown"))).StatusCode == HttpStatusCode.NotFound
                && (await client.GetAsync(Route(moduleId: "unauthorized.module"))).StatusCode == HttpStatusCode.NotFound,
                "module releases accept only configured sources and Host authorized module identities");
            var empty = (await client.GetFromJsonAsync<ServerModuleReleasePageDto>(Route(moduleId: "relaxkonos.future"), RelaxKonOSJsonOptions.Default))!;
            check(empty.Items.Count == 0 && !empty.PackageActionsSupported && empty.BlockedBy.Any(b => b.ProblemCode == "module-package-installer-api-unavailable"),
                "module releases distinguish an authorized empty directory from an installed module");
            builder.Configuration["ModulePackages:Sources:official:Revision"] = "2";
            var changedPolicy = await client.GetAsync(Route());
            check(changedPolicy.StatusCode == HttpStatusCode.ServiceUnavailable && (await changedPolicy.Content.ReadAsStringAsync()).Contains("module-package-source-refresh-required", StringComparison.Ordinal),
                "module releases fail closed when current deployment policy has not been durably accepted");
            builder.Configuration["ModulePackages:Sources:official:Revision"] = "1";
            try { ModulePackageReleaseDirectory.Read(context, management, modules, ServerModules.FirewallId, "official", 0, 25, now.AddDays(1)); throw new Exception("expired directory accepted"); }
            catch (ModulePackageException e) when (e.ProblemCode == "module-package-catalog-expired")
            { check(true, "module releases recheck catalog expiry on each observation"); }
            check(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Count(path => Path.GetFileName(path) != "host.lock") == hashes.Count
                && hashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value),
                "module releases read metadata without downloads state writes archive extraction or module construction");
            await app.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }
}
