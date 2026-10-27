using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage.Sqlite;

internal static class QuietSettingsVerification
{
    public static async Task RunAsync(string root)
    {
        var actor = Guid.NewGuid();
        var path = Path.Combine(root, "runtime.json");
        var runtime = new ServerRuntimeSettingsStore(path);
        var original = runtime.Current;
        var saved = runtime.Save(new(original.InstanceId, "registryFlushDelayMilliseconds", JsonSerializer.SerializeToElement(100), original.Revision), actor)!;
        Check(saved.UpdatedBy == actor && saved.Settings.RegistryFlushDelayMilliseconds == 100, "Server settings must record the real actor.");
        Check(runtime.Save(new(saved.InstanceId, "retryDelaySeconds", JsonSerializer.SerializeToElement(1), original.Revision), actor) is null,
            "Server runtime settings must reject stale revisions.");
        Check(runtime.Save(new(Guid.NewGuid(), "retryDelaySeconds", JsonSerializer.SerializeToElement(1), saved.Revision), actor) is null,
            "Server runtime writes must bind the actual instance.");
        var restarted = new ServerRuntimeSettingsStore(path);
        Check(restarted.Current == saved, "Durable server settings must retain the instance identity and value after restart.");
        var stored = JsonSerializer.Deserialize<ServerRuntimeSettingsStore.StoredDocument>(File.ReadAllText(path), RelaxKonOSJsonOptions.Default)!;
        Check(stored.History.Single().Actor == actor && stored.History.Single().Before == original.Settings,
            "Server settings need an audited before/after change, without a fictional owner.");

        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={Path.Combine(root, "quiet.db")};Pooling=False").Options;
        await using (var db = new RelaxKonOSDbContext(options)) await db.Database.EnsureCreatedAsync();
        var factory = new CountingFactory(new PooledDbContextFactory<RelaxKonOSDbContext>(options));
        var cache = new CachedSqliteRegistryRepository(factory, runtime);
        await cache.StartAsync(CancellationToken.None);
        var startupReads = factory.Count;
        await Task.Delay(250);
        Check(factory.Count == startupReads, "An idle registry must not open persistence contexts.");
        var owner = Guid.NewGuid(); var workspace = Guid.NewGuid();
        for (var index = 0; index < 100; index++) cache.Upsert(new RegistryEntry
        {
            UserId = owner, Scope = RegistryScope.Workspace, ScopeId = workspace, Path = "Workspace\\QuietProbe", Name = "value",
            ValueType = RegistryValueType.Number, ValueJson = index.ToString(), DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = actor.ToString("D"),
        });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (cache.Find(owner, RegistryScope.Workspace, workspace, "Workspace\\QuietProbe", "value")?.AppliedRevision != 100 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(cache.Find(owner, RegistryScope.Workspace, workspace, "Workspace\\QuietProbe", "value")?.AppliedRevision == 100 && factory.Count == startupReads + 1,
            "A burst must persist the latest revision in a single event-triggered batch.");
        await Task.Delay(250);
        Check(factory.Count == startupReads + 1, "The write scheduler must return to idle after draining a batch.");
        runtime.Save(new(saved.InstanceId, "registryFlushDelayMilliseconds", JsonSerializer.SerializeToElement(30000), saved.Revision), actor);
        cache.Upsert(new RegistryEntry { UserId = owner, Scope = RegistryScope.Workspace, ScopeId = workspace, Path = "Workspace\\QuietProbe", Name = "shutdown", ValueType = RegistryValueType.String, ValueJson = "\"last\"" });
        await cache.StopAsync(CancellationToken.None);
        await using (var db = new RelaxKonOSDbContext(options))
            Check(await db.RegistryEntries.AnyAsync(entry => entry.Name == "shutdown"), "Graceful shutdown must force pending writes, regardless of configured delay.");
        await VerifyApiAsync(root, actor);
        await SettingsSystemVerification.VerifyQuietHttpAsync(root);
        Console.WriteLine("PASS: quiet event-driven persistence, burst batching, shutdown flush, instance-owned audited runtime settings and protected API.");
    }
    private static async Task VerifyApiAsync(string root, Guid actor)
    {
        var runtime = new ServerRuntimeSettingsStore(Path.Combine(root, "api-runtime.json"));
        var privilege = new TestHostAccountPrivilegeService();
        var grants = new HostElevationSessionStore(privilege, new UploadSessionChecks.SystemMode(), new HostElevationSessionState());
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization(); builder.Services.AddSingleton(runtime); builder.Services.AddSingleton<IHostElevationSessionStore>(grants);
        await using var app = builder.Build();
        app.Use((http, next) => { http.User = new(new ClaimsIdentity([new Claim("sub", actor.ToString("D")), new Claim("jti", "runtime-test"), new Claim("amr", "system")], "test")); return next(http); });
        app.UseAuthorization(); app.MapServerRuntimeEndpoints(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using (var denied = await http.GetAsync(ServerRuntimeRoutes.Settings))
        {
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "An ordinary user cannot inspect server runtime settings.");
            var problem = await denied.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default);
            Check(problem?.Type == "https://relaxkonos.app/problems/elevation-required",
                "Runtime denial must trigger the client's administrator verification flow.");
        }
        privilege.Level = HostAccountPrivilege.HostAdministrator;
        var snapshot = (await http.GetFromJsonAsync<ServerRuntimeSnapshot>(ServerRuntimeRoutes.Settings, RelaxKonOSJsonOptions.Default))!;
        using (var invalid = await http.PutAsJsonAsync(ServerRuntimeRoutes.Settings, new PutServerRuntimeFieldRequest(snapshot.InstanceId, "registryFlushDelayMilliseconds", JsonSerializer.SerializeToElement(0), snapshot.Revision), RelaxKonOSJsonOptions.Default))
            Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Unsafe write delays must be refused.");
        using (var response = await http.PutAsJsonAsync(ServerRuntimeRoutes.Settings, new PutServerRuntimeFieldRequest(snapshot.InstanceId, "notificationCoalesceMilliseconds", JsonSerializer.SerializeToElement(20), snapshot.Revision), RelaxKonOSJsonOptions.Default))
            Check(response.IsSuccessStatusCode, "A host administrator should update the instance configuration.");
        using (var stale = await http.PutAsJsonAsync(ServerRuntimeRoutes.Settings, new PutServerRuntimeFieldRequest(snapshot.InstanceId, "notificationCoalesceMilliseconds", JsonSerializer.SerializeToElement(30), snapshot.Revision), RelaxKonOSJsonOptions.Default))
            Check(stale.StatusCode == HttpStatusCode.Conflict, "The server API must preserve revision conflicts.");
        Check(runtime.Current.UpdatedBy == actor, "The protected API lost the real actor.");
        await app.StopAsync();
    }
    private sealed class CountingFactory(IDbContextFactory<RelaxKonOSDbContext> inner) : IDbContextFactory<RelaxKonOSDbContext>
    {
        public int Count;
        public RelaxKonOSDbContext CreateDbContext() { Interlocked.Increment(ref Count); return inner.CreateDbContext(); }
        public Task<RelaxKonOSDbContext> CreateDbContextAsync(CancellationToken ct = default) { Interlocked.Increment(ref Count); return inner.CreateDbContextAsync(ct); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
