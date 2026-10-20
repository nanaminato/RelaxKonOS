using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

internal static class MihomoProfilePublicationChecks
{
    internal static async Task<SqliteProxyProfileRepository> CreateAsync(string root)
    {
        var database = Path.Combine(root, "profiles.db");
        await HostGlobalMigrationRunner.MigrateAsync("Data Source=" + database, default);
        return new(new Environment(root), Options.Create(new StorageOptions { DatabasePath = database }));
    }

    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        async Task Denied(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected profile publication refusal: " + code);
        }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-profile-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = await CreateAsync(root);
            var first = await repository.UpsertAsync(null, "first", "mihomo", null, default);
            var second = await repository.UpsertAsync(null, "second", "mihomo", null, default);
            var selection = new MihomoProfilePublication(first.Id, first.Revision, null, null, first.CreatedAt, null);
            var operation = Guid.NewGuid(); var identity = new string('A', 64);
            await repository.ValidatePublicationAsync(selection, default);
            await repository.CommitPublicationAsync(operation, selection, identity, default);
            var committed = (await repository.GetAsync(first.Id, default))!;
            Check(committed.IsActive && committed.Revision == first.Revision + 1, "SQLite commits selected profile and revision");
            await repository.CommitPublicationAsync(operation, selection, identity, default);
            Check((await repository.GetAsync(first.Id, default))!.Revision == committed.Revision, "Exact operation replay does not increment revision twice");
            repository = await CreateAsync(root);
            await repository.CommitPublicationAsync(operation, selection, identity, default);
            Check((await repository.GetAsync(first.Id, default))!.Revision == committed.Revision, "Recreated repository and repeated migrations preserve exact operation marker");
            await Denied("idempotency_conflict", () => repository.CommitPublicationAsync(operation, selection, new string('B', 64), default));
            await Denied("idempotency_conflict", () => repository.CommitPublicationAsync(operation, selection with { PreviousActiveId = second.Id, PreviousActiveRevision = 1, PreviousActiveCreatedAt = second.CreatedAt }, identity, default));
            await Denied("profile_selection_conflict", () => repository.CommitPublicationAsync(Guid.NewGuid(), selection, identity, default));
            Check((await repository.GetAsync(first.Id, default))!.IsActive, "Rejected operation leaves the previous active selection intact");

            var stale = new MihomoProfilePublication(second.Id, second.Revision, first.Id, committed.Revision, second.CreatedAt, first.CreatedAt);
            committed = await repository.UpsertAsync(first.Id, "edited first", "mihomo", committed.Revision, default);
            await Denied("profile_selection_conflict", () => repository.CommitPublicationAsync(Guid.NewGuid(), stale, identity, default));
            await Denied("profile_selection_conflict", () => repository.CommitPublicationAsync(operation, selection, identity, default));
            var deleted = await repository.UpsertAsync(null, "deleted", "mihomo", null, default);
            await repository.DeleteAsync(deleted.Id, default);
            await Denied("profile_selection_conflict", () => repository.ValidatePublicationAsync(new(deleted.Id, deleted.Revision, first.Id, committed.Revision, deleted.CreatedAt, first.CreatedAt), default));
            await Denied("profile_selection_invalid", () => repository.ValidatePublicationAsync(new(Guid.Empty, 1, null, null, DateTimeOffset.UtcNow, null), default));
            await Denied("profile_selection_invalid", () => repository.ValidatePublicationAsync(new(first.Id, long.MaxValue, null, null, first.CreatedAt, null), default));

            var next = stale with { PreviousActiveRevision = committed.Revision };
            await using var database = new SqliteConnection("Data Source=" + Path.Combine(root, "profiles.db"));
            await database.OpenAsync();
            async Task Sql(string sql) { await using var command = database.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
            await Sql("CREATE TRIGGER reject_marker BEFORE UPDATE ON proxy_profile_publication BEGIN SELECT RAISE(ABORT, 'injected marker write failure'); END;");
            var nextOperation = Guid.NewGuid();
            await Denied("profile_store_unavailable", () => repository.CommitPublicationAsync(nextOperation, next, identity, default));
            Check((await repository.GetAsync(first.Id, default))!.IsActive && !(await repository.GetAsync(second.Id, default))!.IsActive
                && (await repository.GetAsync(second.Id, default))!.Revision == second.Revision,
                "A failed marker write atomically rolls back selection and revision");
            await Sql("DROP TRIGGER reject_marker;");
            await repository.CommitPublicationAsync(nextOperation, next, identity, default);
            Check((await repository.GetAsync(second.Id, default))!.IsActive && !(await repository.GetAsync(first.Id, default))!.IsActive,
                "The same accepted operation succeeds after storage recovers");
            await repository.CommitPublicationAsync(nextOperation, next, identity, default);
            Check((await repository.GetAsync(second.Id, default))!.Revision == second.Revision + 1, "Recovered marker remains idempotent");

            var concurrentSelection = new MihomoProfilePublication(first.Id, committed.Revision, second.Id, second.Revision + 1, first.CreatedAt, second.CreatedAt);
            async Task<bool> CommitConcurrent()
            {
                try { await repository.CommitPublicationAsync(Guid.NewGuid(), concurrentSelection, identity, default); return true; }
                catch (RuntimePolicyException ex) when (ex.ProblemCode is "runtime.profile_selection_conflict" or "runtime.instance_busy") { return false; }
            }
            var outcomes = await Task.WhenAll(Task.Run(CommitConcurrent), Task.Run(CommitConcurrent));
            Check(outcomes.Count(x => x) == 1 && (await repository.GetAsync(first.Id, default))!.Revision == committed.Revision + 1,
                "Concurrent distinct operations cannot both claim one frozen selection");
            var recycled = await repository.UpsertAsync(null, "recycled", "mihomo", null, default);
            var currentActive = (await repository.GetAsync(first.Id, default))!;
            var recycleSelection = new MihomoProfilePublication(recycled.Id, recycled.Revision, first.Id,
                currentActive.Revision, recycled.CreatedAt, first.CreatedAt);
            var recycleOperation = Guid.NewGuid();
            await repository.CommitPublicationAsync(recycleOperation, recycleSelection, identity, default);
            await repository.SetActiveAsync(first.Id, default);
            await repository.DeleteAsync(recycled.Id, default);
            var recreated = await repository.UpsertAsync(recycled.Id, "recreated", "mihomo", null, default);
            await repository.SetActiveAsync(recreated.Id, default);
            Check((await repository.GetAsync(recreated.Id, default))!.Revision == recycled.Revision + 1
                && recreated.CreatedAt != recycled.CreatedAt, "Recreated profile may reuse ID and revision but has a distinct creation identity");
            await Denied("profile_selection_conflict", () => repository.CommitPublicationAsync(recycleOperation, recycleSelection, identity, default));

        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        return count;
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MihomoProfilePublicationChecks";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
