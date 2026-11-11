using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.Storage.Sqlite;

internal static class OptimizationChecks
{
    public static async Task RunAsync(string root)
    {
        ObservabilityChecks.VerifyProtocolAndSanitization();
        var sanitizer = new ObservabilitySanitizer(new ObservabilityOptions());
        foreach (var input in new[]
        {
            "{\"password\":\"example secret\"}", "{'token':'example secret'}",
            "secret=\"example secret\"", "{\"API_KEY\":\"example\\\"secret\"}",
            "password=\"example secret", "token='example secret", "password=example secret"
        })
        {
            TestAssert.True(!sanitizer.SanitizeSummary(input).Contains("example", StringComparison.Ordinal), "Quoted secrets must be redacted");
            TestAssert.True(!sanitizer.SanitizeException(new InvalidOperationException(input)).Summary.Contains("example", StringComparison.Ordinal), "Exception secrets must be redacted");
        }
        TestAssert.Equal("ordinary message", sanitizer.SanitizeSummary("ordinary message"), "Normal text must survive");
        TestAssert.Equal(8, sanitizer.SanitizeSummary("password=\"example secret\"", 8).Length, "Truncation follows redaction");
        var longInput = "token=\"example secret\", message=" + new string('x', 100000);
        var boundedSummary = sanitizer.SanitizeSummary(longInput);
        TestAssert.True(boundedSummary.Length == 1024 && !boundedSummary.Contains("example", StringComparison.Ordinal), "Long input is redacted before its output is bounded");

        var memory = new InMemoryBrowserRepository();
        VerifyOwnership(memory);
        var user = Guid.NewGuid();
        Parallel.For(0, 1000, _ => memory.UpsertHistory(user, "Same", "https://same.test"));
        TestAssert.Equal(1000, memory.ListHistory(user, 0, 1).Single().VisitCount, "Concurrent visits must not lose increments");
        Parallel.For(0, 1000, _ => memory.UpsertBookmark(user, "Same", "https://same.test"));
        TestAssert.Equal(1, memory.ListBookmarks(user, 0, 500).Count, "Concurrent upserts must not leave duplicate IDs");
        TestAssert.True(memory.DeleteBookmark(user, memory.ListBookmarks(user, 0, 1).Single().Id), "Concurrent bookmark remains deletable");
        TestAssert.Equal(0, memory.ClearBookmarks(user), "Deletion clears both indexes");
        var snapshot = memory.UpsertHistory(user, "Copy", "https://snapshot.test");
        memory.UpsertHistory(user, "Copy", "https://snapshot.test");
        TestAssert.Equal(1, snapshot.VisitCount, "Returned history snapshots are immutable relative to later writes");
        Parallel.For(0, 500, i =>
        {
            var entry = memory.UpsertBookmark(user, "Race", $"https://race.test/{i % 20}");
            if (i % 3 == 0) memory.DeleteBookmark(user, entry.Id);
            if (i % 7 == 0) memory.ClearBookmarks(user);
        });
        foreach (var entry in memory.ListBookmarks(user, 0, 500))
            TestAssert.True(memory.DeleteBookmark(user, entry.Id), "Every remaining URL index has a deletable ID after mixed writes");

        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={Path.Combine(root, "browser.db")}").Options;
        using var db = new RelaxKonOSDbContext(options);
        db.Database.EnsureCreated();
        var sqlite = new SqliteBrowserRepository(db);
        VerifyOwnership(sqlite);
        foreach (var count in new[] { 1000, 10000 })
        {
            var capacityUser = Guid.NewGuid();
            var rows = Enumerable.Range(0, count).Select(i => new Bookmark
            {
                Id = Guid.NewGuid(), UserId = capacityUser, Title = $"Item {i:D5}", Url = $"https://test.local/{i}", CreatedAt = DateTimeOffset.UtcNow
            }).ToArray();
            db.Bookmarks.AddRange(rows);
            db.SaveChanges();
            db.ChangeTracker.Clear();
            sqlite.ListBookmarks(capacityUser, 0, 100); // warm the query
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var page = sqlite.ListBookmarks(capacityUser, 0, 100);
            timer.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TestAssert.Equal(100, page.Count, "Large catalogs are bounded");
            TestAssert.Equal(500, sqlite.ListBookmarks(capacityUser, 0, int.MaxValue).Count, "Oversized queries are bounded");
            TestAssert.Equal(1, sqlite.ListBookmarks(capacityUser, -1, 0).Count, "Zero limit cannot mean unlimited");
            var next = sqlite.ListBookmarks(capacityUser, 100, 100);
            TestAssert.True(!page.Select(x => x.Id).Intersect(next.Select(x => x.Id)).Any(), "Pages must not overlap for an unchanged catalog");
            var exact = sqlite.ListBookmarks(capacityUser, 0, 1, rows[^1].Url);
            TestAssert.Equal(rows[^1].Id, exact.Single().Id, "URL lookup finds bookmarks outside the first page");
            var pageBytes = JsonSerializer.SerializeToUtf8Bytes(page.Select(item => item.ToDto())).Length;
            db.Bookmarks.AsNoTracking().Where(item => item.UserId == capacityUser).OrderBy(item => item.Title).ToList();
            var baselineAllocated = GC.GetAllocatedBytesForCurrentThread();
            var baselineTimer = Stopwatch.StartNew();
            var baseline = db.Bookmarks.AsNoTracking().Where(item => item.UserId == capacityUser).OrderBy(item => item.Title).ToList();
            baselineTimer.Stop();
            baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineAllocated;
            var baselineBytes = JsonSerializer.SerializeToUtf8Bytes(baseline.Select(item => item.ToDto())).Length;
            Console.WriteLine($"Browser {count} rows: full query {baselineTimer.Elapsed.TotalMilliseconds:F2} ms/{baselineAllocated} bytes allocated/{baselineBytes} response bytes; 100-item query {timer.Elapsed.TotalMilliseconds:F2} ms/{allocated} bytes allocated/{pageBytes} response bytes");
        }
        await EventAlertChecks.VerifyAppendProjectionAndRecoveryAsync(root);
        await VerifyBrowserHttpAsync();
        await VerifyReadSnapshotsAsync(root);
        await BenchmarkEventsAsync(root);
        Console.WriteLine("Optimization ownership, capacity, sanitization and event checks passed.");
    }

