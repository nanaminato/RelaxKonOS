using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoSubscriptionQueueAcceptance
{
    internal static async Task<Guid?> RunAsync(ProcessStartInfo start, IProxyPlatformPaths paths, int port,
        MihomoPublicationStore publication, MihomoRuntimeLifecycle lifecycle, LoginRequest login, Func<int, Task<bool>> probe,
        Func<CancellationToken, Task> verifyNative, Func<Func<Task>, Task>? restartHelper = null)
    {
        var postSwitch = restartHelper is not null || Environment.GetEnvironmentVariable("RELAXKONOS_TEST_MIHOMO_POST_SWITCH") == "1";
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(postSwitch ? 200 : 145));
        var ct = budget.Token;
        var settingsFile = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
        var original = File.Exists(settingsFile) ? await File.ReadAllBytesAsync(settingsFile, ct) : null;
        var settings = await new ProxySettingsReader(paths).GetAsync(ct);
        var owner = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID") + ":"
            + Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID");
        await File.WriteAllTextAsync(settingsFile, JsonSerializer.Serialize(settings with { AllowInsecureSubscriptionSources = true, MixedPort = port }), ct);
        try
        {
            await NativeRuntimeProcess.RunAsync("/usr/bin/chown", [owner, "--", settingsFile], 256);
            return await VerifyAsync();
        }
        finally
        {
            if (original is null) File.Delete(settingsFile);
            else await File.WriteAllBytesAsync(settingsFile, original);
        }

        async Task<Guid?> VerifyAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var closedConnections = Channel.CreateUnbounded<string>();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Parse("93.184.216.34"), 0, listener =>
                listener.Use(next => async connection =>
                {
                    try { await next(connection); }
                    finally { closedConnections.Writer.TryWrite(connection.ConnectionId); }
                })));
            await using var origin = builder.Build();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var importStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var importConnection = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshConnection = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            origin.MapGet("/feed", async (HttpContext context) =>
            {
                var request = Interlocked.Increment(ref requests);
                if (request == 5)
                {
                    await context.Response.WriteAsync("rules:\n  - UNSUPPORTED-RULE,DIRECT\n", context.RequestAborted);
                    return;
                }
                await context.Response.WriteAsync("vless://550e8400-e29b-41d4-a716-446655440000@", context.RequestAborted);
                if (request == 1)
                {
                    importConnection.SetResult(context.Connection.Id);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    importStarted.TrySetResult();
                    await releaseImport.Task.WaitAsync(context.RequestAborted);
                }
                if (request == 2)
                {
                    refreshConnection.SetResult(context.Connection.Id);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    bodyStarted.TrySetResult();
                    await release.Task.WaitAsync(context.RequestAborted);
                }
                await context.Response.WriteAsync($"origin-{request}.example.test:443#fixture", context.RequestAborted);
            });
            await origin.StartAsync(ct);
            var endpoint = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var events = Channel.CreateUnbounded<string>();
            var collections = Channel.CreateUnbounded<string>();
            start.Environment["Modules__Proxy__Enabled"] = "true";
            start.Environment["Identity__LinuxPamTransport"] = "helper";
            start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location;
            start.Environment["RELAXKONOS_TEST_COLLECT_SUBSCRIPTION"] = "1";
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
                    using var importer = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                    var importing = importer.PostAsJsonAsync(ProxyApiRoutes.Subscriptions,
                        new ImportProxySubscriptionRequest(endpoint + "/feed", "private-subscription-fixture"), RelaxKonOSJsonOptions.Default, ct);
                    try
                    {
                        await importStarted.Task.WaitAsync(ct);
                        if (importing.IsCompleted || await ReadLeasesAsync(client) != 1)
                            throw new InvalidOperationException("Foreground subscription import lost its management lease while the origin body was pending.");
                    }
                    finally { releaseImport.TrySetResult(); }
                    using var imported = await importing;
                    if (imported.StatusCode != HttpStatusCode.Created)
                        throw new InvalidOperationException($"Managed subscription import returned HTTP {(int)imported.StatusCode}.");
                    var subscription = await imported.Content.ReadFromJsonAsync<ProxySubscriptionDto>(RelaxKonOSJsonOptions.Default, ct);
                    if (subscription is null || subscription.IsActive)
                        throw new InvalidOperationException("The imported subscription was not stored as an inactive draft.");
                    subscriptionId = subscription.Id;
                    var first = await events.Reader.ReadAsync(ct);
                    await VerifyUnloadedAsync(first);
                    await VerifyCollectedAsync(first);
                    refreshRoute = SubscriptionRoute(ProxyApiRoutes.SubscriptionRefreshPattern, subscription.Id);
                    contentRoute = SubscriptionRoute(ProxyApiRoutes.SubscriptionContentPattern, subscription.Id);
                    var initial = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct);
                    if (initial?.Content.Contains("origin-1.example.test", StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("The original converted draft was not stored.");
                    var listed = await client.GetFromJsonAsync<ProxySubscriptionDto[]>(ProxyApiRoutes.Subscriptions, RelaxKonOSJsonOptions.Default, ct);
                    using var options = await client.GetAsync(ProxyApiRoutes.SubscriptionDownloadOptions, ct);
                    if (listed?.Any(item => item.Id == subscriptionId) != true || !options.IsSuccessStatusCode || Volatile.Read(ref requests) != 1)
                        throw new InvalidOperationException("Subscription reads did not return the original draft and download options without a second download.");
                    while (await ReadLeasesAsync(client) != 0) await Task.Delay(25, ct);
                    if (await closedConnections.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct)
                        != await importConnection.Task.WaitAsync(ct))
                        throw new InvalidOperationException("The foreground scope did not close its original origin connection.");
                    Console.WriteLine($"PASS MANAGED MIHOMO FOREGROUND SUBSCRIPTION: draft {subscriptionId}; import, original content, list and download options drained");

                    operation = await SubmitAsync(client, refreshRoute, key);
                    var second = await events.Reader.ReadAsync(ct);
                    await bodyStarted.Task.WaitAsync(ct);
                    if (second == first || !second.StartsWith("load ", StringComparison.Ordinal)
                        || (await ReadOperationAsync(client, operation)).State != ProxyOperationState.Running
                        || await ReadLeasesAsync(client) != 1
                        || events.Reader.TryPeek(out _) || Volatile.Read(ref requests) != 2)
                        throw new InvalidOperationException("The original queued refresh did not own a fresh generation during the real body read.");
                    await verifyNative(ct);
                    release.TrySetResult();
                    await VerifyUnloadedAsync(second);
                    terminal = await ReadOperationAsync(client, operation);
                    if (terminal.State != ProxyOperationState.Succeeded)
                        throw new InvalidOperationException("The subscription executor unloaded before the original terminal receipt.");
                    await VerifyCollectedAsync(second);
                    start.Environment.Remove("RELAXKONOS_TEST_COLLECT_SUBSCRIPTION");
                    updated = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct)
                        ?? throw new InvalidOperationException("The refreshed draft was unavailable.");
                    if (terminal.State != ProxyOperationState.Succeeded || updated.SubscriptionId != subscription.Id
                        || !updated.Content.Contains("origin-2.example.test", StringComparison.Ordinal))
                        throw new InvalidOperationException($"The original refresh did not persist its converted draft: {terminal.ProblemCode}.");
                    while (await ReadLeasesAsync(client) != 0) await Task.Delay(25, ct);
                    var refreshSocket = await refreshConnection.Task.WaitAsync(ct);
                    if (refreshSocket == await importConnection.Task.WaitAsync(ct)
                        || await closedConnections.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct) != refreshSocket)
                        throw new InvalidOperationException("The queued refresh did not close its own origin connection.");
                    Console.WriteLine($"PASS MANAGED MIHOMO SUBSCRIPTION CONNECTION OWNERS: draft {subscriptionId}; refresh {operation}; both origin connections closed before shutdown");
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
            await VerifyActiveRefreshResponseLossAsync();
            await VerifyFailedPublicationAsync(activation: false);
            await VerifyFailedPublicationAsync(activation: true);
            await VerifySettingsFirstAsync();
            return postSwitch && OperatingSystem.IsLinux() ? await VerifyPostSwitchCompensationAsync() : null;

            async Task VerifyActiveRefreshAsync()
            {
                var socket = start.Environment["PrivilegedHelper__SocketPath"]!;
                var activationRoute = SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, subscriptionId);
                var activationKey = Guid.NewGuid().ToString("N");
                Guid activationId;
                RuntimeInstallationSnapshot activationReceipt;
                var configureCalls = 0;
                await using (var activationRelay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-subscription-activation.sock",
                    socket, ct, (kind, _) =>
                    {
                        if (kind != "MihomoRuntimeConfigure") return false;
                        Interlocked.Increment(ref configureCalls);
                        return true;
                    }))
                {
                    start.Environment["PrivilegedHelper__SocketPath"] = activationRelay.Path;
                    start.Environment["RELAXKONOS_TEST_SETTINGS_CLEANUP"] = start.WorkingDirectory;
                    using var activation = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(activation, login, ct);
                        activationId = await SubmitAsync(client, activationRoute, activationKey);
                        var nativeActivation = await WaitForNativeCommitAsync(client, activationId, activationRelay);
                        activationReceipt = await lifecycle.QueryAsync(ct, nativeActivation);
                        var pending = (await publication.ReadAsync(ct)).Pending;
                        if (nativeActivation != activationId || pending?.OperationId != activationId
                            || pending.Profile?.ActivationSubscriptionId != subscriptionId || pending.Profile.Refresh is not null
                            || activationReceipt.Receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded }
                            || (await ReadOperationAsync(client, activationId)).State != ProxyOperationState.Running
                            || await ReadLeasesAsync(client) != 1 || Volatile.Read(ref requests) != 2 || !await probe(port))
                            throw new InvalidOperationException("Managed activation did not retain its original queue and native IDs while awaiting the reply.");
                        await CancelCompetingSettingsAsync(client, activationId);
                        activation.Kill();
                        await activation.WaitForExitAsync(ct);
                        if (activation.ExitCode == 0) throw new InvalidOperationException("The managed activation Server did not crash.");
                    }
                    finally
                    {
                        start.Environment.Remove("RELAXKONOS_TEST_SETTINGS_CLEANUP");
                        await ProxyAuthenticatedPublicationChecks.StopAsync(activation);
                    }
                }
                start.Environment["PrivilegedHelper__SocketPath"] = socket;
                using (var activationRecovery = ProxyAuthenticatedPublicationChecks.Start(start, OnError))
                {
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(activationRecovery, login, ct);
                        while ((await publication.ReadAsync(ct)).Pending is not null) await Task.Delay(30, ct);
                        var original = await ReadOperationAsync(client, activationId);
                        var observed = await lifecycle.QueryAsync(ct, activationId);
                        var selected = await ReadSubscriptionAsync(client);
                        if (original.State != ProxyOperationState.Succeeded || original.CompletedAt is null
                            || await SubmitAsync(client, activationRoute, activationKey) != activationId
                            || await ReadOperationAsync(client, activationId) != original
                            || observed.Receipt != activationReceipt.Receipt
                            || observed.Installation.StateRevision != activationReceipt.Installation.StateRevision
                            || !selected.IsActive || selected.LastUpdatedAt != updated.RetrievedAt
                            || Volatile.Read(ref requests) != 2 || Volatile.Read(ref configureCalls) != 1 || !await probe(port))
                            throw new InvalidOperationException("Managed activation recovery did not confirm the original receipt or replayed work.");
                        Console.WriteLine($"PASS MANAGED MIHOMO SUBSCRIPTION ACTIVATION CRASH: original {activationId}; native commit once and original receipt recovered");
                    }
                    finally { await ProxyAuthenticatedPublicationChecks.StopAsync(activationRecovery); }
                }
                var before = await lifecycle.QueryAsync(ct);
                var oldPublication = await publication.ReadAsync(ct);
                var activeKey = Guid.NewGuid().ToString("N");
                Guid refreshId;
                Guid nativeId;
                DateTimeOffset refreshTime;
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
                        nativeId = await WaitForNativeCommitAsync(client, refreshId, relay);
                        committed = await lifecycle.QueryAsync(ct, nativeId);
                        var pending = await publication.ReadAsync(ct);
                        if (committed.Receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded } || committed.Receipt.OperationId != nativeId)
                            throw new InvalidOperationException($"The Helper did not commit the active refresh: {committed.Receipt?.Outcome}/{committed.Receipt?.ProblemCode}.");
                        if (nativeId != refreshId || pending.Pending?.OperationId != nativeId
                            || pending.Pending.Profile?.Refresh is not { } refresh || refresh.SubscriptionId != subscriptionId
                            || committed.ConfigurationIdentity != pending.Pending.Identity
                            || committed.ConfigurationIdentity == oldPublication.Identity
                            || committed.Installation.StateRevision <= before.Installation.StateRevision
                            || (await ReadOperationAsync(client, refreshId)).State != ProxyOperationState.Running
                            || (await ReadSubscriptionAsync(client)).LastUpdatedAt != updated.RetrievedAt
                            || Volatile.Read(ref requests) != 3 || !await probe(port))
                            throw new InvalidOperationException("The active refresh did not reach the original native commit with a pending queue receipt.");
                        refreshTime = refresh.RetrievedAt;
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
                    if (await SubmitAsync(client, refreshRoute, activeKey) != refreshId)
                        throw new InvalidOperationException("Recovery rebound the original refresh idempotency key.");
                    var original = await ReadOperationAsync(client, refreshId);
                    var observed = await lifecycle.QueryAsync(ct, nativeId);
                    var draft = await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute, RelaxKonOSJsonOptions.Default, ct);
                    var subscription = await ReadSubscriptionAsync(client);
                    if (recovered.Identity != committed.ConfigurationIdentity || recovered.LastConfiguration?.OperationId != nativeId
                        || recovered.LastConfiguration.Confirmed != true || observed.Receipt != committed.Receipt
                        || observed.Installation.StateRevision != committed.Installation.StateRevision
                        || original.State != ProxyOperationState.Succeeded || original.CompletedAt is null
                        || draft?.RetrievedAt != refreshTime || subscription.LastUpdatedAt != refreshTime
                        || subscription.UpdatedAt != refreshTime || !subscription.IsActive
                        || draft?.Content.Contains("origin-3.example.test", StringComparison.Ordinal) != true
                        || Volatile.Read(ref requests) != 3 || !await probe(port))
                        throw new InvalidOperationException("Restart did not complete the original queue and native receipts with the frozen refresh time.");
                    if (await SubmitAsync(client, refreshRoute, activeKey) != refreshId || await ReadOperationAsync(client, refreshId) != original
                        || Volatile.Read(ref requests) != 3 || await ReadLeasesAsync(client) != 0)
                        throw new InvalidOperationException("A repeated observation changed the terminal receipt or started new work.");
                    Console.WriteLine($"PASS MANAGED MIHOMO ACTIVE SUBSCRIPTION CRASH: original queue and native receipt {refreshId} reconciled, frozen timestamp, no replay");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(recovery); }

                async Task CancelCompetingSettingsAsync(HttpClient client, Guid id)
                {
                    var prefix = Path.Combine(start.WorkingDirectory, "full-settings-cleanup");
                    var settingsBefore = await File.ReadAllBytesAsync(settingsFile, ct);
                    var saved = await new ProxySettingsReader(paths).GetAsync(ct);
                    var payload = new UpdateProxySettingsRequest(saved.SystemProxyEnabled, saved.AllowLan, saved.DnsEnabled,
                        saved.Ipv6Enabled, saved.UnifiedDelay, saved.LogLevel == "error" ? "warning" : "error", saved.MixedPort,
                        saved.AllowInsecureSubscriptionSources, saved.SystemProxyHost, saved.Tun, saved.SystemProxy);
                    await File.WriteAllTextAsync(prefix + "-arm", "armed", ct);
                    using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var pendingWrite = client.PutAsJsonAsync(ProxyApiRoutes.Settings, payload, RelaxKonOSJsonOptions.Default, caller.Token);
                    try
                    {
                        while (await ReadLeasesAsync(client) != 2)
                        {
                            if (pendingWrite.IsCompleted) throw new InvalidOperationException("The settings request did not wait for the original gate.");
                            await Task.Delay(25, ct);
                        }
                        await caller.CancelAsync();
                        try { using var unexpected = await pendingWrite; throw new InvalidOperationException("Expected the competing caller to disconnect."); }
                        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                        while (!File.Exists(prefix + "-held")) await Task.Delay(25, ct);
                        if (await ReadLeasesAsync(client) != 2 || (await publication.ReadAsync(ct)).Pending?.OperationId != id
                            || (await ReadOperationAsync(client, id)).State != ProxyOperationState.Running || !await probe(port))
                            throw new InvalidOperationException("The competing scope did not independently retain cleanup while activation owned the gate.");
                    }
                    finally { await File.WriteAllTextAsync(prefix + "-release", "release", CancellationToken.None); }
                    while (!File.Exists(prefix + "-complete") || await ReadLeasesAsync(client) != 1) await Task.Delay(25, ct);
                    if ((await publication.ReadAsync(ct)).Pending?.OperationId != id || Volatile.Read(ref configureCalls) != 1
                        || !(await File.ReadAllBytesAsync(settingsFile, ct)).AsSpan().SequenceEqual(settingsBefore))
                        throw new InvalidOperationException("Cancelling the competing writer changed the original publication or settings.");
                    Console.WriteLine($"PASS MANAGED MIHOMO SHARED GATE DRAIN: activation {id}; settings cancelled, cleanup drained independently, one native dispatch");
                }
            }

            async Task VerifyActiveRefreshResponseLossAsync()
            {
                var socket = start.Environment["PrivilegedHelper__SocketPath"]!;
                var key = Guid.NewGuid().ToString("N");
                await using var relay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-subscription-loss.sock",
                    socket, ct, static (kind, _) => kind == "MihomoRuntimeConfigure");
                start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
                using var server = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                    var previous = await ReadSubscriptionAsync(client);
                    var id = await SubmitAsync(client, refreshRoute, key);
                    var nativeId = await WaitForNativeCommitAsync(client, id, relay);
                    var receipt = await lifecycle.QueryAsync(ct, id);
                    var state = await publication.ReadAsync(ct);
                    if (nativeId != id || receipt.Receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded }
                        || state.Pending?.OperationId != id || state.Pending.Profile?.Refresh is not { } refresh
                        || (await ReadOperationAsync(client, id)).State != ProxyOperationState.Running
                        || await ReadLeasesAsync(client) != 1 || Volatile.Read(ref requests) != 4)
                        throw new InvalidOperationException("The active refresh did not own the original native response before disconnect.");

                    relay.DropHeldResponse();
                    var unknown = await WaitForTerminalAsync(client, id);
                    if (unknown.State != ProxyOperationState.Interrupted || unknown.Stage != "publication_pending"
                        || unknown.CompletedAt is not null || server.HasExited
                        || (await ReadSubscriptionAsync(client)).LastUpdatedAt != previous.LastUpdatedAt
                        || await SubmitAsync(client, refreshRoute, key) != id)
                        throw new InvalidOperationException("Socket loss did not leave an unresolved original receipt in the live Server.");
                    using var request = new HttpRequestMessage(HttpMethod.Post, refreshRoute);
                    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                    using var denied = await client.SendAsync(request, ct);
                    if (denied.StatusCode != HttpStatusCode.BadRequest
                        || !(await denied.Content.ReadAsStringAsync(ct)).Contains(ProxyProblemCodes.OperationInterrupted, StringComparison.Ordinal)
                        || Volatile.Read(ref requests) != 4)
                        throw new InvalidOperationException("Unresolved response loss admitted another refresh.");

                    ProxyOperationDto final;
                    do
                    {
                        final = await ReadOperationAsync(client, id);
                        if (final.State != ProxyOperationState.Interrupted) break;
                        await Task.Delay(50, ct);
                    } while (true);
                    var observed = await lifecycle.QueryAsync(ct, id);
                    var refreshed = await ReadSubscriptionAsync(client);
                    if (final.State != ProxyOperationState.Succeeded || final.CompletedAt is null || server.HasExited
                        || refreshed.LastUpdatedAt != refresh.RetrievedAt || refreshed.UpdatedAt != refresh.RetrievedAt
                        || observed.Receipt != receipt.Receipt || observed.ConfigurationIdentity != receipt.ConfigurationIdentity
                        || observed.Installation.StateRevision != receipt.Installation.StateRevision
                        || await SubmitAsync(client, refreshRoute, key) != id || await ReadOperationAsync(client, id) != final
                        || Volatile.Read(ref requests) != 4 || await ReadLeasesAsync(client) != 0 || !await probe(port))
                        throw new InvalidOperationException("The running Server did not recover the original response without replay.");
                    Console.WriteLine($"PASS MANAGED MIHOMO ACTIVE SUBSCRIPTION RESPONSE LOSS: original {id}, unresolved before proof, succeeded in the same Server, no replay");
                }
                finally
                {
                    await ProxyAuthenticatedPublicationChecks.StopAsync(server);
                    start.Environment["PrivilegedHelper__SocketPath"] = socket;
                }
            }

            async Task<ProxySubscriptionDto> ReadSubscriptionAsync(HttpClient client) =>
                (await client.GetFromJsonAsync<ProxySubscriptionDto[]>(ProxyApiRoutes.Subscriptions, RelaxKonOSJsonOptions.Default, ct))!
                    .Single(item => item.Id == subscriptionId);

            async Task VerifyFailedPublicationAsync(bool activation)
            {
                var socket = start.Environment["PrivilegedHelper__SocketPath"]!;
                var key = Guid.NewGuid().ToString("N");
                var route = activation ? SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, subscriptionId) : refreshRoute;
                var previous = await publication.ReadAsync(ct);
                Guid id;
                ProxySubscriptionDto subscription;
                RuntimeInstallationSnapshot rejected;
                await using (var relay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-subscription-failed.sock",
                    socket, ct, static (kind, _) => kind == "MihomoRuntimeConfigure"))
                {
                    start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
                    using var server = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                        subscription = await ReadSubscriptionAsync(client);
                        if (activation && (await client.GetFromJsonAsync<ProxySubscriptionContentDto>(contentRoute,
                            RelaxKonOSJsonOptions.Default, ct))?.Content.Contains("UNSUPPORTED-RULE", StringComparison.Ordinal) != true)
                            throw new InvalidOperationException("Missing previously downloaded invalid draft for native activation rejection.");
                        id = await SubmitAsync(client, route, key);
                        var nativeId = await WaitForNativeCommitAsync(client, id, relay);
                        rejected = await lifecycle.QueryAsync(ct, id);
                        var state = await publication.ReadAsync(ct);
                        if (nativeId != id || rejected.Receipt is not { Outcome: RuntimeSwitchOutcome.Failed }
                            || rejected.ConfigurationIdentity != previous.Identity || state.Pending?.OperationId != id
                            || state.Pending.Identity == previous.Identity
                            || (activation ? state.Pending.Profile?.ActivationSubscriptionId != subscriptionId
                                : state.Pending.Profile?.Refresh?.SubscriptionId != subscriptionId)
                            || !await probe(port)
                            || (await ReadOperationAsync(client, id)).State != ProxyOperationState.Running
                            || await ReadLeasesAsync(client) != 1 || Volatile.Read(ref requests) != 5)
                            throw new InvalidOperationException("The real failed subscription publication did not restore the previous configuration with its original receipt.");
                        server.Kill();
                        await server.WaitForExitAsync(ct);
                        if (server.ExitCode == 0) throw new InvalidOperationException("The failed refresh Server did not terminate at the reply boundary.");
                    }
                    finally
                    {
                        await ProxyAuthenticatedPublicationChecks.StopAsync(server);
                        start.Environment["PrivilegedHelper__SocketPath"] = socket;
                    }
                }

                using var recovery = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(recovery, login, ct);
                    ProxyOperationDto final;
                    do
                    {
                        final = await ReadOperationAsync(client, id);
                        if (final.State != ProxyOperationState.Interrupted) break;
                        await Task.Delay(100, ct);
                    } while (true);
                    var observed = await lifecycle.QueryAsync(ct, id);
                    var state = await publication.ReadAsync(ct);
                    if (final.State != ProxyOperationState.Failed || final.ProblemCode != ProxyProblemCodes.ConfigApplyFailed
                        || final.CompletedAt is null || state.Pending is not null || state.Identity != previous.Identity
                        || state.LastConfiguration is not { Confirmed: false, NotStarted: false } failure || failure.OperationId != id
                        || observed.Receipt != rejected.Receipt || observed.Installation.StateRevision != rejected.Installation.StateRevision
                        || await ReadSubscriptionAsync(client) != subscription || await SubmitAsync(client, route, key) != id
                        || (activation && state.SubscriptionActivationCompletions?.GetValueOrDefault(id)?.Confirmed != false)
                        || await ReadOperationAsync(client, id) != final || Volatile.Read(ref requests) != 5
                        || await ReadLeasesAsync(client) != 0 || !await probe(port))
                        throw new InvalidOperationException("Restart did not finish the original failed queue receipt without replay or changing the subscription timestamp.");
                    Console.WriteLine(activation
                        ? $"PASS MANAGED MIHOMO SUBSCRIPTION ACTIVATION COMPENSATION: original failed {id}, previous traffic and timestamp preserved, no replay"
                        : $"PASS MANAGED MIHOMO ACTIVE SUBSCRIPTION COMPENSATION: original failed {id}, previous traffic and timestamp preserved, no replay");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(recovery); }
            }

            async Task VerifySettingsFirstAsync()
            {
                var socket = start.Environment["PrivilegedHelper__SocketPath"]!;
                var prefix = Path.Combine(start.WorkingDirectory, "full-settings-cleanup");
                foreach (var marker in new[] { "arm", "held", "release", "complete" }) File.Delete(prefix + "-" + marker);
                await File.WriteAllTextAsync(prefix + "-arm", "armed", ct);
                var previous = await new ProxySettingsReader(paths).GetAsync(ct);
                var desired = previous with { LogLevel = previous.LogLevel == "error" ? "warning" : "error" };
                var payload = new UpdateProxySettingsRequest(desired.SystemProxyEnabled, desired.AllowLan, desired.DnsEnabled,
                    desired.Ipv6Enabled, desired.UnifiedDelay, desired.LogLevel, desired.MixedPort,
                    desired.AllowInsecureSubscriptionSources, desired.SystemProxyHost, desired.Tun, desired.SystemProxy);
                var route = SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, subscriptionId);
                var key = Guid.NewGuid().ToString("N");
                var dispatches = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
                Guid settingsId;
                Guid activationId;
                ProxyOperationDto final;
                RuntimeInstallationSnapshot nativeSettings;
                RuntimeInstallationSnapshot nativeActivation;
                await using (var relay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-settings-first.sock",
                    socket, ct, (kind, id) =>
                    {
                        if (kind != "MihomoRuntimeConfigure") return false;
                        dispatches.Enqueue(id);
                        return true;
                    }))
                {
                    start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
                    start.Environment["RELAXKONOS_TEST_SETTINGS_CLEANUP"] = start.WorkingDirectory;
                    using var server = ProxyAuthenticatedPublicationChecks.Start(start, OnError, line =>
                    {
                        if (line.Contains("Exception", StringComparison.Ordinal) || line.Contains("Proxy operation", StringComparison.Ordinal)
                            || line.Contains("ProblemCode=module-", StringComparison.Ordinal)) Console.WriteLine(line);
                    });
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                        using var settingsClient = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                        var selectedBefore = await ReadSubscriptionAsync(client);
                        // The settings connection may receive its 204 while the Server still owns scope cleanup.
                        var write = settingsClient.PutAsJsonAsync(ProxyApiRoutes.Settings, payload, RelaxKonOSJsonOptions.Default, ct);
                        if (await Task.WhenAny(relay.Committed.Task, write).WaitAsync(ct) == write)
                        {
                            using var early = await write;
                            throw new InvalidOperationException($"Settings returned {early.StatusCode} before the original reply was held.");
                        }
                        settingsId = await relay.Committed.Task;
                        nativeSettings = await lifecycle.QueryAsync(ct, settingsId);
                        var pending = (await publication.ReadAsync(ct)).Pending;
                        if (pending?.OperationId != settingsId || pending.Settings?.Candidate != desired
                            || nativeSettings.Receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded }
                            || nativeSettings.ConfigurationIdentity != pending.Identity || !await probe(port))
                            throw new InvalidOperationException("The foreground settings did not commit under their original ID.");

                        activationId = await SubmitAsync(client, route, key);
                        while ((await ReadOperationAsync(client, activationId)).State == ProxyOperationState.Queued) await Task.Delay(25, ct);
                        if ((await ReadOperationAsync(client, activationId)).State != ProxyOperationState.Running
                            || await ReadLeasesAsync(client) != 2 || !dispatches.ToArray().SequenceEqual([settingsId])
                            || (await publication.ReadAsync(ct)).Pending?.OperationId != settingsId
                            || await SubmitAsync(client, route, key) != activationId || Volatile.Read(ref requests) != 5)
                            throw new InvalidOperationException("The queued activation did not wait with its own lease behind the foreground gate.");

                        Console.WriteLine($"MIHOMO SETTINGS FIRST WAITING: settings {settingsId}; activation {activationId}");
                        relay.ReleaseHeldResponse();
                        while (!File.Exists(prefix + "-held")) await Task.Delay(25, ct);
                        Console.WriteLine("MIHOMO SETTINGS FIRST CLEANUP: foreground scope held");
                        do
                        {
                            final = await ReadOperationAsync(client, activationId);
                            if (final.CompletedAt is not null) break;
                            await Task.Delay(25, ct);
                        } while (true);
                        while (await ReadLeasesAsync(client) != 1) await Task.Delay(25, ct);
                        nativeActivation = await lifecycle.QueryAsync(ct, activationId);
                        var settled = await publication.ReadAsync(ct);
                        if (final.State != ProxyOperationState.Failed || final.ProblemCode != ProxyProblemCodes.ConfigApplyFailed
                            || nativeActivation.Receipt is not { Outcome: RuntimeSwitchOutcome.Failed }
                            || settled.Pending is not null || settled.Identity != nativeSettings.ConfigurationIdentity
                            || nativeActivation.ConfigurationIdentity != settled.Identity || await ReadSubscriptionAsync(client) != selectedBefore
                            || await new ProxySettingsReader(paths).GetAsync(ct) != desired || Volatile.Read(ref requests) != 5
                            || !dispatches.ToArray().SequenceEqual([settingsId, activationId]) || !await probe(port))
                            throw new InvalidOperationException("The queued activation did not finish once while the foreground scope still owned cleanup.");
                        await File.WriteAllTextAsync(prefix + "-release", "release", ct);
                        using var response = await write;
                        response.EnsureSuccessStatusCode();
                        while (!File.Exists(prefix + "-complete") || await ReadLeasesAsync(client) != 0) await Task.Delay(25, ct);
                        Console.WriteLine("MIHOMO SETTINGS FIRST CLEANUP: both owners drained");
                    }
                    finally
                    {
                        relay.ReleaseHeldResponse();
                        await File.WriteAllTextAsync(prefix + "-release", "release", CancellationToken.None);
                        start.Environment.Remove("RELAXKONOS_TEST_SETTINGS_CLEANUP");
                        await ProxyAuthenticatedPublicationChecks.StopAsync(server);
                        start.Environment["PrivilegedHelper__SocketPath"] = socket;
                    }
                }
                using var restarted = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(restarted, login, ct);
                    var afterSettings = await lifecycle.QueryAsync(ct, settingsId);
                    var afterActivation = await lifecycle.QueryAsync(ct, activationId);
                    if (await SubmitAsync(client, route, key) != activationId || await ReadOperationAsync(client, activationId) != final
                        || afterSettings.Receipt != nativeSettings.Receipt || afterActivation.Receipt != nativeActivation.Receipt
                        || afterActivation.ConfigurationIdentity != nativeSettings.ConfigurationIdentity || await ReadLeasesAsync(client) != 0
                        || Volatile.Read(ref requests) != 5 || !await probe(port))
                        throw new InvalidOperationException("Restart changed an original receipt or repeated a subscription side effect.");
                    Console.WriteLine($"PASS MANAGED MIHOMO SETTINGS FIRST DRAIN: settings {settingsId}; activation {activationId}; ordered dispatch, independent cleanup, original receipts");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(restarted); }
            }

            [SupportedOSPlatform("linux")]
            async Task<Guid> VerifyPostSwitchCompensationAsync()
            {
                const string fault = "/run/relaxkonos-mihomo-start-fault";
                if (!File.Exists(Path.Combine(fault, "systemctl")))
                    throw new InvalidOperationException("The private Mihomo systemd fault boundary is unavailable.");
                var previous = await publication.ReadAsync(ct);
                var socket = start.Environment["PrivilegedHelper__SocketPath"]!;
                var key = Guid.NewGuid().ToString("N");
                var writes = 0;
                string route;
                Guid id;
                ProxySubscriptionDto draft;
                ProxySubscriptionDto originalSubscription;
                RuntimeInstallationSnapshot? failed = null;
                int? restoredPid = null;
                int candidatePid;
                await using (var relay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-post-switch.sock", socket, ct,
                    (kind, _) =>
                    {
                        if (kind != "MihomoRuntimeConfigure") return false;
                        Interlocked.Increment(ref writes);
                        return true;
                    }))
                {
                    start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
                    using var server = ProxyAuthenticatedPublicationChecks.Start(start, OnError);
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(server, login, ct);
                        originalSubscription = await ReadSubscriptionAsync(client);
                        using var imported = await client.PostAsJsonAsync(ProxyApiRoutes.Subscriptions,
                            new ImportProxySubscriptionRequest(endpoint + "/feed", "post-switch-fault"), RelaxKonOSJsonOptions.Default, ct);
                        imported.EnsureSuccessStatusCode();
                        draft = await imported.Content.ReadFromJsonAsync<ProxySubscriptionDto>(RelaxKonOSJsonOptions.Default, ct)
                            ?? throw new InvalidOperationException("The valid activation draft was not created.");
                        route = SubscriptionRoute(ProxyApiRoutes.SubscriptionActivatePattern, draft.Id);
                        var originalPid = await ReadPidAsync();
                        if (!originalSubscription.IsActive || draft.IsActive || originalPid <= 0 || !await probe(port))
                            throw new InvalidOperationException("The original managed publication is not active.");
                        File.WriteAllText(Path.Combine(fault, "armed"), "candidate");
                        id = await SubmitAsync(client, route, key);
                        while (!File.Exists(Path.Combine(fault, "started")))
                        {
                            if ((await ReadOperationAsync(client, id)).CompletedAt is not null)
                                throw new InvalidOperationException("The original activation terminated before the native switch.");
                            await Task.Delay(25, ct);
                        }
                        var pending = (await publication.ReadAsync(ct)).Pending;
                        candidatePid = await ReadPidAsync();
                        if (pending?.OperationId != id || pending.Profile?.ActivationSubscriptionId != draft.Id
                            || pending.Identity == previous.Identity || candidatePid <= 0 || candidatePid == originalPid
                            || !string.Equals(await ReadConfigurationIdentityAsync(candidatePid), pending.Identity, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The original activation did not start the expected candidate configuration.");
                        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            ready.CancelAfter(TimeSpan.FromSeconds(5));
                            while (true)
                            {
                                using var connection = new TcpClient();
                                try { await connection.ConnectAsync(IPAddress.Loopback, port, ready.Token); break; }
                                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
                                { await Task.Delay(25, ready.Token); }
                            }
                        }
                        if (!await probe(port) || await ReadPidAsync() != candidatePid || await ReadLeasesAsync(client) != 1
                            || (await ReadOperationAsync(client, id)).State != ProxyOperationState.Running || Volatile.Read(ref writes) != 1)
                            throw new InvalidOperationException("The original activation did not serve real traffic from the candidate configuration.");
                        if (restartHelper is not null)
                        {
                            server.Kill();
                            await server.WaitForExitAsync(ct);
                            await restartHelper(async () =>
                            {
                                var unresolved = await lifecycle.QueryAsync(ct, id);
                                if (unresolved.PendingOperationId != id || unresolved.Receipt is not null
                                    || (await publication.ReadAsync(ct)).Pending?.OperationId != id
                                    || await ReadPidAsync() != candidatePid || !await probe(port))
                                    throw new InvalidOperationException("The interrupted Helper did not leave the original unresolved activation serving traffic.");
                                Console.WriteLine($"MIHOMO ACTIVATION PENDING: {id}; native stage {unresolved.PendingStage}, candidate PID {candidatePid}, no native receipt");
                                File.WriteAllText(Path.Combine(fault, "release"), "helper-interrupted");
                            });
                        }
                        else
                        {
                            await NativeRuntimeProcess.RunAsync("/usr/bin/systemctl", ["stop", LinuxMihomoArchiveHost.Unit], 1024);
                            if (await ReadPidAsync() != 0)
                                throw new InvalidOperationException("The isolated candidate did not stop.");
                            File.WriteAllText(Path.Combine(fault, "release"), "stopped");
                            if (await WaitForNativeCommitAsync(client, id, relay) != id)
                                throw new InvalidOperationException("The Helper replied for a different activation.");
                            failed = await lifecycle.QueryAsync(ct, id);
                            var pid = await ReadPidAsync();
                            if (failed.Receipt is not { Outcome: RuntimeSwitchOutcome.Failed }
                                || failed.ConfigurationIdentity != previous.Identity || pid <= 0 || pid == candidatePid
                                || !string.Equals(await ReadConfigurationIdentityAsync(pid), previous.Identity, StringComparison.OrdinalIgnoreCase)
                                || !await probe(port) || (await ReadOperationAsync(client, id)).State != ProxyOperationState.Running
                                || await ReadLeasesAsync(client) != 1 || Volatile.Read(ref writes) != 1)
                                throw new InvalidOperationException("Native compensation did not restore the previous serving configuration.");
                            restoredPid = pid;
                            server.Kill();
                            await server.WaitForExitAsync(ct);
                        }
                    }
                    finally
                    {
                        File.WriteAllText(Path.Combine(fault, "release"), "cleanup");
                        await ProxyAuthenticatedPublicationChecks.StopAsync(server);
                        start.Environment["PrivilegedHelper__SocketPath"] = socket;
                    }
                }

                using var recovery = ProxyAuthenticatedPublicationChecks.Start(start, OnError, line =>
                {
                    if (restartHelper is not null && line.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)
                        && line.Contains("Mihomo", StringComparison.Ordinal)) Console.WriteLine(line);
                });
                try
                {
                    using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(recovery, login, ct);
                    ProxyOperationDto final;
                    do
                    {
                        final = await ReadOperationAsync(client, id);
                        if (final.State != ProxyOperationState.Interrupted) break;
                        await Task.Delay(250, ct);
                    } while (true);
                    var observed = await lifecycle.QueryAsync(ct, id);
                    var state = await publication.ReadAsync(ct);
                    var subscriptions = await client.GetFromJsonAsync<ProxySubscriptionDto[]>(ProxyApiRoutes.Subscriptions, RelaxKonOSJsonOptions.Default, ct);
                    var pid = await ReadPidAsync();
                    var nativeOutcome = restartHelper is null ? RuntimeSwitchOutcome.Failed : RuntimeSwitchOutcome.Interrupted;
                    if (final.State != ProxyOperationState.Failed || final.ProblemCode != ProxyProblemCodes.ConfigApplyFailed
                        || final.CompletedAt is null || state.Pending is not null || state.Identity != previous.Identity
                        || state.SubscriptionActivationCompletions?.GetValueOrDefault(id)?.Confirmed != false
                        || observed.PendingOperationId is not null || observed.Receipt is not { } receipt
                        || receipt.OperationId != id || receipt.Outcome != nativeOutcome || observed.ConfigurationIdentity != previous.Identity
                        || (failed is not null && (receipt != failed.Receipt || observed.Installation.StateRevision != failed.Installation.StateRevision))
                        || pid <= 0 || pid == candidatePid || (restoredPid is not null && pid != restoredPid)
                        || !string.Equals(await ReadConfigurationIdentityAsync(pid), previous.Identity, StringComparison.OrdinalIgnoreCase)
                        || await ReadSubscriptionAsync(client) != originalSubscription
                        || subscriptions?.Single(item => item.Id == draft.Id) != draft || await SubmitAsync(client, route, key) != id
                        || await ReadOperationAsync(client, id) != final || Volatile.Read(ref requests) != 6
                        || await ReadLeasesAsync(client) != 0 || !await probe(port))
                        throw new InvalidOperationException("Restart did not recover the original compensated activation without replay.");
                    var boundary = restartHelper is null ? "POST-SWITCH COMPENSATION" : "POST-SWITCH HELPER INTERRUPTION";
                    Console.WriteLine($"PASS MANAGED MIHOMO {boundary}: {id}; real candidate traffic, native {nativeOutcome}, restored original identity and receipt");
                }
                finally { await ProxyAuthenticatedPublicationChecks.StopAsync(recovery); }
                return id;

                async Task<int> ReadPidAsync() => int.Parse((await NativeRuntimeProcess.RunAsync("/usr/bin/systemctl",
                    ["show", LinuxMihomoArchiveHost.Unit, "--property=MainPID", "--value"], 1024)).Trim());

                async Task<string> ReadConfigurationIdentityAsync(int pid) => Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync($"/proc/{pid}/root/run/credentials/{LinuxMihomoArchiveHost.Unit}/active.yaml", ct)));
            }

            void OnError(string line)
            {
                if (line.StartsWith(StartupHook.SubscriptionEventPrefix, StringComparison.Ordinal))
                    events.Writer.TryWrite(line[StartupHook.SubscriptionEventPrefix.Length..]);
                if (line.StartsWith(StartupHook.SubscriptionCollectionPrefix, StringComparison.Ordinal))
                    collections.Writer.TryWrite(line[StartupHook.SubscriptionCollectionPrefix.Length..]);
            }

            async Task VerifyCollectedAsync(string loaded)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                if (await collections.Reader.ReadAsync(deadline.Token) != loaded[5..])
                    throw new InvalidOperationException("The original subscription execution generation was not collected.");
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

            async Task<ProxyOperationDto> ReadOperationAsync(HttpClient client, Guid id)
            {
                using var response = await client.GetAsync(ProxyApiRoutes.Proxy
                    + ProxyApiRoutes.OperationsPattern.Replace("{operationId:guid}", id.ToString("D"), StringComparison.Ordinal),
                    ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Subscription operation {id} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                return await response.Content.ReadFromJsonAsync<ProxyOperationDto>(RelaxKonOSJsonOptions.Default, ct)
                    ?? throw new InvalidOperationException("Missing original subscription operation.");
            }

            async Task<ProxyOperationDto> WaitForTerminalAsync(HttpClient client, Guid id)
            {
                while (true)
                {
                    var operation = await ReadOperationAsync(client, id);
                    if (operation.State is not (ProxyOperationState.Queued or ProxyOperationState.Running)) return operation;
                    await Task.Delay(25, ct);
                }
            }

            async Task<Guid> WaitForNativeCommitAsync(HttpClient client, Guid id,
                ProxyAuthenticatedPublicationChecks.ReplyRelay relay)
            {
                while (!relay.Committed.Task.IsCompleted)
                {
                    var operation = await ReadOperationAsync(client, id);
                    if (operation.State is not (ProxyOperationState.Queued or ProxyOperationState.Running))
                        throw new InvalidOperationException($"Refresh {id} left execution before the native reply: {operation.State}/{operation.ProblemCode}.");
                    await Task.WhenAny(relay.Committed.Task, Task.Delay(100, ct)).WaitAsync(ct);
                }
                return await relay.Committed.Task;
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
