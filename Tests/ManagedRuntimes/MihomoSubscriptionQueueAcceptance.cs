using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoSubscriptionQueueAcceptance
{
    internal static async Task RunAsync(ProcessStartInfo start, IProxyPlatformPaths paths, int port,
        MihomoPublicationStore publication, MihomoRuntimeLifecycle lifecycle, Func<int, Task<bool>> probe,
        Func<CancellationToken, Task> verifyNative)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var ct = budget.Token;
        var login = ProxyAuthenticatedPublicationChecks.ReadLogin();
        var settingsFile = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
        var original = File.Exists(settingsFile) ? await File.ReadAllBytesAsync(settingsFile, ct) : null;
        var settings = await new ProxySettingsReader(paths).GetAsync(ct);
        var owner = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID") + ":"
            + Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID");
        await File.WriteAllTextAsync(settingsFile, JsonSerializer.Serialize(settings with { AllowInsecureSubscriptionSources = true, MixedPort = port }), ct);
        try
        {
            await NativeRuntimeProcess.RunAsync("/usr/bin/chown", [owner, "--", settingsFile], 256);
            await VerifyAsync();
        }
        finally
        {
            if (original is null) File.Delete(settingsFile);
            else await File.WriteAllBytesAsync(settingsFile, original);
        }

        async Task VerifyAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://93.184.216.34:0");
            await using var origin = builder.Build();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            origin.MapGet("/feed", async (HttpContext context) =>
            {
                var request = Interlocked.Increment(ref requests);
                await context.Response.WriteAsync("vless://550e8400-e29b-41d4-a716-446655440000@", context.RequestAborted);
                if (request == 2)
                {
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await release.Task.WaitAsync(context.RequestAborted);
                }
                await context.Response.WriteAsync($"origin-{request}.example.test:443#fixture", context.RequestAborted);
            });
            await origin.StartAsync(ct);
            var endpoint = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var events = Channel.CreateUnbounded<string>();
            start.Environment["Modules__Proxy__Enabled"] = "true";
            start.Environment["Identity__LinuxPamTransport"] = "helper";
            start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location;
            start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = Guid.NewGuid().ToString("N");
            string refreshRoute;
            string contentRoute;
            Guid subscriptionId;
            Guid operation;
            ProxyOperationDto terminal;
            ProxySubscriptionContentDto updated;
            using (var server = ProxyAuthenticatedPublicationChecks.Start(start, OnError, line =>
            {
                if (line.Contains("Application is shutting down", StringComparison.Ordinal)) stopping.TrySetResult();
            }))
            {
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                    using var imported = await client.PostAsJsonAsync(ProxyApiRoutes.Subscriptions,
                        new ImportProxySubscriptionRequest(endpoint + "/feed", "private-subscription-fixture"), RelaxKonOSJsonOptions.Default, ct);
                    if (imported.StatusCode != HttpStatusCode.Created)
                        throw new InvalidOperationException($"Managed subscription import returned HTTP {(int)imported.StatusCode}.");
                    var subscription = await imported.Content.ReadFromJsonAsync<ProxySubscriptionDto>(RelaxKonOSJsonOptions.Default, ct);
                    if (subscription is null || subscription.IsActive)
                        throw new InvalidOperationException("The imported subscription was not stored as an inactive draft.");
                    subscriptionId = subscription.Id;
                    var first = await events.Reader.ReadAsync(ct);
                    await VerifyUnloadedAsync(first);
                    refreshRoute = SubscriptionRoute(ProxyApiRoutes.SubscriptionRefreshPattern, subscription.Id);
                    contentRoute = SubscriptionRoute(ProxyApiRoutes.SubscriptionContentPattern, subscription.Id);
                    var initial = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct);
                    if (initial?.Content.Contains("origin-1.example.test", StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("The original converted draft was not stored.");

                    operation = await SubmitAsync(client, refreshRoute, key);
                    var second = await events.Reader.ReadAsync(ct);
                    if (second == first || !second.StartsWith("load ", StringComparison.Ordinal)
                        || (await ReadOperationAsync(client, operation)).State != ProxyOperationState.Running
                        || await ReadLeasesAsync(client) != 1
                        || events.Reader.TryPeek(out _) || Volatile.Read(ref requests) != 2)
                        throw new InvalidOperationException("The original queued refresh did not own a fresh generation during the real body read.");
                    await verifyNative(ct);
                    release.TrySetResult();
                    await VerifyUnloadedAsync(second);
                    do
                    {
                        terminal = await ReadOperationAsync(client, operation);
                        if (terminal.State is not (ProxyOperationState.Queued or ProxyOperationState.Running)) break;
                        await Task.Delay(25, ct);
                    } while (true);
                    updated = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct)
                        ?? throw new InvalidOperationException("The refreshed draft was unavailable.");
                    if (terminal.State != ProxyOperationState.Succeeded || updated.SubscriptionId != subscription.Id
                        || !updated.Content.Contains("origin-2.example.test", StringComparison.Ordinal))
                        throw new InvalidOperationException($"The original refresh did not persist its converted draft: {terminal.ProblemCode}.");
                    while (await ReadLeasesAsync(client) != 0) await Task.Delay(25, ct);
                    await ProxyAuthenticatedPublicationChecks.SignalShutdownAsync(server, client, stopping.Task, ct);
                    await server.WaitForExitAsync(ct);
                    if (server.ExitCode != 0) throw new InvalidOperationException("The subscription Server did not exit cleanly.");
                }
                finally
                {
                    release.TrySetResult();
                    await ProxyAuthenticatedPublicationChecks.StopAsync(server);
                }
            }

            using var restarted = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
            try
            {
                using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(restarted, login, ct);
                if (await SubmitAsync(client, refreshRoute, key) != operation || await ReadOperationAsync(client, operation) != terminal
                    || await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct) != updated
                    || Volatile.Read(ref requests) != 2 || events.Reader.TryPeek(out _))
                    throw new InvalidOperationException("Restart replayed a download or changed the original receipt or profile.");
                await verifyNative(ct);
                Console.WriteLine($"PASS MANAGED MIHOMO SUBSCRIPTION QUEUE: original refresh {operation}, fresh execution generation, durable draft and no replay");
            }
            finally { await ProxyAuthenticatedPublicationChecks.StopAsync(restarted); }
            await VerifyActiveRefreshAsync();

            async Task VerifyActiveRefreshAsync()
            {
                var socket = start.Environment["PrivilegedHelper__SocketPath"]!;
                using (var activation = ProxyAuthenticatedPublicationChecks.Start(start))
                {
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(activation, login, ct);
                        var id = await SubmitAsync(client, SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, subscriptionId),
                            Guid.NewGuid().ToString("N"));
                        if ((await WaitForTerminalAsync(client, id)).State != ProxyOperationState.Succeeded || !await probe(port))
                            throw new InvalidOperationException("The real managed subscription could not be activated.");
                    }
                    finally { await ProxyAuthenticatedPublicationChecks.StopAsync(activation); }
                }
                var before = await lifecycle.QueryAsync(ct);
                var oldPublication = await publication.ReadAsync(ct);
                var activeKey = Guid.NewGuid().ToString("N");
                Guid refreshId;
                Guid nativeId;
                RuntimeInstallationSnapshot committed;
                await using (var relay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-subscription-refresh.sock",
                    socket, ct, static (kind, _) => kind == "MihomoRuntimeConfigure"))
                {
                    start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
                    using var active = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(active, login, ct);
                        refreshId = await SubmitAsync(client, refreshRoute, activeKey);
                        nativeId = await relay.Committed.Task.WaitAsync(ct);
                        committed = await lifecycle.QueryAsync(ct, nativeId);
                        var pending = await publication.ReadAsync(ct);
                        if (committed.Receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded } || committed.Receipt.OperationId != nativeId)
                            throw new InvalidOperationException($"The Helper did not commit the active refresh: {committed.Receipt?.Outcome}/{committed.Receipt?.ProblemCode}.");
                        if (nativeId == Guid.Empty || pending.Pending?.OperationId != nativeId
                            || committed.ConfigurationIdentity != pending.Pending.Identity
                            || committed.ConfigurationIdentity == oldPublication.Identity
                            || committed.Installation.StateRevision <= before.Installation.StateRevision
                            || (await ReadOperationAsync(client, refreshId)).State != ProxyOperationState.Running
                            || Volatile.Read(ref requests) != 3 || !await probe(port))
                            throw new InvalidOperationException("The active refresh did not reach the original native commit with a pending queue receipt.");
                        active.Kill();
                        await active.WaitForExitAsync(ct);
                        if (active.ExitCode == 0) throw new InvalidOperationException("The active refresh host did not terminate at the reply boundary.");
                    }
                    finally { await ProxyAuthenticatedPublicationChecks.StopAsync(active); }
                }
                start.Environment["PrivilegedHelper__SocketPath"] = socket;
                using var recovery = ProxyAuthenticatedPublicationChecks.Start(start);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(recovery, login, ct);
                    MihomoPublicationState recovered;
                    do
                    {
                        recovered = await publication.ReadAsync(ct);
                        if (recovered.Pending is null) break;
                        await Task.Delay(50, ct);
                    } while (true);
                    var original = await ReadOperationAsync(client, refreshId);
                    var observed = await lifecycle.QueryAsync(ct, nativeId);
                    var draft = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct);
                    if (recovered.Identity != committed.ConfigurationIdentity || recovered.LastConfiguration?.OperationId != nativeId
                        || recovered.LastConfiguration.Confirmed != true || observed.Receipt != committed.Receipt
                        || observed.Installation.StateRevision != committed.Installation.StateRevision
                        || original.State != ProxyOperationState.Interrupted
                        || await SubmitAsync(client, refreshRoute, activeKey) != refreshId
                        || draft?.Content.Contains("origin-3.example.test", StringComparison.Ordinal) != true
                        || Volatile.Read(ref requests) != 3 || !await probe(port))
                        throw new InvalidOperationException("Restart did not reconcile the original publication without replay or preserve the unknown queue result.");
                    using var newRequest = new HttpRequestMessage(HttpMethod.Post, refreshRoute);
                    newRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                    using var denied = await client.SendAsync(newRequest, ct);
                    if (denied.StatusCode != HttpStatusCode.BadRequest
                        || !(await denied.Content.ReadAsStringAsync(ct)).Contains(ProxyProblemCodes.OperationInterrupted, StringComparison.Ordinal)
                        || Volatile.Read(ref requests) != 3)
                        throw new InvalidOperationException("Unresolved original queue ownership did not block new work.");
                    Console.WriteLine($"PASS MANAGED MIHOMO ACTIVE SUBSCRIPTION CRASH: original queue {refreshId} interrupted, native receipt {nativeId} reconciled, no replay");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(recovery); }
            }

            void OnError(string line)
            {
                if (line.StartsWith(StartupHook.SubscriptionEventPrefix, StringComparison.Ordinal))
                    events.Writer.TryWrite(line[StartupHook.SubscriptionEventPrefix.Length..]);
            }

            async Task VerifyUnloadedAsync(string loaded)
            {
                if (!loaded.StartsWith("load ", StringComparison.Ordinal)
                    || await events.Reader.ReadAsync(ct) != $"unload {loaded[5..]}")
                    throw new InvalidOperationException("The original subscription body generation did not unload.");
            }

            async Task<Guid> SubmitAsync(HttpClient client, string route, string idempotencyKey)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route);
                request.Headers.Add("Idempotency-Key", idempotencyKey);
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.Accepted)
                    throw new InvalidOperationException($"Subscription refresh returned HTTP {(int)response.StatusCode}.");
                return (await response.Content.ReadFromJsonAsync<ProxyOperationAcceptedDto>(RelaxKonOSJsonOptions.Default, ct))!.OperationId;
            }

            async Task<ProxyOperationDto> ReadOperationAsync(HttpClient client, Guid id) =>
                await client.GetFromJsonAsync<ProxyOperationDto>(ProxyApiRoutes.Proxy
                    + ProxyApiRoutes.OperationsPattern.Replace("{operationId:guid}", id.ToString("D"), StringComparison.Ordinal),
                    RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidOperationException("Missing original subscription operation.");

            async Task<ProxyOperationDto> WaitForTerminalAsync(HttpClient client, Guid id)
            {
                while (true)
                {
                    var operation = await ReadOperationAsync(client, id);
                    if (operation.State is not (ProxyOperationState.Queued or ProxyOperationState.Running)) return operation;
                    await Task.Delay(25, ct);
                }
            }

            async Task<int> ReadLeasesAsync(HttpClient client) =>
                (await client.GetFromJsonAsync<ServerModuleDto>(
                    ServerModuleManagementRoutes.Detail.Replace("{moduleId}", "relaxkonos.proxy", StringComparison.Ordinal),
                    RelaxKonOSJsonOptions.Default, ct))?.Residency?.ActiveLeases
                    ?? throw new InvalidOperationException("Missing Proxy work lease count.");
        }
    }

    private static string SubscriptionRoute(string pattern, Guid id) =>
        ProxyApiRoutes.Proxy + pattern.Replace("{subscriptionId:guid}", id.ToString("D"), StringComparison.Ordinal);
}
