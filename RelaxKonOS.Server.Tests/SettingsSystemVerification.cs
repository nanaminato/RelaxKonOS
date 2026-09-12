using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Hubs;

internal static class SettingsSystemVerification
{
    public static async Task RunAsync(string root)
    {
        await SettingsOperationVerification.RunAsync(root);
        await VerifyHttpAsync(root);
        Verify(new InMemoryRegistryRepository());
        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "settings-concurrency.db")};Pooling=False").Options;
        await using (var db = new RelaxKonOSDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            Verify(new SqliteRegistryRepository(db), concurrent: false);
        }
        var factory = new PooledDbContextFactory<RelaxKonOSDbContext>(options);
        var cache = new CachedSqliteRegistryRepository(factory);
        await cache.StartAsync(CancellationToken.None);
        var workspace = Verify(cache);
        var before = new WorkspaceSettingsService(cache).Read(workspace);
        Check(before.PersistedRevision is null, "Cached acceptance must not claim durable persistence before flush.");
        await cache.StopAsync(CancellationToken.None);
        var restarted = new CachedSqliteRegistryRepository(factory);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            var restored = new WorkspaceSettingsService(restarted).Read(workspace);
            Check(restored.PersistedRevision == restored.Revision, "Restarted SQLite snapshot must report the durable revision.");
            Check(restored.Revision == before.Revision && restored.Theme == before.Theme,
                "Preference value and revision must survive cache flush and restart.");
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
        Console.WriteLine("Settings verification passed: stale writes, parallel writers, tenant isolation, corrupt data, SQLite restart.");
    }

    private static async Task VerifyHttpAsync(string root)
    {
        var owner = Guid.NewGuid();
        var workspace = new Workspace { Id = Guid.NewGuid(), UserId = owner };
        var workspaces = new InMemoryWorkspaceRepository();
        workspaces.Add(workspace);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<SettingsSubscriptions>();
        builder.Services.AddHostedService<SettingsChangesBroadcastService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            foreach (var converter in RelaxKonOSJsonOptions.Default.Converters)
                options.SerializerOptions.Converters.Add(converter);
        });
        builder.Services.AddSingleton<IWorkspaceRepository>(workspaces);
        builder.Services.AddSingleton<IRegistryRepository, InMemoryRegistryRepository>();
        builder.Services.AddScoped<IWorkspaceSettingsService, WorkspaceSettingsService>();
        builder.Services.AddSingleton<WorkspaceWallpaperStore>();
        builder.Services.Configure<StorageOptions>(_ => { });
        await using var app = builder.Build();
        // Test-only authenticated principals exercise production authorization/ownership checks.
        // This harness is bound solely to ephemeral loopback, never a remote configuration target.
        app.Use(async (context, next) =>
        {
            var subject = context.Request.Headers["X-Test-Subject"].FirstOrDefault() ?? owner.ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, subject), new Claim("workspace_id", workspace.Id.ToString())
            ], "settings-test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapWorkspaceEndpoints();
        app.MapRegistryEndpoints();
        app.MapHub<SettingsChangesHub>(RelaxKonOSEndpoints.SettingsChangesHubPath);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var route = WorkspaceApiRoutes.Preferences.Replace("{id}", workspace.Id.ToString());
            var initial = await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default);
            Check(initial?.Revision > 0, "HTTP GET must return a preference revision.");
            using var missing = await http.PutAsJsonAsync(route, initial! with { Revision = null }, RelaxKonOSJsonOptions.Default);
            Check((int)missing.StatusCode == 428, "HTTP PUT without revision must return 428.");
            using var saved = await http.PutAsJsonAsync(route, initial! with { Theme = ThemeKind.Dark }, RelaxKonOSJsonOptions.Default);
            Check(saved.IsSuccessStatusCode, "Versioned HTTP preference write failed.");
            using var stale = await http.PutAsJsonAsync(route, initial!, RelaxKonOSJsonOptions.Default);
            Check(stale.StatusCode == HttpStatusCode.Conflict, "Stale HTTP PUT must return 409.");
            using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, route);
            foreignRequest.Headers.Add("X-Test-Subject", Guid.NewGuid().ToString());
            using var foreign = await http.SendAsync(foreignRequest);
            Check(foreign.StatusCode == HttpStatusCode.NotFound, "Cross-user HTTP reads must not reveal preferences.");
            using var foreignWrite = new HttpRequestMessage(HttpMethod.Put, route)
            {
                Content = JsonContent.Create(initial!, options: RelaxKonOSJsonOptions.Default)
            };
            foreignWrite.Headers.Add("X-Test-Subject", Guid.NewGuid().ToString());
            using var denied = await http.SendAsync(foreignWrite);
            Check(denied.StatusCode == HttpStatusCode.NotFound, "Cross-user HTTP writes must be rejected.");

            var value = System.Text.Json.JsonSerializer.SerializeToElement(initial!, RelaxKonOSJsonOptions.Default);
            using var registryStale = await http.PutAsJsonAsync(RegistryApiRoutes.Entries,
                new PutRegistryEntryRequest(RegistryScope.Workspace, WorkspaceConfigurationRegistry.DesktopPath,
                    WorkspaceConfigurationRegistry.DefaultValueName, RegistryValueType.Json, value, initial!.Revision), RelaxKonOSJsonOptions.Default);
            Check(registryStale.StatusCode == HttpStatusCode.Conflict, "The registry editor must not bypass preference revisions.");
            using var deleted = await http.DeleteAsync(RegistryApiRoutes.Entries + "?scope=Workspace&path=Workspace%5CDesktop&name=%28Default%29");
            Check(deleted.StatusCode == HttpStatusCode.Conflict, "Deleting managed preferences must not reset their revision.");
            await SettingsNotificationsVerification.RunAsync(address, owner, workspace.Id, async () =>
            {
                var current = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
                using var response = await http.PutAsJsonAsync(route, current with { Language = "ja-JP" }, RelaxKonOSJsonOptions.Default);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<WorkspacePreferencesDto>(RelaxKonOSJsonOptions.Default))!.Revision!.Value;
            });
            Console.WriteLine("Settings HTTP verification passed: 428, 409, cross-user read/write denial, registry bypass rejection.");
        }
        finally { await app.StopAsync(); }
    }

    private static Workspace Verify(IRegistryRepository registry, bool concurrent = true)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        var service = new WorkspaceSettingsService(registry);
        var initial = service.Read(workspace);
        Check(initial.Revision > 0, "Reads must supply the observed revision without opening a window.");
        var changed = service.Save(workspace, initial with { Theme = ThemeKind.Dark }, "test")!;
        Check(changed.Revision > initial.Revision, "A successful mutation must advance the revision.");
        Check(service.Save(workspace, initial with { Language = "ja-JP" }, "stale") is null,
            "A stale draft must not overwrite another client's theme.");
        Check(service.Read(workspace).Theme == ThemeKind.Dark, "Conflict handling lost the committed theme.");
        var other = new Workspace { Id = workspace.Id, UserId = Guid.NewGuid() };
        Check(service.Read(other).Theme == ThemeKind.Light, "User keys must isolate the same workspace id.");

        if (concurrent)
        {
            var successes = 0;
            Parallel.For(0, 32, _ =>
            {
                if (service.Save(workspace, changed with { Region = "ja-JP" }, "parallel") is not null)
                    Interlocked.Increment(ref successes);
            });
            Check(successes == 1, "Exactly one writer may commit against a shared revision.");
        }
        var corrupt = new Workspace { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        WorkspaceConfigurationRegistry.Write(registry, corrupt, WorkspaceConfigurationRegistry.DesktopPath, new { malformed = true }, "test");
        try { service.Read(corrupt); throw new Exception("Corrupt preferences were silently replaced with defaults."); }
        catch (InvalidDataException) { }
        Check(registry.Find(corrupt.UserId, RelaxKonOS.Protocol.Registry.RegistryScope.Workspace, corrupt.Id,
            WorkspaceConfigurationRegistry.DesktopPath, WorkspaceConfigurationRegistry.DefaultValueName)!.ValueJson.Contains("malformed"),
            "Corruption must preserve recovery evidence.");
        return workspace;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
