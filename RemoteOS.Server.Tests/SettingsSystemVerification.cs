using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Protocol.Desktop;
using Server.ConfigurationRegistry;
using Server.Domain;
using Server.Settings;
using Server.Storage;
using Server.Storage.Sqlite;

internal static class SettingsSystemVerification
{
    public static async Task RunAsync(string root)
    {
        Verify(new InMemoryRegistryRepository());
        var options = new DbContextOptionsBuilder<RemoteOsDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "settings-concurrency.db")};Pooling=False").Options;
        await using (var db = new RemoteOsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            Verify(new SqliteRegistryRepository(db), concurrent: false);
        }
        var factory = new PooledDbContextFactory<RemoteOsDbContext>(options);
        var cache = new CachedSqliteRegistryRepository(factory);
        await cache.StartAsync(CancellationToken.None);
        var workspace = Verify(cache);
        var before = new WorkspaceSettingsService(cache).Read(workspace);
        await cache.StopAsync(CancellationToken.None);
        var restarted = new CachedSqliteRegistryRepository(factory);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            var restored = new WorkspaceSettingsService(restarted).Read(workspace);
            Check(restored.Revision == before.Revision && restored.Theme == before.Theme,
                "Preference value and revision must survive cache flush and restart.");
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
        Console.WriteLine("Settings verification passed: stale writes, parallel writers, tenant isolation, corrupt data, SQLite restart.");
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
        Check(registry.Find(corrupt.UserId, RemoteOS.Protocol.Registry.RegistryScope.Workspace, corrupt.Id,
            WorkspaceConfigurationRegistry.DesktopPath, WorkspaceConfigurationRegistry.DefaultValueName)!.ValueJson.Contains("malformed"),
            "Corruption must preserve recovery evidence.");
        return workspace;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
