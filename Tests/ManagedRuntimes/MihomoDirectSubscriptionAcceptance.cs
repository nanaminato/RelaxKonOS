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
        var originBuilder = WebApplication.CreateBuilder();
        originBuilder.Logging.ClearProviders();
        originBuilder.WebHost.UseUrls("http://93.184.216.34:0");
        await using var origin = originBuilder.Build();
        origin.MapGet("/feed", () => $"vless://550e8400-e29b-41d4-a716-446655440000@origin-{Interlocked.Increment(ref feeds)}.example.test:443#fixture");
        await origin.StartAsync(ct);

        using var native = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new("http://127.0.0.1:9090/") };
        var relayBuilder = WebApplication.CreateBuilder();
        relayBuilder.Logging.ClearProviders();
        relayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var relay = relayBuilder.Build();
        var reloadAttempts = 0;
        var reloads = 0;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (reload && response.IsSuccessStatusCode && Interlocked.Increment(ref reloads) == 2)
            {
                committed.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await relay.StartAsync(ct);

        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Proxy__Mihomo__Controller__Endpoint"] = Address(relay) + "/";
        start.Environment["Proxy__Mihomo__Controller__TimeoutSeconds"] = "30";
        var database = Path.Combine(start.WorkingDirectory, "direct-subscription.db");
        start.Environment["Storage__DatabasePath"] = database;
        try
        {
            await guard.VerifyTrafficAsync(async (_, trafficCt) =>
            {
                var pid = guard.RuntimePid;
                var key = Guid.NewGuid().ToString("N");
                string route;
                Guid operation;
                ProxySubscriptionDto subscription;
                MihomoDirectReload checkpoint;
                long revision;
                using (var server = ProxyAuthenticatedPublicationChecks.Start(start, Console.WriteLine))
                {
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, trafficCt);
                        using var imported = await client.PostAsJsonAsync(ProxyApiRoutes.Subscriptions,
                            new ImportProxySubscriptionRequest(Address(origin) + "/feed", "direct-subscription-fixture"), RelaxKonOSJsonOptions.Default, trafficCt);
                        if (imported.StatusCode != HttpStatusCode.Created)
                            throw new InvalidOperationException($"Direct subscription import returned HTTP {(int)imported.StatusCode}: {await imported.Content.ReadAsStringAsync(trafficCt)}");
                        subscription = (await imported.Content.ReadFromJsonAsync<ProxySubscriptionDto>(RelaxKonOSJsonOptions.Default, trafficCt))!;
                        var activation = await SubmitAsync(client, SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, subscription.Id),
                            Guid.NewGuid().ToString("N"), trafficCt);
                        var activated = await WaitForTerminalAsync(client, activation, trafficCt);
                        if (activated.State != ProxyOperationState.Succeeded)
                            throw new InvalidOperationException($"The direct subscription could not be activated: {activated.ProblemCode}.");
                        revision = await ReadRevisionAsync(subscription.ProfileId, trafficCt);
                        route = SubscriptionRoute(ProxyApiRoutes.SubscriptionRefreshPattern, subscription.Id);
                        operation = await SubmitAsync(client, route, key, trafficCt);
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
                        var leaseCount = (await client.GetFromJsonAsync<ServerModuleDto>(
                            ServerModuleManagementRoutes.Detail.Replace("{moduleId}", "relaxkonos.proxy", StringComparison.Ordinal),
                            RelaxKonOSJsonOptions.Default, trafficCt))?.Residency?.ActiveLeases;
                        if (checkpoint.OperationId != operation || checkpoint.Profile?.Refresh?.SubscriptionId != subscription.Id
                            || (await ReadOperationAsync(client, operation, trafficCt)).State != ProxyOperationState.Running
                            || checkpoint.Profile.ExpectedRevision != revision || leaseCount != 1
                            || Volatile.Read(ref feeds) != 2 || Volatile.Read(ref reloads) != 2 || Volatile.Read(ref reloadAttempts) != 2)
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
                        if (receipt.State != ProxyOperationState.Interrupted) break;
                        await Task.Delay(30, trafficCt);
                    } while (true);
                    var content = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(
                        SubscriptionRoute(ProxyApiRoutes.SubscriptionContentPattern, subscription.Id), RelaxKonOSJsonOptions.Default, trafficCt);
                    var state = await store.ReadAsync(trafficCt);
                    if (receipt.State != ProxyOperationState.Succeeded || receipt.CompletedAt is null
                        || await SubmitAsync(client, route, key, trafficCt) != operation || await ReadOperationAsync(client, operation, trafficCt) != receipt
                        || state.DirectReload is not null || state.SubscriptionRefreshCompletions?.GetValueOrDefault(operation)?.Confirmed != true
                        || content?.Content.Contains("origin-2.example.test", StringComparison.Ordinal) != true
                        || content.RetrievedAt != checkpoint.Profile!.Refresh!.RetrievedAt
                        || await ReadRevisionAsync(subscription.ProfileId, trafficCt) != revision + 1
                        || Volatile.Read(ref feeds) != 2 || Volatile.Read(ref reloads) != 2 || Volatile.Read(ref reloadAttempts) != 2
                        || guard.RuntimePid != pid)
                        throw new InvalidOperationException("Restart did not commit the original direct subscription without replay.");
                    Console.WriteLine($"PASS AUTHENTICATED DIRECT SUBSCRIPTION RECOVERY: original {operation}; one refresh download, reload and SQLite commit");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(restarted); }
            }, TimeSpan.FromSeconds(55));
        }
        finally
        {
            release.TrySetResult();
            await File.WriteAllBytesAsync(settingsPath, originalSettings);
            File.Delete(runtimePath);
            await File.WriteAllBytesAsync(activePath, originalYaml);
            var secrets = new DataProtectionProxyControllerSecretStore(
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = start.WorkingDirectory },
                ManagedServiceChecks.OpenProtection(root));
            using var controller = new MihomoControllerClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }), secrets, new());
            if (await controller.ReloadAsync(default) is not null)
                throw new InvalidOperationException("Could not restore the private direct proxy configuration.");
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
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static string SubscriptionRoute(string pattern, Guid id) =>
        ProxyApiRoutes.Proxy + pattern.Replace("{subscriptionId:guid}", id.ToString("D"), StringComparison.Ordinal);

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