    private static void VerifyOwnership(IBrowserRepository repo)
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var bookmark = repo.UpsertBookmark(owner, "Owned", "https://owned.test");
        var history = repo.UpsertHistory(owner, "Owned", "https://owned.test");
        TestAssert.True(!repo.DeleteBookmark(other, bookmark.Id), "Foreign bookmark deletion must be rejected");
        TestAssert.True(!repo.DeleteHistory(other, history.Id), "Foreign history deletion must be rejected");
        TestAssert.Equal(1, repo.ListBookmarks(owner, 0, 100).Count, "Foreign deletion preserves bookmark");
        TestAssert.True(repo.DeleteBookmark(owner, bookmark.Id), "Owner can delete after foreign rejection");
        TestAssert.True(repo.DeleteHistory(owner, history.Id), "Owner can delete history after foreign rejection");
        TestAssert.True(!repo.DeleteBookmark(owner, bookmark.Id), "Repeated bookmark delete is false");
        TestAssert.Equal(0, repo.ClearHistory(owner), "Both history indexes are cleared");
        for (var i = 0; i < 12; i++) repo.UpsertHistory(owner, $"History {i}", $"https://history.test/{i}");
        TestAssert.Equal(1, repo.ListHistory(owner, 0, 0).Count, "History zero limit is bounded");
        TestAssert.Equal(5, repo.ListHistory(owner, 0, 5).Count, "History page size is honored");
        TestAssert.True(!repo.ListHistory(owner, 0, 5).Select(x => x.Id).Intersect(repo.ListHistory(owner, 5, 5).Select(x => x.Id)).Any(), "History pages do not overlap");
        TestAssert.Equal(0, repo.ListHistory(other, 0, 100).Count, "History is isolated by user");
    }

    public static async Task BenchmarkEventsAsync(string root)
    {
        var store = new EventAlertStore(new TestHostEnvironment(root), new EventAlertsOptions { DatabasePath = "event-benchmark.db" }, new ObservabilitySanitizer(new ObservabilityOptions()));
        var publisher = new OperationalEventPublisher(store, new ObservabilitySanitizer(new ObservabilityOptions()));
        for (var i = 0; i < 1000; i++)
            await publisher.PublishAsync(new($"seed-{i}", "deployment.operation_failed", Guid.NewGuid(), Guid.NewGuid(), "deployment.build_failed"));
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var timer = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var started = Stopwatch.GetTimestamp();
                if (worker == 0)
                    await publisher.PublishAsync(new($"mixed-{i}", "deployment.operation_failed", Guid.NewGuid(), Guid.NewGuid(), "deployment.build_failed"));
                else
                    await store.ListEventsAsync(100, null, null, null, null, CancellationToken.None);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        })));
        var sorted = latencies.Order().ToArray();
        Console.WriteLine($"Event mixed 8 workers/800 operations: {timer.Elapsed.TotalMilliseconds:F1} ms; P50 {sorted[400]:F2}, P95 {sorted[760]:F2}, P99 {sorted[792]:F2} ms; writes/s {100 / timer.Elapsed.TotalSeconds:F1}");
        var all = new HashSet<Guid>(); string? cursor = null;
        do
        {
            var page = await store.ListEventsAsync(100, cursor, null, null, null, CancellationToken.None);
            foreach (var item in page.Items) TestAssert.True(all.Add(item.EventId), "Event pagination cannot duplicate IDs");
            cursor = page.NextCursor;
        } while (cursor is not null);
        TestAssert.Equal(1100, all.Count, "Mixed load preserves every event");
        var alerts = new HashSet<Guid>(); cursor = null;
        do
        {
            var page = await store.ListAlertsAsync(100, cursor, null, null, CancellationToken.None);
            foreach (var item in page.Items) TestAssert.True(alerts.Add(item.AlertId), "Alert pagination cannot duplicate IDs");
            cursor = page.NextCursor;
        } while (cursor is not null);
        TestAssert.Equal(1100, alerts.Count, "All alerts remain queryable after mixed load");
    }

    private static async Task VerifyReadSnapshotsAsync(string root)
    {
        var options = new EventAlertsOptions { DatabasePath = "snapshot-check.db" };
        var sanitizer = new ObservabilitySanitizer(new ObservabilityOptions());
        var store = new EventAlertStore(new TestHostEnvironment(root), options, sanitizer);
        // Concurrent first-use initialization must not race the schema creation.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.GetSummaryAsync(CancellationToken.None))));
        var publisher = new OperationalEventPublisher(store, sanitizer);
        await publisher.PublishAsync(new("snapshot-event", "deployment.operation_failed", Guid.NewGuid(), Guid.NewGuid(), "deployment.build_failed"));
        var alert = (await store.ListAlertsAsync(10, null, null, null, CancellationToken.None)).Items.Single();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, options.DatabasePath)}");
        connection.Open();
        using (var transaction = connection.BeginTransaction())
        {
            using var delete = connection.CreateCommand(); delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM operational_events"; delete.ExecuteNonQuery();
            var detail = await store.GetDetailAsync(alert.AlertId, CancellationToken.None);
            TestAssert.Equal(1, detail!.Events.Count, "WAL readers see the committed detail snapshot during an uncommitted write");
            transaction.Rollback();
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await store.ListEventsAsync(10, null, null, null, null, canceled.Token); throw new InvalidOperationException("Canceled read unexpectedly succeeded"); }
        catch (OperationCanceledException) { }
        TestAssert.Equal(1, (await store.ListEventsAsync(10, null, null, null, null, CancellationToken.None)).Items.Count, "Cancellation and rollback preserve event state");
        await store.RunRetentionAsync(CancellationToken.None);
        TestAssert.Equal(1, (await store.ListEventsAsync(10, null, null, null, null, CancellationToken.None)).Items.Count, "Retention preserves recent events");
    }

    private static async Task VerifyBrowserHttpAsync()
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var repo = new InMemoryBrowserRepository();
        for (var i = 0; i < 550; i++)
        {
            repo.UpsertBookmark(owner, $"Page {i:D3}", $"https://http.test/{i}");
            repo.UpsertHistory(owner, $"Page {i:D3}", $"https://http.test/{i}");
        }
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IBrowserRepository>(repo);
        builder.Services.AddSingleton(DispatchProxy.Create<IWorkspaceRepository, OptimizationUnusedService>());
        builder.Services.AddSingleton(DispatchProxy.Create<IRegistryRepository, OptimizationUnusedService>());
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddAuthentication("Bearer").AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false, ValidateAudience = false, ValidateIssuerSigningKey = true,
            IssuerSigningKey = key, ValidateLifetime = true
        });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapBrowserEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using (var unauthorized = await http.GetAsync(BrowserApiRoutes.Bookmarks))
            TestAssert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode, "Browser collections require authentication");
        string Token(Guid user) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(JwtRegisteredClaimNames.Sub, user.ToString("D"))], expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token(owner));
        var first = await http.GetFromJsonAsync<BookmarkDto[]>(BrowserApiRoutes.Bookmarks);
        TestAssert.Equal(100, first!.Length, "HTTP defaults to a bounded bookmark page");
        TestAssert.Equal(500, (await http.GetFromJsonAsync<BookmarkDto[]>(BrowserApiRoutes.Bookmarks + "?limit=10000"))!.Length, "HTTP enforces bookmark maximum");
        var next = await http.GetFromJsonAsync<BookmarkDto[]>(BrowserApiRoutes.Bookmarks + "?offset=100&limit=100");
        TestAssert.True(!first.Select(x => x.Id).Intersect(next!.Select(x => x.Id)).Any(), "HTTP bookmark offset reaches the next page");
        var lookup = await http.GetFromJsonAsync<BookmarkDto[]>(BrowserApiRoutes.Bookmarks + "?url=https%3A%2F%2Fhttp.test%2F549&limit=1");
        TestAssert.Equal("https://http.test/549", lookup!.Single().Url, "HTTP exact lookup reaches unloaded bookmarks");
        TestAssert.Equal(1, (await http.GetFromJsonAsync<HistoryEntryDto[]>(BrowserApiRoutes.History + "?limit=0"))!.Length, "HTTP history zero is bounded");
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token(other));
        using (var denied = await http.DeleteAsync(BrowserApiRoutes.BookmarksDelete.Replace("{id}", first[0].Id.ToString("D"))))
            TestAssert.Equal(HttpStatusCode.NotFound, denied.StatusCode, "HTTP foreign delete is rejected");
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token(owner));
        using (var deleted = await http.DeleteAsync(BrowserApiRoutes.BookmarksDelete.Replace("{id}", first[0].Id.ToString("D"))))
            TestAssert.Equal(HttpStatusCode.NoContent, deleted.StatusCode, "HTTP owner can delete after rejection");
        await app.StopAsync();
    }
}

public class OptimizationUnusedService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw new NotSupportedException("Settings dependencies are not used by collection checks.");
}
