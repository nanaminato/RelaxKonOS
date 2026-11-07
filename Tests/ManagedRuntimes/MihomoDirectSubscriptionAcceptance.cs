using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

internal static class MihomoDirectSubscriptionAcceptance
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static async Task RunAsync(string root, string assembly, string socket, LoginRequest login, ProxySystemdGuardProbe guard)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = budget.Token;
        var paths = new ProxyPlatformPaths();
        var directory = paths.GetStateDirectory();
        var settingsPath = Path.Combine(directory, "mihomo-settings.json");
        var runtimePath = Path.Combine(directory, "mihomo-runtime.json");
        var activePath = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        var originalSettings = await File.ReadAllBytesAsync(settingsPath, ct);
        var originalYaml = await File.ReadAllBytesAsync(activePath, ct);
        var store = new MihomoPublicationStore(paths, ManagedServiceChecks.OpenProtection(root));
        if ((await store.ReadAsync(ct)) is not { Published: null, Pending: null, DirectReload: null } || File.Exists(runtimePath))
            throw new InvalidOperationException("The direct subscription check requires isolated, unclaimed Mihomo state.");

        var version = MihomoRuntimeManifest.SupportedVersion;
        var bin = Directory.CreateDirectory(Path.Combine(paths.GetEngineVersionsDirectory(MihomoEngine.Id),
            version + "-" + MihomoRuntimeManifest.CurrentRid())).FullName;
        var executable = Path.Combine(bin, "mihomo");
        var data = Directory.CreateDirectory(paths.GetEngineDataDirectory(MihomoEngine.Id)).FullName;
        File.Copy(guard.RuntimeExecutable, executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await File.WriteAllTextAsync(runtimePath, JsonSerializer.Serialize(new { ActiveVersion = version, ActivatedAt = DateTimeOffset.UtcNow }), ct);
        var settings = await new ProxySettingsReader(paths).GetAsync(ct);
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings with { AllowInsecureSubscriptionSources = true }), ct);
        var owner = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID") + ":" +
            Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID");
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown", [owner, "--", data], 256);
        // Earlier native-writer cases create the private profile directory as root.
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown",
            ["-R", owner, "--", paths.GetProtectedConfigurationDirectory()], 256);

        var feeds = 0;
        var draftFeeds = 0;
        var skippedFeeds = 0;
        var windowFeeds = 0;
        var originBuilder = WebApplication.CreateBuilder();
        originBuilder.Logging.ClearProviders();
        originBuilder.WebHost.UseUrls("http://93.184.216.34:0");
        await using var origin = originBuilder.Build();
        origin.MapGet("/feed", () =>
        {
            var revision = Interlocked.Increment(ref feeds);
            return $"vless://550e8400-e29b-41d4-a716-446655440000@origin-{revision}.example.test:443#fixture-{revision}";
        });
        origin.MapGet("/draft", () => $"vless://550e8400-e29b-41d4-a716-446655440000@draft-{Interlocked.Increment(ref draftFeeds)}.example.test:443#draft");
        origin.MapGet("/skipped", () => $"vless://550e8400-e29b-41d4-a716-446655440000@skipped-{Interlocked.Increment(ref skippedFeeds)}.example.test:443#skipped");
        origin.MapGet("/draft-window", () => $"vless://550e8400-e29b-41d4-a716-446655440000@window-{Interlocked.Increment(ref windowFeeds)}.example.test:443#window");
        await origin.StartAsync(ct);

        using var native = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new("http://127.0.0.1:9090/") };
        var relayBuilder = WebApplication.CreateBuilder();
        relayBuilder.Logging.ClearProviders();
        relayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var relay = relayBuilder.Build();
        var reloadAttempts = 0;
        var reloads = 0;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unacknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.Run(async context =>
        {
            var reload = HttpMethods.IsPut(context.Request.Method) && context.Request.Path == "/configs";
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
            if (reload)
            {
                Interlocked.Increment(ref reloadAttempts);
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
            using var response = await native.SendAsync(request, context.RequestAborted);
            if (reload && response.IsSuccessStatusCode)
            {
                var count = Interlocked.Increment(ref reloads);
                if (count == 2)
                {
                    committed.TrySetResult();
                    await release.Task.WaitAsync(context.RequestAborted);
                }
                if (count == 3)
                {
                    unacknowledged.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
            }
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await relay.StartAsync(ct);

        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        var secrets = new DataProtectionProxyControllerSecretStore(
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = start.WorkingDirectory },
            ManagedServiceChecks.OpenProtection(root));
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Proxy__Mihomo__Controller__Endpoint"] = Address(relay) + "/";
        start.Environment["Proxy__Mihomo__Controller__TimeoutSeconds"] = "30";
        var database = Path.Combine(start.WorkingDirectory, "direct-subscription.db");
        start.Environment["Storage__DatabasePath"] = database;
        try
        {
            await guard.VerifyTrafficAsync((_, token) => VerifyDraftCommitWindowAsync(token), TimeSpan.FromSeconds(55));
            await guard.VerifyTrafficAsync(async (_, trafficCt) =>
            {
                var pid = guard.RuntimePid;
                var key = Guid.NewGuid().ToString("N");
                Guid operation;
                ProxySubscriptionDto subscription;
                ProxySubscriptionDto draft;
                ProxySubscriptionDto skipped;
                MihomoDirectReload checkpoint;
                Guid child;
                long revision;
                var unknownKey = Guid.NewGuid().ToString("N");
                Guid unknown;
                Guid unknownChild;
                byte[] acknowledgedYaml;
                MihomoDirectReload unknownCheckpoint;
                using (var server = ProxyAuthenticatedPublicationChecks.Start(start, Console.WriteLine))
                {
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, trafficCt);
                        draft = await ImportAsync(client, "/draft", "00-draft", trafficCt);
                        subscription = await ImportAsync(client, "/feed", "50-active", trafficCt);
                        skipped = await ImportAsync(client, "/skipped", "99-skipped", trafficCt);
                        var activation = await SubmitAsync(client, SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, subscription.Id),
                            Guid.NewGuid().ToString("N"), trafficCt);
                        var activated = await WaitForTerminalAsync(client, activation, trafficCt);
                        if (activated.State != ProxyOperationState.Succeeded)
                            throw new InvalidOperationException($"The direct subscription could not be activated: {activated.ProblemCode}.");
                        revision = await ReadRevisionAsync(subscription.ProfileId, trafficCt);
                        operation = await SubmitAsync(client, ProxyApiRoutes.SubscriptionsRefresh, key, trafficCt);
                        while (!committed.Task.IsCompleted)
                        {
                            if ((await ReadOperationAsync(client, operation, trafficCt)).State is not (ProxyOperationState.Running or ProxyOperationState.Queued))
                                throw new InvalidOperationException("The direct refresh ended before the real reload response.");
                            await Task.WhenAny(committed.Task, Task.Delay(30, trafficCt)).WaitAsync(trafficCt);
                        }
                        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                        await connection.OpenAsync(trafficCt);
                        await using var writeLock = connection.BeginTransaction();
                        release.TrySetResult();
                        while ((await store.ReadAsync(trafficCt)).DirectReload is not { ProfileReloadConfirmed: true })
                            await Task.Delay(25, trafficCt);
                        checkpoint = (await store.ReadAsync(trafficCt)).DirectReload!;
                        var steps = await ReadBatchAsync(operation, trafficCt);
                        child = steps[1].OperationId;
                        if (steps.Length != 3 || steps[0].SubscriptionId != draft.Id || steps[1].SubscriptionId != subscription.Id
                            || steps[2].SubscriptionId != skipped.Id
                            || !steps.Select(step => step.State).SequenceEqual([ProxyOperationStore.RefreshStepState.Succeeded,
                                ProxyOperationStore.RefreshStepState.Running, ProxyOperationStore.RefreshStepState.Planned])
                            || checkpoint.OperationId != child || checkpoint.Profile?.Refresh?.SubscriptionId != subscription.Id
                            || (await ReadOperationAsync(client, operation, trafficCt)).State != ProxyOperationState.Running
                            || checkpoint.Profile.ExpectedRevision != revision || await ReadLeasesAsync(client, trafficCt) != 1
                            || Volatile.Read(ref feeds) != 2 || Volatile.Read(ref draftFeeds) != 2 || Volatile.Read(ref skippedFeeds) != 1
                            || Volatile.Read(ref reloads) != 2 || Volatile.Read(ref reloadAttempts) != 2)
                            throw new InvalidOperationException("The refresh lost its original lease or identity before the SQLite commit.");
                        server.Kill();
                        await server.WaitForExitAsync(trafficCt);
                        if (server.ExitCode == 0) throw new InvalidOperationException("The original Server did not crash.");
                    }
                    finally { await ProxyAuthenticatedPublicationChecks.StopAsync(server); }
                }

                using var restarted = ProxyAuthenticatedPublicationChecks.Start(start, Console.WriteLine);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(restarted, login, trafficCt);
                    ProxyOperationDto receipt;
                    do
                    {
                        receipt = await ReadOperationAsync(client, operation, trafficCt);
                        if (receipt.Stage == "batch_interrupted") break;
                        await Task.Delay(30, trafficCt);
                    } while (true);
                    var content = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(
                        SubscriptionRoute(ProxyApiRoutes.SubscriptionContentPattern, subscription.Id), RelaxKonOSJsonOptions.Default, trafficCt);
                    var state = await store.ReadAsync(trafficCt);
                    var steps = await ReadBatchAsync(operation, trafficCt);
                    if (receipt.State != ProxyOperationState.Interrupted || receipt.CompletedAt is null
                        || await SubmitAsync(client, ProxyApiRoutes.SubscriptionsRefresh, key, trafficCt) != operation
                        || await ReadOperationAsync(client, operation, trafficCt) != receipt
                        || steps[1].OperationId != child || !steps.Select(step => step.State).SequenceEqual([ProxyOperationStore.RefreshStepState.Succeeded,
                            ProxyOperationStore.RefreshStepState.Succeeded, ProxyOperationStore.RefreshStepState.Skipped])
                        || state.DirectReload is not null || state.SubscriptionRefreshCompletions?.GetValueOrDefault(child)?.Confirmed != true
                        || content?.Content.Contains("origin-2.example.test", StringComparison.Ordinal) != true
                        || content.RetrievedAt != checkpoint.Profile!.Refresh!.RetrievedAt
                        || await ReadRevisionAsync(subscription.ProfileId, trafficCt) != revision + 1
                        || Volatile.Read(ref feeds) != 2 || Volatile.Read(ref draftFeeds) != 2 || Volatile.Read(ref skippedFeeds) != 1
                        || Volatile.Read(ref reloads) != 2 || Volatile.Read(ref reloadAttempts) != 2
                        || guard.RuntimePid != pid)
                        throw new InvalidOperationException("Restart did not commit the original direct subscription without replay.");
                    Console.WriteLine($"PASS AUTHENTICATED SUBSCRIPTION BATCH RECOVERY: original {operation}, child {child}; draft and active committed, final item skipped without replay");

                    acknowledgedYaml = await File.ReadAllBytesAsync(activePath, trafficCt);
                    unknown = await SubmitAsync(client, ProxyApiRoutes.SubscriptionsRefresh, unknownKey, trafficCt);
                    await unacknowledged.Task.WaitAsync(trafficCt);
                    unknownCheckpoint = (await store.ReadAsync(trafficCt)).DirectReload
                        ?? throw new InvalidOperationException("Missing unacknowledged direct refresh checkpoint.");
                    var unknownSteps = await ReadBatchAsync(unknown, trafficCt);
                    unknownChild = unknownSteps[1].OperationId;
                    if (unknownCheckpoint.OperationId != unknownChild || unknownCheckpoint.ProfileReloadConfirmed
                        || unknownCheckpoint.Profile?.Refresh?.SubscriptionId != subscription.Id
                        || (await ReadOperationAsync(client, unknown, trafficCt)).State != ProxyOperationState.Running
                        || await ReadLeasesAsync(client, trafficCt) != 1
                        || await ReadRevisionAsync(subscription.ProfileId, trafficCt) != revision + 1
                        || Volatile.Read(ref feeds) != 3 || Volatile.Read(ref draftFeeds) != 3 || Volatile.Read(ref skippedFeeds) != 1
                        || Volatile.Read(ref reloads) != 3 || Volatile.Read(ref reloadAttempts) != 3)
                        throw new InvalidOperationException("The refresh lost its original work or committed without the reload response.");
                    restarted.Kill();
                    await restarted.WaitForExitAsync(trafficCt);
                    if (restarted.ExitCode == 0) throw new InvalidOperationException("The unacknowledged Server did not crash.");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(restarted); }

                using var recovery = ProxyAuthenticatedPublicationChecks.Start(start, Console.WriteLine);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(recovery, login, trafficCt);
                    while (!(await File.ReadAllBytesAsync(activePath, trafficCt)).SequenceEqual(acknowledgedYaml))
                        await Task.Delay(25, trafficCt);
                    var pending = await ReadOperationAsync(client, unknown, trafficCt);
                    var durableLedger = Path.Combine(directory, "proxy-operations.json");
                    var persisted = await File.ReadAllTextAsync(durableLedger, trafficCt);
                    using var ledger = JsonDocument.Parse(persisted);
                    var saved = ledger.RootElement.EnumerateArray().Select(entry => entry.GetProperty("Operation"))
                        .Single(entry => entry.GetProperty(nameof(ProxyOperationDto.OperationId)).GetGuid() == unknown);
                    using var fresh = new HttpRequestMessage(HttpMethod.Post, ProxyApiRoutes.SubscriptionsRefresh);
                    fresh.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                    using var denied = await client.SendAsync(fresh, trafficCt);
                    var status = await client.GetFromJsonAsync<RuntimeRecoverySnapshot>(RuntimeRoutes.MihomoRecovery, RelaxKonOSJsonOptions.Default, trafficCt);
                    var state = await store.ReadAsync(trafficCt);
                    var content = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(
                        SubscriptionRoute(ProxyApiRoutes.SubscriptionContentPattern, subscription.Id), RelaxKonOSJsonOptions.Default, trafficCt);
                    var steps = await ReadBatchAsync(unknown, trafficCt);
                    if (pending.State != ProxyOperationState.Interrupted || pending.Stage != "batch_pending"
                        || pending.CompletedAt is not null || pending.ProblemCode != ProxyProblemCodes.OperationInterrupted
                        || saved.GetProperty(nameof(ProxyOperationDto.Stage)).GetString() != pending.Stage
                        || await SubmitAsync(client, ProxyApiRoutes.SubscriptionsRefresh, unknownKey, trafficCt) != unknown
                        || await ReadOperationAsync(client, unknown, trafficCt) != pending
                        || await File.ReadAllTextAsync(durableLedger, trafficCt) != persisted
                        || denied.StatusCode != HttpStatusCode.BadRequest
                        || !(await denied.Content.ReadAsStringAsync(trafficCt)).Contains(ProxyProblemCodes.OperationInterrupted, StringComparison.Ordinal)
                        || status is not { Required: true } || status.OperationId != unknownChild
                        || state.DirectReload != unknownCheckpoint || state.SubscriptionRefreshCompletions?.ContainsKey(unknownChild) == true
                        || steps[1].OperationId != unknownChild || !steps.Select(step => step.State).SequenceEqual([ProxyOperationStore.RefreshStepState.Succeeded,
                            ProxyOperationStore.RefreshStepState.Running, ProxyOperationStore.RefreshStepState.Skipped])
                        || content?.RetrievedAt != checkpoint.Profile!.Refresh!.RetrievedAt
                        || await ReadRevisionAsync(subscription.ProfileId, trafficCt) != revision + 1
                        || await ReadLeasesAsync(client, trafficCt) != 0
                        || Volatile.Read(ref feeds) != 3 || Volatile.Read(ref draftFeeds) != 3 || Volatile.Read(ref skippedFeeds) != 1
                        || Volatile.Read(ref reloads) != 3 || Volatile.Read(ref reloadAttempts) != 3
                        || guard.RuntimePid != pid)
                        throw new InvalidOperationException("Restart lost the pending direct subscription receipt, replayed work, or committed an unknown result.");
                    using var readNative = new HttpRequestMessage(HttpMethod.Get, "proxies/fixture-3");
                    readNative.Headers.Authorization = new("Bearer", await secrets.GetOrCreateAsync(trafficCt));
                    using var liveCandidate = await native.SendAsync(readNative, trafficCt);
                    liveCandidate.EnsureSuccessStatusCode();
                    Console.WriteLine($"PASS AUTHENTICATED SUBSCRIPTION BATCH UNKNOWN: original {unknown}, child {unknownChild}; restored disk, stable pending receipt, no replay");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(recovery); }
            }, TimeSpan.FromSeconds(55));
        }
        finally
        {
            release.TrySetResult();
            await File.WriteAllBytesAsync(settingsPath, originalSettings);
            File.Delete(runtimePath);
            await File.WriteAllBytesAsync(activePath, originalYaml);
            using var controller = new MihomoControllerClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }), secrets, new());
            if (await controller.ReloadAsync(default) is not null)
                throw new InvalidOperationException("Could not restore the private direct proxy configuration.");
        }

        async Task VerifyDraftCommitWindowAsync(CancellationToken token)
        {
            var draftDatabase = Path.Combine(start.WorkingDirectory, "draft-window.db");
            var repository = new SqliteProxySubscriptionRepository(
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = start.WorkingDirectory },
                Microsoft.Extensions.Options.Options.Create(new RelaxKonOS.Server.Storage.StorageOptions { DatabasePath = draftDatabase }),
                ManagedServiceChecks.OpenProtection(root));
            start.Environment["Storage__DatabasePath"] = draftDatabase;
            var key = Guid.NewGuid().ToString("N");
            Guid operation;
            ProxySubscriptionDto subscription;
            ProxyDraftRefreshIntent intent;
            string draftPath;
            byte[] replaced;
            DateTime written;
            try
            {
                using (var server = ProxyAuthenticatedPublicationChecks.Start(start, Console.WriteLine))
                {
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, token);
                        subscription = await ImportAsync(client, "/draft-window", "draft-window", token);
                        await SqlAsync("""
                            CREATE TRIGGER reject_draft_receipt BEFORE UPDATE OF last_draft_refresh_operation_id ON proxy_subscriptions
                            WHEN NEW.last_draft_refresh_operation_id IS NOT NULL
                            BEGIN SELECT RAISE(ABORT, 'private draft receipt fault'); END;
                            """);
                        operation = await SubmitAsync(client, ProxyApiRoutes.SubscriptionsRefresh, key, token);
                        var pending = await WaitForTerminalAsync(client, operation, token);
                        var child = (await ReadBatchAsync(operation, token)).Single().OperationId;
                        intent = await repository.ReadDraftRefreshIntentAsync(child, token)
                            ?? throw new InvalidOperationException("Missing pre-replacement draft intent.");
                        draftPath = ProxyDraftRefreshRecovery.ProfilePath(paths, subscription.ProfileId);
                        replaced = await File.ReadAllBytesAsync(draftPath, token);
                        written = File.GetLastWriteTimeUtc(draftPath);
                        if (pending.Stage != "batch_pending" || pending.CompletedAt is not null
                            || intent.SubscriptionId != subscription.Id || intent.ProfileId != subscription.ProfileId
                            || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(replaced)) != intent.Identity
                            || !System.Text.Encoding.UTF8.GetString(replaced).Contains("window-2.example.test", StringComparison.Ordinal)
                            || await repository.ReadDraftRefreshReceiptAsync(child, subscription.Id, token) is not null)
                            throw new InvalidOperationException("The draft replacement or blocked SQLite receipt was not observed.");
                        server.Kill();
                        await server.WaitForExitAsync(token);
                        if (server.ExitCode == 0) throw new InvalidOperationException("The draft Server did not crash.");
                    }
                    finally { await ProxyAuthenticatedPublicationChecks.StopAsync(server); }
                }

                await SqlAsync("DROP TRIGGER reject_draft_receipt;");
                using var restarted = ProxyAuthenticatedPublicationChecks.Start(start, Console.WriteLine);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(restarted, login, token);
                    var terminal = await ReadOperationAsync(client, operation, token);
                    var content = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(
                        SubscriptionRoute(ProxyApiRoutes.SubscriptionContentPattern, subscription.Id), RelaxKonOSJsonOptions.Default, token);
                    if (terminal.State != ProxyOperationState.Interrupted || terminal.Stage != "batch_interrupted"
                        || await SubmitAsync(client, ProxyApiRoutes.SubscriptionsRefresh, key, token) != operation
                        || await ReadOperationAsync(client, operation, token) != terminal
                        || (await ReadBatchAsync(operation, token)).Single().State != ProxyOperationStore.RefreshStepState.Succeeded
                        || await repository.ReadDraftRefreshReceiptAsync(intent.OperationId, subscription.Id, token) != intent.RetrievedAt
                        || await repository.ReadDraftRefreshIntentAsync(intent.OperationId, token) is not null
                        || content?.RetrievedAt != intent.RetrievedAt
                        || !content.Content.Contains("window-2.example.test", StringComparison.Ordinal)
                        || content.Content.Contains(intent.OperationId.ToString("N"), StringComparison.Ordinal)
                        || !(await File.ReadAllBytesAsync(draftPath, token)).SequenceEqual(replaced)
                        || File.GetLastWriteTimeUtc(draftPath) != written || Volatile.Read(ref windowFeeds) != 2
                        || Volatile.Read(ref reloadAttempts) != 0)
                        throw new InvalidOperationException("Draft recovery changed the original bytes, fetched again, or missed the original receipt.");
                    Console.WriteLine($"PASS AUTHENTICATED DRAFT COMMIT RECOVERY: batch {operation}, original child {intent.OperationId}; same YAML and one refresh download");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(restarted); }
            }
            finally { start.Environment["Storage__DatabasePath"] = database; }

            async Task SqlAsync(string sql)
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = draftDatabase }.ToString());
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }
        }

        async Task<long> ReadRevisionAsync(Guid profile, CancellationToken token)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT revision FROM proxy_profiles WHERE profile_id=$id AND is_active=1;";
            command.Parameters.AddWithValue("$id", profile.ToString("D"));
            return (long)(await command.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("The original subscription profile is no longer active."));
        }

        async Task<ProxySubscriptionDto> ImportAsync(HttpClient client, string path, string name, CancellationToken token)
        {
            using var imported = await client.PostAsJsonAsync(ProxyApiRoutes.Subscriptions,
                new ImportProxySubscriptionRequest(Address(origin) + path, name), RelaxKonOSJsonOptions.Default, token);
            if (imported.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException($"Direct subscription import returned HTTP {(int)imported.StatusCode}: {await imported.Content.ReadAsStringAsync(token)}");
            return (await imported.Content.ReadFromJsonAsync<ProxySubscriptionDto>(RelaxKonOSJsonOptions.Default, token))!;
        }

        async Task<ProxyOperationStore.RefreshStep[]> ReadBatchAsync(Guid batchId, CancellationToken token)
        {
            using var ledger = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "proxy-operations.json"), token));
            return ledger.RootElement.EnumerateArray().Single(entry => entry.GetProperty("Operation")
                .GetProperty(nameof(ProxyOperationDto.OperationId)).GetGuid() == batchId)
                .GetProperty("Batch").Deserialize<ProxyOperationStore.RefreshStep[]>()!;
        }
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static string SubscriptionRoute(string pattern, Guid id) =>
        ProxyApiRoutes.Proxy + pattern.Replace("{subscriptionId:guid}", id.ToString("D"), StringComparison.Ordinal);

    private static async Task<int> ReadLeasesAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ServerModuleDto>(
            ServerModuleManagementRoutes.Detail.Replace("{moduleId}", "relaxkonos.proxy", StringComparison.Ordinal),
            RelaxKonOSJsonOptions.Default, ct))?.Residency?.ActiveLeases
        ?? throw new InvalidOperationException("Missing Proxy work lease count.");

    private static async Task<Guid> SubmitAsync(HttpClient client, string route, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProxyOperationAcceptedDto>(RelaxKonOSJsonOptions.Default, ct))!.OperationId;
    }

    private static async Task<ProxyOperationDto> ReadOperationAsync(HttpClient client, Guid id, CancellationToken ct) =>
        await client.GetFromJsonAsync<ProxyOperationDto>(ProxyApiRoutes.Proxy +
            ProxyApiRoutes.OperationsPattern.Replace("{operationId:guid}", id.ToString("D"), StringComparison.Ordinal),
            RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidOperationException("Missing original direct subscription operation.");

    private static async Task<ProxyOperationDto> WaitForTerminalAsync(HttpClient client, Guid id, CancellationToken ct)
    {
        while (true)
        {
            var operation = await ReadOperationAsync(client, id, ct);
            if (operation.State is not (ProxyOperationState.Running or ProxyOperationState.Queued)) return operation;
            await Task.Delay(25, ct);
        }
    }
}
