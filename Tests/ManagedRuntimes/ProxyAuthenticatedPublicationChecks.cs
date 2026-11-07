using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.ProxyServer;

internal static class ProxyAuthenticatedPublicationChecks
{
    internal static async Task RunAsync(string root, string assembly, string socket, ISingBoxArchiveHost host,
        ProxyFullServerTrafficProbe traffic, ProxyServerSecret retained, Func<RuntimeArchiveState> nativeState,
        Func<Task> stopHelper, Func<Task> restartHelper, ProxySystemdGuardProbe guard,
        Func<Guid, Task<ProxyServerPublicationReceipt>> readReceipt)
    {
        var login = ReadLogin();
        var store = ManagedServiceChecks.OpenFullServerStore(root);
        var state = await store.ReadAsync(default);
        var published = state.Published ?? throw new InvalidOperationException("Missing private published material.");
        var inbound = published.Configuration.Inbounds.Single();
        var added = new StoredCredential(Guid.NewGuid(), Guid.NewGuid(), "authenticated-fixture", [inbound.Id],
            Guid.NewGuid().ToString("D"), true, null);
        var draft = published.Configuration with { Revision = published.Configuration.Revision + 1 };
        await store.WriteAsync(state with { Configuration = draft, Credentials = [.. state.Credentials, added] }, default);
        await SeedCertificateAsync(Path.Combine(root, "full-server"), inbound, published.Certificates!.Single());
        // The ordinary fixture assigns this entire private content directory to the non-root Server.
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Certificate__StorageRoot"] = Path.Combine(start.WorkingDirectory, "certificate-fixture");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = budget.Token;
        var before = nativeState().Installation.StateRevision;
        var key = Guid.NewGuid().ToString("N");
        Guid operation;
        string nativePid;
        // /run is a private tmpfs in the systemd fixture, so the relay socket is invisible to the host.
        await using (var relay = new ReplyRelay("/run/relaxkonos-proxy-fixture.sock", socket, ct))
        {
            start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
            using var initial = Start(start);
            try
            {
                using var client = await ConnectAsync(initial, login, ct);
                operation = await SubmitAsync(client, ProxyServerRoutes.Apply, key, ct);
                while (!relay.Committed.Task.IsCompleted)
                {
                    var current = await ReadAsync(client, operation, ct);
                    if (current.State is not (ProxyOperationState.Running or ProxyOperationState.Queued))
                        throw new InvalidOperationException($"The original publication ended before the reply barrier: {current.ProblemCode}");
                    await Task.Delay(40, ct);
                }
                var pending = await store.ReadAsync(ct);
                if (await relay.Committed.Task != operation || pending.PendingPublicationOperationId != operation
                    || !pending.PendingQueuedPublication || nativeState().Installation.StateRevision != before + 1)
                    throw new InvalidOperationException("Production HTTP and native apply did not share the same pending operation.");
                using var live = await traffic.ForwardAsync(added.Value, ct);
                nativePid = await ReadNativePidAsync();
                initial.Kill();
                await initial.WaitForExitAsync(ct);
                if (initial.ExitCode == 0) throw new InvalidOperationException("The original Server did not crash.");
            }
            finally { await StopAsync(initial); }
        }

        start.Environment["PrivilegedHelper__SocketPath"] = socket;
        using var restarted = Start(start);
        try
        {
            using var client = await ConnectAsync(restarted, login, ct);
            using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
            using var rejected = await anonymous.PostAsync(ProxyServerRoutes.Recovery, null, ct);
            if (rejected.StatusCode != HttpStatusCode.Unauthorized
                || await SubmitAsync(client, ProxyServerRoutes.Apply, key, ct) != operation
                || (await ReadAsync(client, operation, ct)).State != ProxyOperationState.Interrupted)
                throw new InvalidOperationException("Production authorization or the original interrupted receipt was lost.");
            var recoveryKey = Guid.NewGuid().ToString("N");
            if (await SubmitAsync(client, ProxyServerRoutes.Recovery, recoveryKey, ct) != operation)
                throw new InvalidOperationException("The recovery route returned a different operation ID.");
            ProxyOperationDto receipt;
            do { receipt = await ReadAsync(client, operation, ct); if (receipt.State != ProxyOperationState.Succeeded) await Task.Delay(40, ct); }
            while (receipt.State is ProxyOperationState.Running or ProxyOperationState.Queued or ProxyOperationState.Interrupted);
            var final = await store.ReadAsync(ct);
            if (receipt.State != ProxyOperationState.Succeeded || final.PendingPublication is not null
                || final.QueuedPublicationCompletions.GetValueOrDefault(operation) != ProxyServerPublicationOutcome.Committed
                || await SubmitAsync(client, ProxyServerRoutes.Recovery, recoveryKey, ct) != operation
                || await ReadAsync(client, operation, ct) != receipt || nativeState().Installation.StateRevision != before + 1 || !(await host.ObserveAsync(ct)).Running
                || await ReadNativePidAsync() != nativePid)
                throw new InvalidOperationException("The original publication was not confirmed exactly once.");
            using var oldTraffic = await traffic.ForwardAsync(retained.Value, ct);
            using var newTraffic = await traffic.ForwardAsync(added.Value, ct);
            Console.WriteLine($"PASS AUTHENTICATED PROXY QUEUE RECOVERY: {operation}; PAM, original HTTP receipt, native apply once and real traffic");
        }
        finally { await StopAsync(restarted); }
        await VerifyInFlightAsync(root, assembly, socket, host, traffic, retained, added, login, nativeState, stopHelper, restartHelper);
        await VerifyMihomoOverviewRestartAsync(root, assembly, socket, login, nativeState, guard);
        await VerifyMihomoObservationCancellationAsync(root, assembly, socket, login, nativeState, guard);
        await VerifySystemProxyShutdownAsync(root, assembly, socket, login, nativeState, guard);
        await VerifySafetyShutdownAsync(root, assembly, socket, login, nativeState, guard, traffic, retained, added, readReceipt);
        await VerifyManagedPublicationShutdownAsync(root, assembly, socket, login, nativeState, traffic, retained, added, readReceipt);
        await VerifySubscriptionBodyResidencyAsync(guard);
        await VerifyDirectReloadLossAsync(root, socket, guard);
        if (Environment.GetEnvironmentVariable("RELAXKONOS_TEST_PROXY_BENCHMARK_BASELINE") is { Length: > 0 } baseline)
            await ProxyConnectionBenchmark.RunAsync(root, socket, baseline, assembly, login, guard, nativeState);
    }

    private static async Task VerifyDirectReloadLossAsync(string root, string socket, ProxySystemdGuardProbe guard)
    {
        var paths = new RelaxKonOS.Server.Proxy.Platform.ProxyPlatformPaths();
        var protection = ManagedServiceChecks.OpenProtection(root);
        var secrets = new RelaxKonOS.Server.Proxy.Mihomo.DataProtectionProxyControllerSecretStore(
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.Combine(root, "full-server") }, protection);
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        var nativeOptions = new RelaxKonOS.Server.Proxy.Mihomo.MihomoControllerOptions { Endpoint = new("http://127.0.0.1:9090/") };
        using var upstream = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = nativeOptions.Endpoint };
        using var native = new RelaxKonOS.Server.Proxy.Mihomo.MihomoControllerClient(upstream, secrets, nativeOptions);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var relay = builder.Build();
        var dropAfterDispatch = false;
        var holdResponse = true;
        var relayWrites = 0;
        var nativeWrites = 0;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.Run(async context =>
        {
            var reload = HttpMethods.IsPut(context.Request.Method) && context.Request.Path == "/configs";
            if (reload)
            {
                Interlocked.Increment(ref relayWrites);
                if (!Volatile.Read(ref dropAfterDispatch)) { context.Abort(); return; }
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
            if (reload)
            {
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
            }
            using var response = await upstream.SendAsync(request, context.RequestAborted);
            if (reload && response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref nativeWrites);
                committed.TrySetResult();
                if (Volatile.Read(ref holdResponse))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    return;
                }
            }
            context.Response.StatusCode = (int)response.StatusCode;
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await relay.StartAsync();
        var address = relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var previous = await new RelaxKonOS.Server.Proxy.ProxySettingsReader(paths).GetAsync(default);
        var original = await File.ReadAllBytesAsync(active);
        var pid = guard.RuntimePid;
        await guard.VerifyTrafficAsync(async (_, ct) =>
        {
            var oldLevel = await ReadLevelAsync(ct);
            if (oldLevel == "warning") throw new InvalidOperationException("The test must change the live log level.");
            var rejectedPaths = await CreatePathsAsync("before-dispatch", ct);
            var rejected = new RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore(rejectedPaths, protection);
            if (await ExecuteDirectReloadFixtureAsync(root, rejectedPaths.GetStateDirectory(), address, ct) != ProxyProblemCodes.RecoveryRequired
                || (await rejected.ReadAsync(ct)).DirectReload is null
                || Volatile.Read(ref relayWrites) != 1 || Volatile.Read(ref nativeWrites) != 0
                || !(await File.ReadAllBytesAsync(active, ct)).SequenceEqual(original) || await ReadLevelAsync(ct) != oldLevel)
                throw new InvalidOperationException("A pre-dispatch disconnect did not keep the original durable checkpoint and configuration.");

            var crashPaths = await CreatePathsAsync("after-dispatch", ct);
            var beforeCrash = new RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore(crashPaths, protection);
            Volatile.Write(ref dropAfterDispatch, true);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (var argument in new[] { typeof(ProxyAuthenticatedPublicationChecks).Assembly.Location,
                "runtime-proxy-direct-reload-crash", root, crashPaths.GetStateDirectory(), address })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the private direct reload writer.");
            Guid operation;
            try
            {
                if (await Task.WhenAny(committed.Task, child.WaitForExitAsync(ct)).WaitAsync(ct) != committed.Task)
                    throw new InvalidOperationException($"The direct writer exited before the real controller acknowledgement. Exit={child.ExitCode}.");
                var checkpoint = (await beforeCrash.ReadAsync(ct)).DirectReload
                    ?? throw new InvalidOperationException("The original direct checkpoint was not durable before native apply.");
                operation = checkpoint.OperationId;
                if (RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore.Identity(await File.ReadAllTextAsync(active, ct)) != checkpoint.CandidateIdentity
                    || await ReadLevelAsync(ct) != "warning" || Volatile.Read(ref nativeWrites) != 1)
                    throw new InvalidOperationException("The crash barrier did not reach the actual candidate.");
                child.Kill();
                await child.WaitForExitAsync(ct);
                if (child.ExitCode == 0) throw new InvalidOperationException("The direct writer did not crash.");
            }
            finally { await StopAsync(child); }

            var afterCrash = new RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore(crashPaths, protection);
            var services = new ServiceCollection();
            services.AddSingleton(afterCrash);
            services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryService>();
            services.AddLogging();
            await using var provider = services.BuildServiceProvider();
            var recovery = new RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryScheduler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryScheduler>>());
            var blocked = await recovery.ScanAsync(null, ct);
            if (blocked != operation || await recovery.ScanAsync(blocked, ct) != operation
                || (await afterCrash.ReadAsync(ct)).DirectReload?.OperationId != operation
                || await ExecuteDirectReloadFixtureAsync(root, crashPaths.GetStateDirectory(), address, ct) != ProxyProblemCodes.RecoveryRequired
                || Volatile.Read(ref relayWrites) != 2 || Volatile.Read(ref nativeWrites) != 1
                || !(await File.ReadAllBytesAsync(active, ct)).SequenceEqual(original)
                || await new RelaxKonOS.Server.Proxy.ProxySettingsReader(crashPaths).GetAsync(ct) != previous
                || await ReadLevelAsync(ct) != "warning" || guard.RuntimePid != pid)
                throw new InvalidOperationException("Restart did not preserve the original identity, old disk state and live unknown result without replay.");

            // An external controller reload has no Helper receipt. Recovery must work without a
            // privileged transport and must not reinterpret the live candidate as a success.
            var runtime = new MihomoRuntimeService(null!, afterCrash, null!, null!, null!);
            var directRecovery = new MihomoRuntimeRecoveryService(afterCrash, runtime, null!, null!, null!);
            var pending = await runtime.RecoveryAsync(ct);
            var receipt = await directRecovery.RetryAsync(operation, ct);
            if (!pending.Required || pending.OperationId != operation || pending.Service is not null
                || receipt != new RuntimeSwitchResult(operation, RuntimeSwitchOutcome.RecoveryRequired, "runtime.direct_reload_unverified")
                || (await afterCrash.ReadAsync(ct)).DirectReload?.OperationId != operation
                || Volatile.Read(ref relayWrites) != 2 || Volatile.Read(ref nativeWrites) != 1
                || !(await File.ReadAllBytesAsync(active, ct)).SequenceEqual(original)
                || await ReadLevelAsync(ct) != "warning" || guard.RuntimePid != pid)
                throw new InvalidOperationException("Named direct recovery replayed or closed the unknown live result.");
            Console.WriteLine($"PASS MIHOMO DIRECT NAMED RECOVERY: original {operation}; recoveryRequired, one native candidate, zero replay");

            if (await native.ReloadAsync(ct) is not null || await ReadLevelAsync(ct) != oldLevel)
                throw new InvalidOperationException("Explicit fixture cleanup did not reload the original protected configuration.");
            Console.WriteLine($"PASS MIHOMO DIRECT RELOAD LOSS: original {operation}; pre-dispatch loss and real post-apply process crash, disk recovery without replay");
        });

        Volatile.Write(ref holdResponse, false);
        foreach (var barrier in new[] { "before-sqlite", "after-sqlite" })
        {
            await guard.VerifyTrafficAsync(async (_, ct) =>
            {
                var profilePaths = await CreatePathsAsync(barrier, ct);
                var stateDir = profilePaths.GetStateDirectory();
                var repository = await MihomoProfilePublicationChecks.CreateAsync(stateDir);
                var profile = await repository.UpsertAsync(null, barrier, MihomoEngine.Id, null, ct);
                var store = new MihomoPublicationStore(profilePaths, protection);
                var priorWrites = Volatile.Read(ref nativeWrites);
                var priorRequests = Volatile.Read(ref relayWrites);
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                foreach (var argument in new[] { typeof(ProxyAuthenticatedPublicationChecks).Assembly.Location,
                    "runtime-proxy-direct-profile-crash", root, stateDir, address, guard.RuntimeExecutable, profile.Id.ToString("D"), barrier })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the direct profile writer.");
                MihomoDirectReload checkpoint;
                try
                {
                    while (!File.Exists(Path.Combine(stateDir, "commit-barrier")))
                    {
                        if (child.HasExited) throw new InvalidOperationException($"The direct profile writer exited before {barrier}. Exit={child.ExitCode}.");
                        await Task.Delay(30, ct);
                    }
                    checkpoint = (await store.ReadAsync(ct)).DirectReload
                        ?? throw new InvalidOperationException("The acknowledged profile checkpoint was not durable.");
                    var observed = await repository.GetAsync(profile.Id, ct);
                    var sqliteCommitted = barrier == "after-sqlite";
                    if (!checkpoint.ProfileReloadConfirmed || checkpoint.Profile?.ProfileId != profile.Id
                        || observed?.Revision != profile.Revision + (sqliteCommitted ? 1 : 0) || observed.IsActive != sqliteCommitted
                        || Volatile.Read(ref nativeWrites) != priorWrites + 1 || Volatile.Read(ref relayWrites) != priorRequests + 1
                        || await ReadConfigAsync("mode", ct) != "rule")
                        throw new InvalidOperationException($"Unexpected side effects at {barrier}.");
                    child.Kill();
                    await child.WaitForExitAsync(ct);
                    if (child.ExitCode == 0) throw new InvalidOperationException("The profile writer did not crash.");
                }
                finally { await StopAsync(child); }

                var reopened = new MihomoPublicationStore(profilePaths, protection);
                var services = new ServiceCollection().AddSingleton(reopened)
                    .AddSingleton<IProxyProfileRepository>(await MihomoProfilePublicationChecks.CreateAsync(stateDir))
                    .AddSingleton<MihomoPublicationRecoveryService>().AddLogging();
                await using var provider = services.BuildServiceProvider();
                var recovery = provider.GetRequiredService<MihomoPublicationRecoveryService>();
                var named = new MihomoRuntimeRecoveryService(reopened, null!, null!, null!, null!, repository);
                if (await recovery.ReconcileAsync(ct) is not null || await reopened.ReadPendingIdentityAsync(ct) is not null
                    || await named.RetryAsync(checkpoint.OperationId, ct) != new RuntimeSwitchResult(checkpoint.OperationId, RuntimeSwitchOutcome.Succeeded, null)
                    || await repository.GetAsync(profile.Id, ct) is not { IsActive: true } selected || selected.Revision != profile.Revision + 1
                    || MihomoPublicationStore.Identity(await File.ReadAllTextAsync(active, ct)) != checkpoint.CandidateIdentity
                    || Volatile.Read(ref nativeWrites) != priorWrites + 1 || Volatile.Read(ref relayWrites) != priorRequests + 1
                    || guard.RuntimePid != pid)
                    throw new InvalidOperationException($"Restart did not settle the original profile commit at {barrier}.");
                Console.WriteLine($"PASS MIHOMO DIRECT PROFILE RECOVERY: {barrier}, original {checkpoint.OperationId}, one native reload and one profile revision");
                await File.WriteAllBytesAsync(active, original, ct);
                if (await native.ReloadAsync(ct) is not null) throw new InvalidOperationException("Could not restore the private runtime after the profile case.");
            });
        }

        foreach (var barrier in new[] { "before-settings-file", "after-settings-file" })
        {
            await guard.VerifyTrafficAsync(async (_, ct) =>
            {
                var settingsPaths = await CreatePathsAsync(barrier, ct);
                var stateDir = settingsPaths.GetStateDirectory();
                var store = new MihomoPublicationStore(settingsPaths, protection);
                var reader = new ProxySettingsReader(settingsPaths);
                var prior = await reader.GetAsync(ct);
                if (!prior.SystemProxyEnabled || prior.LogLevel == "warning")
                    throw new InvalidOperationException("The native settings case requires the original active system proxy and a distinct log level.");
                var priorWrites = Volatile.Read(ref nativeWrites);
                var priorRequests = Volatile.Read(ref relayWrites);
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                foreach (var argument in new[] { typeof(ProxyAuthenticatedPublicationChecks).Assembly.Location,
                    "runtime-proxy-direct-settings-crash", root, stateDir, address, socket, barrier })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the private settings writer.");
                MihomoDirectReload checkpoint;
                try
                {
                    while (!File.Exists(Path.Combine(stateDir, "settings-commit-barrier")))
                    {
                        if (child.HasExited) throw new InvalidOperationException($"The direct settings writer exited before {barrier}. Exit={child.ExitCode}.");
                        await Task.Delay(30, ct);
                    }
                    checkpoint = (await store.ReadAsync(ct)).DirectReload
                        ?? throw new InvalidOperationException("The acknowledged settings checkpoint was not durable.");
                    var expected = barrier == "before-settings-file" ? prior : prior with { LogLevel = "warning" };
                    if (checkpoint.ConfirmedSettings is null || await reader.GetAsync(ct) != expected
                        || await ReadLevelAsync(ct) != "warning" || Volatile.Read(ref nativeWrites) != priorWrites + 1
                        || Volatile.Read(ref relayWrites) != priorRequests + 1
                        || (await File.ReadAllLinesAsync(Path.Combine(stateDir, "host-applied"), ct)).Length != 1)
                        throw new InvalidOperationException($"Unexpected settings side effects at {barrier}.");
                    child.Kill();
                    await child.WaitForExitAsync(ct);
                    if (child.ExitCode == 0) throw new InvalidOperationException("The direct settings writer did not crash.");
                }
                finally { await StopAsync(child); }

                var reopened = new MihomoPublicationStore(settingsPaths, protection);
                var noHost = MihomoPublicationTransactionChecks.Stub<IHostSystemProxyService>(
                    _ => throw new InvalidOperationException("Local settings recovery must not dispatch the host proxy."));
                var files = new MihomoSettingsPublicationFiles(settingsPaths, noHost);
                var services = new ServiceCollection().AddSingleton(reopened).AddSingleton(files)
                    .AddSingleton<MihomoPublicationRecoveryService>().AddLogging();
                await using var provider = services.BuildServiceProvider();
                var recovery = provider.GetRequiredService<MihomoPublicationRecoveryService>();
                var named = new MihomoRuntimeRecoveryService(reopened, null!, null!, null!, null!, settingsFiles: files);
                var settingsFile = Path.Combine(stateDir, "mihomo-settings.json");
                var beforeCommit = await File.ReadAllBytesAsync(settingsFile, ct);
                var lastWrite = File.GetLastWriteTimeUtc(settingsFile);
                if (await recovery.ReconcileAsync(ct) is not null || await reopened.ReadPendingIdentityAsync(ct) is not null
                    || await named.RetryAsync(checkpoint.OperationId, ct) != new RuntimeSwitchResult(checkpoint.OperationId, RuntimeSwitchOutcome.Succeeded, null)
                    || await reader.GetAsync(ct) != (prior with { LogLevel = "warning" })
                    || Volatile.Read(ref nativeWrites) != priorWrites + 1 || Volatile.Read(ref relayWrites) != priorRequests + 1
                    || await ReadLevelAsync(ct) != "warning" || guard.RuntimePid != pid
                    || barrier == "after-settings-file" && (!(await File.ReadAllBytesAsync(settingsFile, ct)).SequenceEqual(beforeCommit)
                        || File.GetLastWriteTimeUtc(settingsFile) != lastWrite))
                    throw new InvalidOperationException($"Restart did not settle the original settings commit at {barrier}.");
                Console.WriteLine($"PASS MIHOMO DIRECT SETTINGS COMMIT RECOVERY: {barrier}, original {checkpoint.OperationId}, one native reload, one real Host acknowledgement, zero replay");
                await File.WriteAllBytesAsync(active, original, ct);
                if (await native.ReloadAsync(ct) is not null) throw new InvalidOperationException("Could not restore the private runtime after the settings case.");
            });
        }

        var newPort = ReserveProxyPort();
        foreach (var (name, drift) in new[]
        {
            ("tracked-host-reply", false), ("tracked-host-drift", true),
            ("tracked-host-disable", false), ("tracked-host-enable", false),
            ($"tracked-host-port-{newPort}", false), ($"tracked-host-port-{newPort}", true)
        })
        {
            await guard.VerifyTrafficAsync(async (_, ct) =>
            {
                const string environment = "/etc/environment";
                const string journal = "/var/lib/relaxkonos-system-proxy/recovery.json";
                var caseName = drift && name.StartsWith("tracked-host-port-", StringComparison.Ordinal) ? name + "-drift" : name;
                var settingsPaths = await CreatePathsAsync(caseName, ct);
                var stateDir = settingsPaths.GetStateDirectory();
                var store = new MihomoPublicationStore(settingsPaths, protection);
                var reader = new ProxySettingsReader(settingsPaths);
                var originalSettings = await reader.GetAsync(ct);
                var originalBypass = ReadEnvironment("no_proxy");
                var originalEndpoint = ReadEnvironment("http_proxy");
                var cleanupHost = new HostSystemProxyService(new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
                    NullLogger<UnixSocketPrivilegedOperationTransport>.Instance));
                var prior = originalSettings;
                if (name == "tracked-host-enable")
                {
                    prior = prior with { SystemProxyEnabled = false };
                    if (await cleanupHost.ApplyAsync(prior, originalSettings, enforce: false, ct) is not null || File.Exists(journal))
                        throw new InvalidOperationException("Could not establish the private disabled starting state.");
                    await File.WriteAllBytesAsync(Path.Combine(stateDir, "mihomo-settings.json"), JsonSerializer.SerializeToUtf8Bytes(prior), ct);
                }
                var priorEndpoint = ReadEnvironment("http_proxy");
                var candidate = TrackedCandidate(prior, name);
                var expectedEndpoint = candidate.SystemProxyEnabled ? $"http://127.0.0.1:{candidate.MixedPort}" : null;
                var priorWrites = Volatile.Read(ref nativeWrites);
                await using var hostRelay = new ReplyRelay("/run/rk-tracked-settings.sock", socket, ct,
                    static (kind, _) => kind == "LinuxSystemProxyApplyTracked");
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                foreach (var argument in new[] { typeof(ProxyAuthenticatedPublicationChecks).Assembly.Location,
                    "runtime-proxy-direct-settings-crash", root, stateDir, address, hostRelay.Path, name })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the tracked settings writer.");
                Guid id;
                try
                {
                    if (await Task.WhenAny(hostRelay.Committed.Task, child.WaitForExitAsync(ct)).WaitAsync(ct) != hostRelay.Committed.Task)
                        throw new InvalidOperationException($"Tracked settings writer exited before the real Helper reply. Exit={child.ExitCode}.");
                    id = await hostRelay.Committed.Task;
                    var pending = (await store.ReadAsync(ct)).DirectReload;
                    if (pending is not { TrackedHostPolicy: true } || pending.OperationId != id
                        || pending.ConfirmedSettings?.Candidate != candidate
                        || candidate.SystemProxyEnabled && ReadEnvironment("http_proxy") != expectedEndpoint
                        || !candidate.SystemProxyEnabled && (File.Exists(journal) || ReadEnvironment("http_proxy") == originalEndpoint)
                        || (name is "tracked-host-reply" or "tracked-host-drift")
                            && ReadEnvironment("no_proxy")?.Contains("tracked-fixture.invalid", StringComparison.Ordinal) != true
                        || await reader.GetAsync(ct) != prior || Volatile.Read(ref nativeWrites) != priorWrites + 1)
                        throw new InvalidOperationException("The actual Helper mutation did not precede the lost response and local commit.");
                    child.Kill();
                    await child.WaitForExitAsync(ct);
                    hostRelay.DropHeldResponse();
                }
                finally { await StopAsync(child); }

                if (drift)
                {
                    var target = LinuxSystemProxyOperations.EnvironmentTarget;
                    var read = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: target));
                    var edit = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentApply,
                        EnvironmentTarget: target, ExpectedRevision: read.HostEnvironment?.Revision,
                        EnvironmentChange: new([new("no_proxy", EnvironmentMutationKind.Set, "independent-fixture.invalid")], ConfirmHighImpact: true)));
                    if (!edit.Success) throw new InvalidOperationException("Could not introduce isolated host drift.");
                }
                var transport = new TrackedSettingsRecoveryTransport(new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
                    NullLogger<UnixSocketPrivilegedOperationTransport>.Instance), id);
                var host = new HostSystemProxyService(transport);
                var files = new MihomoSettingsPublicationFiles(settingsPaths, host);
                var reopened = new MihomoPublicationStore(settingsPaths, protection);
                var guardPass = new MihomoSystemProxyReconciler(settingsPaths, reader, secrets, host, reopened, settingsFiles: files);
                var named = new MihomoRuntimeRecoveryService(reopened, null!, null!, null!, null!, settingsFiles: files);
                var afterEffect = await File.ReadAllBytesAsync(environment, ct);
                var afterEffectTime = File.GetLastWriteTimeUtc(environment);
                if (await guardPass.ReconcileSystemProxyAsync(true, ct) is not null)
                    throw new InvalidOperationException("The restarted guard did not maintain the persisted host policy.");
                var receipt = await named.RetryAsync(id, ct);
                var state = await reopened.ReadAsync(ct);
                if (receipt.OperationId != id || transport.Observations != 1 || transport.GuardWrites != (drift ? 1 : 0)
                    || Volatile.Read(ref nativeWrites) != priorWrites + 1 || guard.RuntimePid != pid
                    || (drift
                        ? receipt.Outcome != RuntimeSwitchOutcome.RecoveryRequired || state.DirectReload?.HostGuardSuperseded != true
                            || await reader.GetAsync(ct) != prior || ReadEnvironment("http_proxy") != priorEndpoint
                        : receipt.Outcome != RuntimeSwitchOutcome.Succeeded || state.DirectReload is not null
                            || await reader.GetAsync(ct) != candidate || !(await File.ReadAllBytesAsync(environment, ct)).SequenceEqual(afterEffect)
                            || File.GetLastWriteTimeUtc(environment) != afterEffectTime))
                    throw new InvalidOperationException("Guard attributed a new host effect to the original operation or failed to commit the original.");
                if (candidate.MixedPort != prior.MixedPort)
                {
                    await VerifyProxyListenerAsync(candidate.MixedPort, ct);
                    if (await CanConnectToProxyAsync(prior.MixedPort, ct))
                        throw new InvalidOperationException("Mihomo still accepted new connections on the old listener after the port changed.");
                    if (drift)
                        Console.WriteLine($"OBSERVED MIHOMO GUARD PORT GAP: original {id}, guard selected the old closed listener while the candidate listener forwarded");
                }
                else if (name is "tracked-host-enable" or "tracked-host-disable")
                    await VerifyProxyListenerAsync(candidate.MixedPort, ct);
                Console.WriteLine($"PASS MIHOMO TRACKED HOST SETTINGS: {caseName}, original {id}, {receipt.Outcome}, one original Helper mutation, {transport.GuardWrites} separate guard calls");
                await File.WriteAllBytesAsync(active, original, ct);
                if (await native.ReloadAsync(ct) is not null) throw new InvalidOperationException("Could not restore the private runtime.");
                if ((!drift || candidate.MixedPort != prior.MixedPort)
                    && await cleanupHost.ApplyAsync(originalSettings, candidate, enforce: false, ct) is not null)
                    throw new InvalidOperationException("Could not restore the private proxy fixture.");
                if (ReadEnvironment("no_proxy") != originalBypass || ReadEnvironment("http_proxy") != originalEndpoint)
                    throw new InvalidOperationException("The original private system proxy policy was not restored.");
            });
        }

        static string? ReadEnvironment(string name)
        {
            var observed = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentRead,
                EnvironmentTarget: LinuxSystemProxyOperations.EnvironmentTarget));
            return observed.HostEnvironment?.Values.SingleOrDefault(value => value.Name == name)?.Value;
        }

        async Task<DirectReloadFixturePaths> CreatePathsAsync(string name, CancellationToken ct)
        {
            var state = Directory.CreateDirectory(Path.Combine(root, "direct-reload", name)).FullName;
            var source = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
            await File.WriteAllBytesAsync(Path.Combine(state, "mihomo-settings.json"), await File.ReadAllBytesAsync(source, ct), ct);
            return new(state);
        }

        Task<string?> ReadLevelAsync(CancellationToken ct) => ReadConfigAsync("log-level", ct);

        async Task<string?> ReadConfigAsync(string property, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "configs");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await secrets.GetOrCreateAsync(ct));
            using var response = await upstream.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return body.RootElement.GetProperty(property).GetString();
        }
    }

    internal static async Task ExecuteDirectSettingsCommitFixtureAsync(string root, string state, string relay,
        string socket, string barrier, CancellationToken ct)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        var paths = new DirectReloadFixturePaths(state);
        var protection = ManagedServiceChecks.OpenProtection(root);
        var secrets = new DataProtectionProxyControllerSecretStore(
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.Combine(root, "full-server") }, protection);
        using var controller = new MihomoControllerClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }), secrets,
            new() { Endpoint = new(relay + "/") });
        var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var armed = false;
        var host = new HostSystemProxyService(new SettingsHostAcknowledgement(transport, () =>
        {
            File.AppendAllText(Path.Combine(state, "host-applied"), "1\n");
            armed = true;
        }));
        var settingsFile = Path.Combine(state, "mihomo-settings.json");
        var publication = new MihomoPublicationStore(new DirectReloadFixturePaths(state, () =>
        {
            if (armed && barrier == "after-settings-file" && ProxySettingsReader.Decode(File.ReadAllBytes(settingsFile)).LogLevel == "warning")
                Block();
        }), protection);
        var files = new MihomoSettingsPublicationFiles(new DirectReloadFixturePaths(state, () =>
        {
            if (armed && barrier == "before-settings-file") Block();
        }), host);
        var writer = new MihomoSettingsService(paths, controller, secrets, new() { Endpoint = new("http://127.0.0.1:9090/") },
            host, publication, files);
        var prior = await new ProxySettingsReader(paths).GetAsync(ct);
        var candidate = barrier.StartsWith("tracked-host-", StringComparison.Ordinal) ? TrackedCandidate(prior, barrier)
            : prior with { LogLevel = "warning" };
        var problem = await writer.UpdateAsync(new(candidate.SystemProxyEnabled, candidate.AllowLan, candidate.DnsEnabled, candidate.Ipv6Enabled,
            candidate.UnifiedDelay, candidate.LogLevel, candidate.MixedPort, candidate.AllowInsecureSubscriptionSources,
            candidate.SystemProxyHost, candidate.Tun, candidate.SystemProxy), ct);
        throw new InvalidOperationException($"The direct settings writer returned before the barrier. Problem={problem}.");

        void Block()
        {
            using (File.Create(Path.Combine(state, "settings-commit-barrier"))) { }
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static ProxySettingsDto TrackedCandidate(ProxySettingsDto previous, string variant)
    {
        var candidate = previous with { LogLevel = "warning" };
        if (variant == "tracked-host-enable") return candidate with { SystemProxyEnabled = true };
        if (variant == "tracked-host-disable") return candidate with { SystemProxyEnabled = false };
        const string portPrefix = "tracked-host-port-";
        if (variant.StartsWith(portPrefix, StringComparison.Ordinal))
            return candidate with { MixedPort = int.Parse(variant[portPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture) };
        var options = previous.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
        return candidate with { SystemProxy = options with { BypassList = options.BypassList.TrimEnd(',', ';') + ",tracked-fixture.invalid" } };
    }

    private static int ReserveProxyPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectToProxyAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback, port, ct); return true; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
    }

    private static async Task VerifyProxyListenerAsync(int proxyPort, CancellationToken ct)
    {
        using var origin = new TcpListener(IPAddress.Parse("93.184.216.34"), 0);
        origin.Start();
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}") });
        var response = client.GetStringAsync($"http://93.184.216.34:{((IPEndPoint)origin.LocalEndpoint).Port}/", ct);
        using var connection = await origin.AcceptTcpClientAsync(ct);
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nproxy-ok"u8.ToArray(), ct);
        if (await response != "proxy-ok") throw new InvalidOperationException("The new Mihomo listener did not forward the origin response.");
    }

    private sealed class TrackedSettingsRecoveryTransport(IPrivilegedOperationTransport inner, Guid original) : IPrivilegedOperationTransport
    {
        internal int Observations { get; private set; }
        internal int GuardWrites { get; private set; }
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct)
        {
            if (request.Operation == PrivilegedOperationKind.LinuxSystemProxyReceipt && request.OperationId == original) Observations++;
            else if (request.Operation == PrivilegedOperationKind.LinuxSystemProxyApply && request.OperationId != original) GuardWrites++;
            else throw new InvalidOperationException("Recovery attempted a new tracked effect or queried a different ID.");
            return inner.ExecuteAsync(request, ct);
        }
    }

    private sealed class SettingsHostAcknowledgement(IPrivilegedOperationTransport inner, Action acknowledge) : IPrivilegedOperationTransport
    {
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct)
        {
            var result = await inner.ExecuteAsync(request, ct);
            if (request.Operation == PrivilegedOperationKind.LinuxSystemProxyApply && result.Success) acknowledge();
            return result;
        }
    }

    internal static async Task<string?> ExecuteDirectReloadFixtureAsync(string root, string state, string relay, CancellationToken ct)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        var paths = new DirectReloadFixturePaths(state);
        var protection = ManagedServiceChecks.OpenProtection(root);
        var publication = new RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore(paths, protection);
        var secrets = new RelaxKonOS.Server.Proxy.Mihomo.DataProtectionProxyControllerSecretStore(
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.Combine(root, "full-server") }, protection);
        using var client = new RelaxKonOS.Server.Proxy.Mihomo.MihomoControllerClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }),
            secrets, new() { Endpoint = new(relay + "/") });
        var host = MihomoPublicationTransactionChecks.Stub<RelaxKonOS.Server.Proxy.Platform.IHostSystemProxyService>(
            _ => throw new InvalidOperationException("Unknown reload must not dispatch host proxy changes."));
        var settings = new RelaxKonOS.Server.Proxy.Mihomo.MihomoSettingsService(paths, client, secrets,
            new() { Endpoint = new("http://127.0.0.1:9090/") }, host, publication, new(paths, host));
        var previous = await new RelaxKonOS.Server.Proxy.ProxySettingsReader(paths).GetAsync(ct);
        return await settings.UpdateAsync(new(false, previous.AllowLan, previous.DnsEnabled, previous.Ipv6Enabled,
            previous.UnifiedDelay, "warning", previous.MixedPort, previous.AllowInsecureSubscriptionSources,
            previous.SystemProxyHost, previous.Tun, previous.SystemProxy), ct);
    }

    internal static async Task ExecuteDirectProfileFixtureAsync(string root, string state, string relay, string binary,
        Guid profileId, string barrier, CancellationToken ct)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        var paths = new DirectReloadFixturePaths(state);
        var protection = ManagedServiceChecks.OpenProtection(root);
        var publication = new MihomoPublicationStore(paths, protection);
        var secrets = new DataProtectionProxyControllerSecretStore(
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.Combine(root, "full-server") }, protection);
        using var controller = new MihomoControllerClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }), secrets,
            new() { Endpoint = new(relay + "/") });
        var repository = await MihomoProfilePublicationChecks.CreateAsync(state);
        var profiles = new ProfileCommitBarrier(repository, Path.Combine(state, "commit-barrier"), barrier == "after-sqlite");
        var engine = new MihomoEngine(controller, new NativeProfileValidator(binary), paths);
        var service = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), profiles, secrets,
            new() { Endpoint = new("http://127.0.0.1:9090/") }, publication, new ProxySettingsReader(paths));
        var result = await service.ApplyAsync(profileId, "mode: rule\nrules:\n  - MATCH,DIRECT\n", ct);
        throw new InvalidOperationException($"Direct profile publication exited before the commit barrier. Problem={result}.");
    }

    private sealed class NativeProfileValidator(string binary) : IMihomoConfigurationValidator
    {
        public async Task<string?> ValidateAsync(string configurationPath, CancellationToken ct)
        {
            var start = new ProcessStartInfo(binary) { UseShellExecute = false };
            foreach (var argument in new[] { "-t", "-d", Path.GetDirectoryName(binary)!, "-f", configurationPath })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not run native profile validation.");
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? null : ProxyProblemCodes.ConfigInvalid;
        }
    }

    private sealed class ProfileCommitBarrier(IProxyProfileRepository inner, string marker, bool afterCommit) : IProxyProfileRepository
    {
        public Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<ProxyProfileDto?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<ProxyProfileDto?> GetActiveAsync(CancellationToken ct) => inner.GetActiveAsync(ct);
        public Task<ProxyProfileDto> UpsertAsync(Guid? id, string name, string engineId, long? revision, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProxyProfileDto?> SetActiveAsync(Guid id, CancellationToken ct, MihomoSubscriptionRefresh? refresh = null) => throw new NotSupportedException();
        public Task ValidatePublicationAsync(MihomoProfilePublication selection, CancellationToken ct) => inner.ValidatePublicationAsync(selection, ct);
        public async Task CommitPublicationAsync(Guid operationId, MihomoProfilePublication selection, string identity, CancellationToken ct)
        {
            if (afterCommit) await inner.CommitPublicationAsync(operationId, selection, identity, ct);
            await File.WriteAllTextAsync(marker, operationId.ToString("D"), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class DirectReloadFixturePaths(string state, Action? beforeStateAccess = null) : RelaxKonOS.Server.Proxy.IProxyPlatformPaths
    {
        private readonly RelaxKonOS.Server.Proxy.Platform.ProxyPlatformPaths native = new();
        public string GetStateDirectory() { beforeStateAccess?.Invoke(); return state; }
        public string GetProtectedConfigurationDirectory() => native.GetProtectedConfigurationDirectory();
        public string GetSanitizedLogDirectory() => native.GetSanitizedLogDirectory();
        public string GetEngineVersionsDirectory(string engineId) => native.GetEngineVersionsDirectory(engineId);
        public string GetEngineDataDirectory(string engineId) => native.GetEngineDataDirectory(engineId);
    }

    private static async Task VerifySubscriptionBodyResidencyAsync(ProxySystemdGuardProbe guard)
    {
        var originBuilder = WebApplication.CreateBuilder();
        originBuilder.Logging.ClearProviders();
        originBuilder.WebHost.UseUrls("http://93.184.216.34:0");
        await using var origin = originBuilder.Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        origin.MapGet("/feed", async (HttpContext context) =>
        {
            await context.Response.WriteAsync("vless://test-id@", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("example.test:443#fixture", context.RequestAborted);
        });
        await origin.StartAsync();
        var endpoint = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var manager = WebApplication.CreateBuilder();
        new RelaxKonOS.Server.Startup.Modules.ProxyModule().ConfigureServices(manager,
            RelaxKonOS.Server.Startup.ServerModuleContext.Create(manager.Configuration, manager.Environment, "memory"));
        manager.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxySettingsReader, SubscriptionProbeSettings>();
        await using var services = manager.Services.BuildServiceProvider();
        var downloader = services.GetRequiredService<RelaxKonOS.Server.Proxy.IProxySubscriptionDownloader>();
        var loaded = Channel.CreateUnbounded<TaskCompletionSource>();
        AssemblyLoadEventHandler observe = (_, args) =>
        {
            if (args.LoadedAssembly.GetName().Name != "RelaxKonOS.ServerModule.Proxy"
                || System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is not { Name: { } name } context
                || !name.StartsWith("proxy-subscription:", StringComparison.Ordinal)) return;
            var unloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Unloading += _ => unloading.TrySetResult();
            loaded.Writer.TryWrite(unloading);
        };
        AppDomain.CurrentDomain.AssemblyLoad += observe;
        try
        {
            await guard.VerifyTrafficAsync(async (_, ct) =>
            {
                var body = downloader.DownloadAsync(endpoint + "/feed", ProxySubscriptionDownloadRoute.Direct, ct);
                var generation = await loaded.Reader.ReadAsync(ct);
                if (body.IsCompleted || generation.Task.IsCompleted)
                    throw new InvalidOperationException("The subscription generation did not own the live response body.");
                release.TrySetResult();
                var output = await body;
                await generation.Task.WaitAsync(ct);
                var next = await downloader.DownloadAsync(endpoint + "/feed", ProxySubscriptionDownloadRoute.Direct, ct);
                var awakened = await loaded.Reader.ReadAsync(ct);
                await awakened.Task.WaitAsync(ct);
                if (ReferenceEquals(awakened, generation) || next.Content != output.Content
                    || !output.Content.Contains("example.test", StringComparison.Ordinal))
                    throw new InvalidOperationException("A subsequent real subscription did not wake an equivalent execution generation.");
            });
            Console.WriteLine("PASS REAL SUBSCRIPTION BODY RESIDENCY: production downloader streamed, unloaded and woke while the original proxy connection remained live");
        }
        finally
        {
            release.TrySetResult();
            AppDomain.CurrentDomain.AssemblyLoad -= observe;
        }
    }

    private sealed class SubscriptionProbeSettings : RelaxKonOS.Server.Proxy.IProxySettingsReader
    {
        public Task<ProxySettingsDto> GetAsync(CancellationToken ct) =>
            Task.FromResult(RelaxKonOS.Server.Proxy.ProxySettingsReader.Defaults with { AllowInsecureSubscriptionSources = true });
    }

    private static async Task VerifyManagedPublicationShutdownAsync(string root, string assembly, string socket, LoginRequest login,
        Func<RuntimeArchiveState> nativeState, ProxyFullServerTrafficProbe traffic, ProxyServerSecret retained,
        StoredCredential previous, Func<Guid, Task<ProxyServerPublicationReceipt>> readReceipt)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = budget.Token;
        var store = ManagedServiceChecks.OpenFullServerStore(root);
        var state = await store.ReadAsync(ct);
        var next = previous with { Id = Guid.NewGuid(), Name = "shutdown-fixture", Value = Guid.NewGuid().ToString("D"), ExpiresAt = null };
        await store.WriteAsync(state with { Configuration = state.Configuration with { Revision = state.Configuration.Revision + 1 },
            Credentials = [.. state.Credentials, next] }, ct);
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Certificate__StorageRoot"] = Path.Combine(start.WorkingDirectory, "certificate-fixture");
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        var key = Guid.NewGuid().ToString("N");
        var revision = nativeState().Installation.StateRevision;
        await using var relay = new ReplyRelay("/run/relaxkonos-proxy-queue-drain.sock", socket, ct);
        start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = Start(start, onOutput: line =>
        {
            if (line.Contains("Application is shutting down", StringComparison.Ordinal)) stopping.TrySetResult();
        });
        try
        {
            using var client = await ConnectAsync(server, login, ct);
            var original = await SubmitAsync(client, ProxyServerRoutes.Apply, key, ct);
            if (await relay.Committed.Task.WaitAsync(ct) != original || (await ReadAsync(client, original, ct)).State != ProxyOperationState.Running
                || (await store.ReadAsync(ct)).PendingPublicationOperationId != original)
                throw new InvalidOperationException("Accepted management work did not retain its original HTTP receipt at the Helper barrier.");
            var receipt = await readReceipt(original).WaitAsync(ct);
            var pid = await ReadNativePidAsync();
            if (receipt.Outcome != ProxyServerPublicationOutcome.Committed || receipt.OperationId != original
                || nativeState().Installation.StateRevision != revision + 1)
                throw new InvalidOperationException("Missing the original committed management publication.");
            await SignalShutdownAsync(server, client, stopping.Task, ct);
            if (server.HasExited || (await store.ReadAsync(ct)).PendingPublicationOperationId != original)
                throw new InvalidOperationException("Host stop did not retain admitted management work.");
            using (await traffic.ForwardAsync(next.Value, ct)) { }
            relay.ReleaseHeldResponse();
            await server.WaitForExitAsync(ct);
            var final = await store.ReadAsync(ct);
            if (server.ExitCode != 0 || final.PendingPublication is not null
                || final.QueuedPublicationCompletions.GetValueOrDefault(original) != ProxyServerPublicationOutcome.Committed)
                throw new InvalidOperationException("The Host exited before completing the original queued publication.");

            using var restart = Start(start);
            try
            {
                using var restarted = await ConnectAsync(restart, login, ct);
                var operation = await ReadAsync(restarted, original, ct);
                if (operation.State != ProxyOperationState.Succeeded || operation.CompletedAt is null
                    || await SubmitAsync(restarted, ProxyServerRoutes.Apply, key, ct) != original
                    || await ReadAsync(restarted, original, ct) != operation || await readReceipt(original).WaitAsync(ct) != receipt
                    || nativeState().Installation.StateRevision != revision + 1 || await ReadNativePidAsync() != pid)
                    throw new InvalidOperationException("Restart changed or replayed the original management operation.");
                using var keptTraffic = await traffic.ForwardAsync(retained.Value, ct);
                using var newTraffic = await traffic.ForwardAsync(next.Value, ct);
                Console.WriteLine($"PASS FULL SERVER PROXY QUEUE SIGTERM DRAIN: original operation {original}, durable HTTP receipt and one native apply");
            }
            finally { await StopAsync(restart); }
        }
        finally
        {
            relay.ReleaseHeldResponse();
            await StopAsync(server);
        }
    }

    private static async Task VerifySafetyShutdownAsync(string root, string assembly, string socket, LoginRequest login,
        Func<RuntimeArchiveState> nativeState, ProxySystemdGuardProbe guard, ProxyFullServerTrafficProbe traffic,
        ProxyServerSecret retained, StoredCredential expiring, Func<Guid, Task<ProxyServerPublicationReceipt>> readReceipt)
    {
        var store = ManagedServiceChecks.OpenFullServerStore(root);
        var before = await store.ReadAsync(default);
        if (before.PendingPublication is not null || before.Published?.Credentials.Any(item => item.Id == expiring.Id) != true)
            throw new InvalidOperationException("The joint shutdown requires the original live grant and a settled publication.");
        using var oldConnection = await traffic.ForwardAsync(expiring.Value, default);
        var baseline = nativeState();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloads = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var relay = builder.Build();
        using var upstream = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { BaseAddress = new Uri("http://127.0.0.1:9090/") };
        relay.Run(async context =>
        {
            var reload = HttpMethods.IsPut(context.Request.Method) && context.Request.Path == "/configs";
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
            if (reload)
            {
                Interlocked.Increment(ref reloads);
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
            }
            try
            {
                using var response = await upstream.SendAsync(request, context.RequestAborted);
                var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/configs"
                    && response.IsSuccessStatusCode && Volatile.Read(ref reloads) != 0 && !ready.Task.IsCompleted)
                {
                    using var observed = JsonDocument.Parse(body);
                    if (!observed.RootElement.TryGetProperty("tun", out var tun) || tun.GetProperty("enable").ValueKind == JsonValueKind.False)
                    {
                        ready.TrySetResult();
                        await release.Task.WaitAsync(context.RequestAborted);
                    }
                }
                context.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
                await context.Response.Body.WriteAsync(body, context.RequestAborted);
            }
            catch (HttpRequestException) when (reload) { context.Abort(); }
        });
        await relay.StartAsync();
        var endpoint =
            relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
        var mihomoPid = guard.RuntimePid;
        await guard.VerifyTrafficAsync(async (_, ct) =>
        {
            var originalProxy = guard.ReadOwnershipWitness();
            var tunOperation = Guid.Empty;
            // The original connection was established under a live grant. Let expiry commit on
            // the original route before creating the independent TUN recovery marker.
            await store.WriteAsync(before with { Credentials = before.Credentials.Select(item =>
                item.Id == expiring.Id ? item with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } : item).ToArray() }, ct);
            var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
            start.Environment["Identity__LinuxPamTransport"] = "helper";
            start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            start.Environment["Proxy__Mihomo__Controller__TimeoutSeconds"] = "30";
            start.Environment["Proxy__Mihomo__Controller__Endpoint"] = endpoint;

            var applies = 0;
            await using var expiryRelay = new ReplyRelay("/run/relaxkonos-expiry-drain.sock", socket, ct, (kind, _) =>
            {
                if (kind != "ProxyServerApply") return false;
                Interlocked.Increment(ref applies);
                return true;
            });
            start.Environment["PrivilegedHelper__SocketPath"] = expiryRelay.Path;
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = Start(start, onError: line => Console.WriteLine($"Joint Proxy Server stderr: {line}"), onOutput: line =>
            {
                if (line.Contains("Application is shutting down", StringComparison.Ordinal)) stopping.TrySetResult();
                if (line.Contains("TUN was disabled", StringComparison.Ordinal) && line.Contains(tunOperation.ToString(), StringComparison.OrdinalIgnoreCase))
                    restored.TrySetResult();
            });
            try
            {
                using var client = await ConnectAsync(process, login, ct);
                var expiryOperation = await expiryRelay.Committed.Task.WaitAsync(ct);
                var pending = await store.ReadAsync(ct);
                var committed = nativeState();
                var receipt = await readReceipt(expiryOperation).WaitAsync(ct);
                var singBoxPid = await ReadNativePidAsync();
                if (pending.PendingPublicationOperationId != expiryOperation || pending.PendingQueuedPublication
                    || pending.PendingPublication is not { } candidate || candidate.Credentials.Any(item => item.Id == expiring.Id)
                    || pending.Published?.Credentials.Any(item => item.Id == expiring.Id) != true
                    || receipt.OperationId != expiryOperation || receipt.Outcome != ProxyServerPublicationOutcome.Committed
                    || receipt.PreviousHash != baseline.ConfigurationIdentity || receipt.CandidateHash != committed.ConfigurationIdentity
                    || committed.Installation.StateRevision != baseline.Installation.StateRevision + 1)
                    throw new InvalidOperationException("The natural expiry did not leave its original committed receipt and pending application state.");
                tunOperation = await guard.PrepareTunShutdownAsync(root);
                await ready.Task.WaitAsync(ct);
                await guard.VerifyTunShutdownBoundaryAsync(tunOperation, waitingForReply: true, ct);
                await ProxyFullServerTrafficProbe.ClosedAsync(oldConnection, ct);
                await traffic.DeniedAsync(expiring.Value, ct);
                using (await traffic.ForwardAsync(retained.Value, ct)) { }
                guard.SetCompetingValue();
                await SignalShutdownAsync(process, client, stopping.Task, ct);
                if (process.HasExited || restored.Task.IsCompleted || (await store.ReadAsync(ct)).PendingPublicationOperationId != expiryOperation
                    || Volatile.Read(ref reloads) != 1 || Volatile.Read(ref applies) != 1)
                    throw new InvalidOperationException("SIGTERM lost one of the two in-flight safety operations.");
                await guard.VerifyTunShutdownBoundaryAsync(tunOperation, waitingForReply: true, ct);

                expiryRelay.ReleaseHeldResponse();
                while ((await store.ReadAsync(ct)).PendingPublicationOperationId == expiryOperation)
                {
                    if (process.HasExited) throw new InvalidOperationException("The Server exited before expiry persisted.");
                    await Task.Delay(40, ct);
                }
                await VerifyExpiryTerminalAsync();
                if (process.HasExited || restored.Task.IsCompleted)
                    throw new InvalidOperationException("Completing expiry released the independent TUN responsibility.");
                await guard.VerifyTunShutdownBoundaryAsync(tunOperation, waitingForReply: true, ct);
                if (!guard.HasCompetingValue())
                    throw new InvalidOperationException("The guard wrote while TUN owned the shared publication gate.");

                release.TrySetResult();
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0 || !restored.Task.IsCompleted)
                    throw new InvalidOperationException("The Server did not confirm and drain the original TUN recovery.");
                await guard.VerifyTunShutdownBoundaryAsync(tunOperation, waitingForReply: false, ct);
                // If the original guard was already admitted when TUN released the gate, its
                // completed repair is authoritative. Restart must observe it without writing again.
                var repairedBeforeRestart = !guard.HasCompetingValue();
                var proxyBeforeRestart = repairedBeforeRestart ? guard.ReadOwnershipWitness() : originalProxy;

                await using var restartRelay = new ReplyRelay("/run/relaxkonos-joint-safety-restart.sock", socket, ct, (kind, _) =>
                {
                    if (kind == "ProxyServerApply") Interlocked.Increment(ref applies);
                    return kind == "LinuxSystemProxyApply";
                });
                start.Environment["PrivilegedHelper__SocketPath"] = restartRelay.Path;
                using var restart = Start(start);
                try
                {
                    var guardOperation = await restartRelay.Committed.Task.WaitAsync(ct);
                    var proxy = guard.ReadOwnershipWitness();
                    if (proxy.Environment != originalProxy.Environment || proxy.Journal != originalProxy.Journal
                        || proxy.EnvironmentWrite == originalProxy.EnvironmentWrite
                        || (repairedBeforeRestart && proxy != proxyBeforeRestart))
                        throw new InvalidOperationException("Guard recovery lost the original ownership or repeated an already committed repair.");
                    restartRelay.ReleaseHeldResponse();
                    using var restarted = await ConnectAsync(restart, login, ct);
                    await VerifyExpiryTerminalAsync();
                    await guard.VerifyTunShutdownBoundaryAsync(tunOperation, waitingForReply: false, ct);
                    if (Volatile.Read(ref reloads) != 1 || guard.RuntimePid != mihomoPid || guard.ReadOwnershipWitness() != proxy)
                        throw new InvalidOperationException("Restart changed the native proxy or repeated TUN teardown.");
                    await traffic.DeniedAsync(expiring.Value, ct);
                    using var forwarded = await traffic.ForwardAsync(retained.Value, ct);
                    var guardRepair = repairedBeforeRestart ? "drained before restart" : "completed on restart";
                    Console.WriteLine($"PASS FULL SERVER JOINT PROXY SAFETY: expiry {expiryOperation}, TUN {tunOperation}, guard {guardOperation} ({guardRepair}); one apply, one reload, original receipt and traffic");
                }
                finally
                {
                    restartRelay.ReleaseHeldResponse();
                    await StopAsync(restart);
                }

                async Task VerifyExpiryTerminalAsync()
                {
                    var final = await store.ReadAsync(ct);
                    var schedule = await store.ReadExpiryScheduleAsync(ct);
                    if (final.PendingPublication is not null || final.PendingPublicationOperationId is not null
                        || final.Published?.Credentials.Any(item => item.Id == expiring.Id) != false
                        || final.Published.Credentials.Any(item => item.Id == retained.Id) != true
                        || schedule.RequiresPass(DateTimeOffset.UtcNow) || await readReceipt(expiryOperation).WaitAsync(ct) != receipt
                        || nativeState().Installation.StateRevision != committed.Installation.StateRevision
                        || await ReadNativePidAsync() != singBoxPid || Volatile.Read(ref applies) != 1)
                        throw new InvalidOperationException("Expiry did not retain its original receipt, terminal state and live runtime.");
                }
            }
            finally
            {
                expiryRelay.ReleaseHeldResponse();
                release.TrySetResult();
                await StopAsync(process);
            }
        }, TimeSpan.FromSeconds(65));
    }

    private static async Task VerifySystemProxyShutdownAsync(string root, string assembly, string socket, LoginRequest login,
        Func<RuntimeArchiveState> nativeState, ProxySystemdGuardProbe guard)
    {
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        var nativeBefore = nativeState();
        var runtimePid = guard.RuntimePid;
        await guard.VerifyTrafficAsync(async (_, ct) =>
        {
            var original = guard.ReadOwnershipWitness();
            guard.SetCompetingValue();
            await using var relay = new ReplyRelay("/run/relaxkonos-system-proxy-drain.sock", socket, ct,
                static (kind, _) => kind == "LinuxSystemProxyApply");
            start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = Start(start, onOutput: line =>
            {
                if (line.Contains("Application is shutting down", StringComparison.Ordinal)) stopping.TrySetResult();
            });
            try
            {
                var operation = await relay.Committed.Task.WaitAsync(ct);
                using var client = await ConnectAsync(process, login, ct);
                var committed = guard.ReadOwnershipWitness();
                if (committed.Environment != original.Environment || committed.Journal != original.Journal
                    || committed.EnvironmentWrite == original.EnvironmentWrite || guard.RuntimePid != runtimePid)
                    throw new InvalidOperationException("The original guard operation did not restore the owned environment before replying.");
                await SignalShutdownAsync(process, client, stopping.Task, ct);
                if (process.HasExited || guard.ReadOwnershipWitness() != committed)
                    throw new InvalidOperationException("SIGTERM released the guard before its real Helper reply.");
                relay.ReleaseHeldResponse();
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0 || guard.ReadOwnershipWitness() != committed)
                    throw new InvalidOperationException("The Server did not drain the accepted system proxy write.");

                // Startup is allowed a fresh reconciliation. The Helper must find the existing
                // environment already owned and leave both durable files untouched.
                await using var restartRelay = new ReplyRelay("/run/relaxkonos-system-proxy-restart.sock", socket, ct,
                    static (kind, _) => kind == "LinuxSystemProxyApply");
                start.Environment["PrivilegedHelper__SocketPath"] = restartRelay.Path;
                using var restart = Start(start);
                try
                {
                    var next = await restartRelay.Committed.Task.WaitAsync(ct);
                    if (next == operation || guard.ReadOwnershipWitness() != committed)
                        throw new InvalidOperationException("Restart replayed the original system proxy side effect.");
                    restartRelay.ReleaseHeldResponse();
                    using var restarted = await ConnectAsync(restart, login, ct);
                    if (guard.ReadOwnershipWitness() != committed || guard.RuntimePid != runtimePid
                        || nativeState().Installation.StateRevision != nativeBefore.Installation.StateRevision
                        || nativeState().ConfigurationIdentity != nativeBefore.ConfigurationIdentity)
                        throw new InvalidOperationException("Restart changed proxy ownership, the runtime or the inbound publication.");
                    Console.WriteLine($"PASS FULL SERVER SYSTEM PROXY SIGTERM DRAIN: original apply {operation}, restart observation {next}, no replayed write");
                }
                finally
                {
                    restartRelay.ReleaseHeldResponse();
                    await StopAsync(restart);
                }
            }
            finally
            {
                relay.ReleaseHeldResponse();
                await StopAsync(process);
            }
        }, TimeSpan.FromSeconds(40));
    }

    private static async Task VerifyMihomoObservationCancellationAsync(string root, string assembly, string socket, LoginRequest login,
        Func<RuntimeArchiveState> nativeState, ProxySystemdGuardProbe guard)
    {
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var relay = builder.Build();
        using var upstream = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { BaseAddress = new Uri("http://127.0.0.1:9090/") };
        var responseReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outboundCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdFirst = 1;
        relay.MapGet("/{**path}", async (HttpContext context) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, context.Request.Path + context.Request.QueryString);
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
            using var response = await upstream.SendAsync(request, context.RequestAborted);
            var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            if (context.Request.Path == "/connections" && response.IsSuccessStatusCode && Interlocked.Exchange(ref holdFirst, 0) == 1)
            {
                using var registration = context.RequestAborted.Register(() => outboundCancelled.TrySetResult());
                responseReady.TrySetResult(body);
                await outboundCancelled.Task;
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        });
        await relay.StartAsync();
        start.Environment["Proxy__Mihomo__Controller__Endpoint"] =
            relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
        var revision = nativeState().Installation.StateRevision;
        var pid = guard.RuntimePid;
        await guard.VerifyTrafficAsync(async (port, ct) =>
        {
            using var server = Start(start);
            try
            {
                using var client = await ConnectAsync(server, login, ct);
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pending = client.GetAsync(ProxyApiRoutes.Connections, caller.Token);
                var snapshot = await responseReady.Task.WaitAsync(ct);
                var destination = $"93.184.216.34:{port}";
                using var native = JsonDocument.Parse(snapshot);
                var original = native.RootElement.GetProperty("connections").EnumerateArray().Single(connection =>
                    connection.GetProperty("metadata").GetProperty("destinationPort").ToString() == port.ToString()).GetProperty("id").GetString();
                if (await ReadLeasesAsync() != 1)
                    throw new InvalidOperationException("The real controller response was held without a Proxy work lease.");
                caller.Cancel();
                try { using var unexpected = await pending; throw new InvalidOperationException("Expected client cancellation."); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                await outboundCancelled.Task.WaitAsync(ct);
                while (await ReadLeasesAsync() != 0) await Task.Delay(25, ct);
                var connections = await client.GetFromJsonAsync<ProxyConnectionDto[]>(ProxyApiRoutes.Connections, RelaxKonOSJsonOptions.Default, ct);
                if (connections?.SingleOrDefault(item => item.Destination == destination)?.Id != original
                    || guard.RuntimePid != pid || nativeState().Installation.StateRevision != revision)
                    throw new InvalidOperationException("Cancelling an observation changed the original connection or native authority.");
                Console.WriteLine("PASS AUTHENTICATED MIHOMO OBSERVATION CANCELLATION: real controller, active work lease, original connection and publication");

                async Task<int> ReadLeasesAsync()
                {
                    var module = await client.GetFromJsonAsync<ServerModuleDto>(
                        ServerModuleManagementRoutes.Detail.Replace("{moduleId}", "relaxkonos.proxy", StringComparison.Ordinal),
                        RelaxKonOSJsonOptions.Default, ct);
                    return module?.Residency?.ActiveLeases
                        ?? throw new InvalidOperationException("Proxy residency did not expose its work count.");
                }
            }
            finally { await StopAsync(server); }
        });
    }

    private static async Task VerifyMihomoOverviewRestartAsync(string root, string assembly, string socket, LoginRequest login,
        Func<RuntimeArchiveState> nativeState, ProxySystemdGuardProbe guard)
    {
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Certificate__StorageRoot"] = Path.Combine(start.WorkingDirectory, "certificate-fixture");
        var revision = nativeState().Installation.StateRevision;
        var originalPid = guard.RuntimePid;
        await guard.VerifyTrafficAsync(async (port, ct) =>
        {
            async Task<string> ObserveAsync(HttpClient client)
            {
                using var anonymous = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = client.BaseAddress };
                using var rejected = await anonymous.GetAsync(ProxyApiRoutes.Overview, ct);
                using var rejectedConnections = await anonymous.GetAsync(ProxyApiRoutes.Connections, ct);
                if (rejected.StatusCode != HttpStatusCode.Unauthorized || rejectedConnections.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Anonymous clients must not see the Mihomo overview or connection details.");
                var overview = await client.GetFromJsonAsync<ProxyOverviewDto>(ProxyApiRoutes.Overview, RelaxKonOSJsonOptions.Default, ct);
                var runtime = await client.GetFromJsonAsync<ProxyRuntimeDto>(ProxyApiRoutes.Runtime, RelaxKonOSJsonOptions.Default, ct);
                var connections = await client.GetFromJsonAsync<ProxyConnectionDto[]>(ProxyApiRoutes.Connections, RelaxKonOSJsonOptions.Default, ct);
                var target = connections?.SingleOrDefault(item => item.Destination == $"93.184.216.34:{port}");
                if (overview is null || !overview.Health.ControllerReachable || overview.Recovery.RecoveryRequired
                    || runtime != overview.Runtime
                    || target is null || overview.ActiveConnections != connections!.Length)
                    throw new InvalidOperationException("Production HTTP did not report the held Mihomo connection and current Core safety state.");
                return target.Id;
            }

            string original;
            using (var initial = Start(start))
            {
                try
                {
                    using var client = await ConnectAsync(initial, login, ct);
                    original = await ObserveAsync(client);
                    initial.Kill();
                    await initial.WaitForExitAsync(ct);
                    if (initial.ExitCode == 0) throw new InvalidOperationException("The original Server did not crash.");
                }
                finally { await StopAsync(initial); }
            }
            var parserEvents = Channel.CreateUnbounded<string>();
            start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location;
            using var restarted = Start(start, line =>
            {
                if (line.StartsWith(StartupHook.ParserEventPrefix, StringComparison.Ordinal))
                    parserEvents.Writer.TryWrite(line[StartupHook.ParserEventPrefix.Length..]);
            });
            try
            {
                using var client = await ConnectAsync(restarted, login, ct);
                if (await ObserveAsync(client) != original || guard.RuntimePid != originalPid
                    || nativeState().Installation.StateRevision != revision)
                    throw new InvalidOperationException("Server restart changed the original connection or publication revision.");

                var loaded = await parserEvents.Reader.ReadAsync(ct);
                if (!loaded.StartsWith("load ", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Expected the first parser generation to load, observed {loaded}.");
                var unloaded = await parserEvents.Reader.ReadAsync(ct);
                if (unloaded != $"unload {loaded[5..]}" || restarted.HasExited)
                    throw new InvalidOperationException($"Expected idle unload in the same Server process, observed {unloaded}.");
                if (await ObserveAsync(client) != original || guard.RuntimePid != originalPid
                    || nativeState().Installation.StateRevision != revision || restarted.HasExited)
                    throw new InvalidOperationException("Parser wake changed the connection, publication revision or process.");
                var reloaded = await parserEvents.Reader.ReadAsync(ct);
                if (!reloaded.StartsWith("load ", StringComparison.Ordinal) || reloaded == loaded)
                    throw new InvalidOperationException($"Expected a new parser generation, observed {reloaded} after {loaded}.");
            }
            finally { await StopAsync(restarted); }
        }, TimeSpan.FromSeconds(55));
        Console.WriteLine("PASS AUTHENTICATED MIHOMO OVERVIEW RESTART: original held HTTP connection, authenticated count and details, same runtime and publication revision");
        Console.WriteLine("PASS AUTHENTICATED MIHOMO PARSER IDLE: same Server and held connection across parser unload and new generation");
    }

    private static async Task VerifyInFlightAsync(string root, string assembly, string socket, ISingBoxArchiveHost host,
        ProxyFullServerTrafficProbe traffic, ProxyServerSecret retained, StoredCredential kept, LoginRequest login,
        Func<RuntimeArchiveState> nativeState, Func<Task> stopHelper, Func<Task> restartHelper)
    {
        const string fault = "/run/relaxkonos-proxy-start";
        var armed = Path.Combine(fault, "armed");
        var blocked = Path.Combine(fault, "blocked");
        var store = ManagedServiceChecks.OpenFullServerStore(root);
        var old = await store.ReadAsync(default);
        var candidate = kept with { Id = Guid.NewGuid(), Name = "interrupted-fixture", Value = Guid.NewGuid().ToString("D") };
        await store.WriteAsync(old with { Configuration = old.Configuration with { Revision = old.Configuration.Revision + 1 },
            Credentials = [.. old.Credentials, candidate] }, default);
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
        start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Certificate__StorageRoot"] = Path.Combine(start.WorkingDirectory, "certificate-fixture");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = budget.Token;
        var baseline = nativeState();
        await host.SetBootAsync(true, ct);
        var key = Guid.NewGuid().ToString("N");
        Guid id;
        using (var initial = Start(start))
        {
            try
            {
                using var client = await ConnectAsync(initial, login, ct);
                await File.WriteAllTextAsync(armed, "private-start", ct);
                id = await SubmitAsync(client, ProxyServerRoutes.Apply, key, ct);
                while (!File.Exists(blocked))
                {
                    var current = await ReadAsync(client, id, ct);
                    if (current.State is not (ProxyOperationState.Queued or ProxyOperationState.Running))
                        throw new InvalidOperationException($"The original apply ended before native start: {current.ProblemCode}");
                    await Task.Delay(40, ct);
                }
                var pending = await store.ReadAsync(ct);
                var inFlight = nativeState();
                var observed = await host.ObserveAsync(ct);
                if (inFlight.ProxyPublication is not { Outcome: ProxyServerPublicationOutcome.InFlight } receipt
                    || receipt.OperationId != id || pending.PendingPublicationOperationId != id || !pending.PendingQueuedPublication
                    || inFlight.Installation.StateRevision != baseline.Installation.StateRevision || observed.Running || observed.BootEnabled != true
                    || observed.RegisteredConfigurationIdentity != receipt.CandidateHash || receipt.PreviousHash != baseline.ConfigurationIdentity)
                    throw new InvalidOperationException("Production Helper had no matching native candidate at the start boundary.");
                Console.WriteLine($"PASS AUTHENTICATED PROXY NATIVE BOUNDARY: {id}; candidate registered and service stopped before start");
                initial.Kill();
                await initial.WaitForExitAsync(ct);
                if (initial.ExitCode == 0) throw new InvalidOperationException("The original Server did not crash.");
                await stopHelper();
            }
            finally
            {
                await StopAsync(initial);
                File.Delete(armed);
            }
        }

        while (!File.Exists(Path.Combine(fault, "released"))) await Task.Delay(20, ct);
        await restartHelper();
        using var restarted = Start(start);
        try
        {
            using var client = await ConnectAsync(restarted, login, ct);
            if (await SubmitAsync(client, ProxyServerRoutes.Apply, key, ct) != id
                || (await ReadAsync(client, id, ct)).State != ProxyOperationState.Interrupted
                || nativeState().ProxyPublication?.Outcome != ProxyServerPublicationOutcome.InFlight)
                throw new InvalidOperationException("The original interrupted apply did not survive the two process exits.");
            var recoveryKey = Guid.NewGuid().ToString("N");
            if (await SubmitAsync(client, ProxyServerRoutes.Recovery, recoveryKey, ct) != id)
                throw new InvalidOperationException("Recovery must return the original authenticated publication ID.");
            var terminal = await WaitForTerminalAsync(client, id, ct);
            var compensated = nativeState();
            var stopped = await host.ObserveAsync(ct);
            var domain = await store.ReadAsync(ct);
            if (terminal.State != ProxyOperationState.Failed || terminal.ProblemCode != "proxy_server.config_apply_failed"
                || compensated.ProxyPublication is not { Outcome: ProxyServerPublicationOutcome.Compensated } proof || proof.OperationId != id
                || compensated.Installation.StateRevision != baseline.Installation.StateRevision
                || stopped.Running || stopped.BootEnabled != false || stopped.RegisteredConfigurationIdentity != baseline.ConfigurationIdentity
                || domain.PendingPublication is not null || domain.QueuedPublicationCompletions.GetValueOrDefault(id) != ProxyServerPublicationOutcome.Compensated
                || await SubmitAsync(client, ProxyServerRoutes.Recovery, recoveryKey, ct) != id
                || await SubmitAsync(client, ProxyServerRoutes.Apply, key, ct) != id || await ReadAsync(client, id, ct) != terminal)
                throw new InvalidOperationException("Original compensation did not restore configuration and close the authenticated receipt exactly once.");
            var startId = await SubmitAsync(client, ProxyServerRoutes.RuntimeActions + "/start", Guid.NewGuid().ToString("N"), ct);
            if ((await WaitForTerminalAsync(client, startId, ct)).State != ProxyOperationState.Succeeded)
                throw new InvalidOperationException("Authorized HTTP start failed after compensation.");
            using var retainedTraffic = await traffic.ForwardAsync(retained.Value, ct);
            using var keptTraffic = await traffic.ForwardAsync(kept.Value, ct);
            await traffic.DeniedAsync(candidate.Value, ct);
            Console.WriteLine($"PASS AUTHENTICATED PROXY QUEUE COMPENSATION: {id}; original Failed receipt, no apply replay, candidate grant rejected");
        }
        finally { await StopAsync(restarted); }
    }

    private static async Task<ProxyOperationDto> WaitForTerminalAsync(HttpClient client, Guid id, CancellationToken ct)
    {
        while (true)
        {
            var receipt = await ReadAsync(client, id, ct);
            if (receipt.State is ProxyOperationState.Succeeded or ProxyOperationState.Failed) return receipt;
            await Task.Delay(40, ct);
        }
    }

    internal static LoginRequest ReadLogin()
    {
        try
        {
            using var input = JsonDocument.Parse(Console.ReadLine() ?? "");
            var identifier = input.RootElement.GetProperty("identifier").GetString();
            var password = input.RootElement.GetProperty("password").GetString();
            if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(password))
                return new LoginRequest(identifier, password, ClientPlatformKind.Linux, "Isolated Proxy acceptance", "0.2.2");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new InvalidOperationException("Missing or malformed PAM fixture input.");
    }

    private static async Task<string> ReadNativePidAsync() => (await SingBoxOperations.RunAsync("/usr/bin/systemctl",
        ["show", LinuxSingBoxArchiveHost.Unit, "--property=MainPID", "--value"])).Trim();

    private static async Task SeedCertificateAsync(string contentRoot, ProxyServerInbound inbound, ProxyServerTlsMaterial tls)
    {
        var id = inbound.CertificateId!.Value;
        var path = Directory.CreateDirectory(Path.Combine(contentRoot, "certificate-fixture", id.ToString("D"))).FullName;
        await File.WriteAllTextAsync(Path.Combine(path, "nginx-fullchain.pem"), tls.CertificatePem);
        var key = Path.Combine(path, "nginx-private.key");
        await File.WriteAllTextAsync(key, tls.PrivateKeyPem);
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var now = DateTimeOffset.UtcNow;
        var metadata = new StoredCertificate(id, tls.Version, inbound.ServerName, [inbound.ServerName], CertificateChallengeType.WebRootHttp01,
            CertificateKeyAlgorithm.Rsa2048, null, null, null, now.AddMinutes(-10), now.AddMinutes(30), CertificateStatus.Active,
            now, now, null, null, null, null, null, CertificateKind.SelfSigned);
        await File.WriteAllTextAsync(Path.Combine(path, "current.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal static Process Start(ProcessStartInfo start, Action<string>? onError = null, Action<string>? onOutput = null)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the authenticated Server.");
        if (onError is not null)
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is { } line) onError(line);
            };
        if (onOutput is not null)
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is { } line) onOutput(line);
            };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return process;
    }

    internal static async Task SignalShutdownAsync(Process process, HttpClient http, Task stopping, CancellationToken ct)
    {
        await NativeRuntimeProcess.RunAsync("/usr/bin/kill",
            ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], 256);
        await stopping.WaitAsync(ct);
        while (!process.HasExited)
        {
            try { using var health = await http.GetAsync(ManagedServiceChecks.FullServerUrl + "/healthz", ct); }
            catch (HttpRequestException) { return; }
            await Task.Delay(50, ct);
        }
    }

    internal static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static async Task<HttpClient> ConnectAsync(Process process, LoginRequest login, CancellationToken ct)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(ManagedServiceChecks.FullServerUrl) };
        try
        {
            while (!process.HasExited)
            {
                try { using var health = await client.GetAsync("/healthz", ct); if (health.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(50, ct);
            }
            if (process.HasExited) throw new InvalidOperationException($"Authenticated Server exited with code {process.ExitCode}.");
            using var response = await client.PostAsJsonAsync("/api/v1.0/auth/login", login, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Production PAM login returned HTTP {(int)response.StatusCode}.");
            var session = await response.Content.ReadFromJsonAsync<LoginResponse>(RelaxKonOSJsonOptions.Default, ct);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.Tokens.AccessToken);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string route, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException($"{route} returned HTTP {(int)response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<ProxyOperationAcceptedDto>(RelaxKonOSJsonOptions.Default, ct))!.OperationId;
    }

    private static async Task<ProxyOperationDto> ReadAsync(HttpClient client, Guid operation, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ProxyOperationDto>($"{ProxyServerRoutes.Root}/operations/{operation:D}", RelaxKonOSJsonOptions.Default, ct))!;

    internal sealed class ReplyRelay : IAsyncDisposable
    {
        private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource stopping;
        private readonly Task run;
        private readonly string upstream;
        private readonly Func<string?, Guid, bool> hold;
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Path { get; }
        internal TaskCompletionSource<Guid> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ReplyRelay(string path, string upstream, CancellationToken ct, Func<string?, Guid, bool>? hold = null)
        {
            Path = path; this.upstream = upstream;
            this.hold = hold ?? ((operation, _) => operation == "ProxyServerApply");
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            listener.Listen(16);
            stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            run = RunAsync();
        }

        internal void ReleaseHeldResponse() => release.TrySetResult(true);
        internal void DropHeldResponse() => release.TrySetResult(false);

        private async Task RunAsync()
        {
            var calls = new List<Task>();
            try { while (!stopping.IsCancellationRequested) calls.Add(ForwardAsync(await listener.AcceptAsync(stopping.Token), stopping.Token)); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            await Task.WhenAll(calls);
        }

        private async Task ForwardAsync(Socket downstream, CancellationToken ct)
        {
            using (downstream)
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                try
                {
                    using var client = new NetworkStream(downstream);
                    var hello = await ReadFrameAsync(client, ct);
                    if (hello is null) return;
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(upstream), ct);
                    using var helper = new NetworkStream(socket);
                    await PrivilegedHelperSocketFraming.WriteAsync(helper, hello, ct);
                    await PrivilegedHelperSocketFraming.WriteAsync(client, (await ReadFrameAsync(helper, ct))!, ct);
                    var request = await ReadFrameAsync(client, ct);
                    if (request is null) return;
                    using var command = JsonDocument.Parse(request);
                    var operation = command.RootElement.GetProperty("operation").GetString();
                    var held = command.RootElement.TryGetProperty("operationId", out var value)
                        && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && hold(operation, id);
                    await PrivilegedHelperSocketFraming.WriteAsync(helper, request, ct);
                    while (await ReadFrameAsync(helper, ct) is { } response)
                    {
                        using var frame = JsonDocument.Parse(response);
                        var terminal = frame.RootElement.GetProperty("Type").GetString() == "result";
                        if (held && terminal && frame.RootElement.GetProperty("Result").GetProperty("success").GetBoolean())
                        {
                            Committed.TrySetResult(command.RootElement.GetProperty("operationId").GetGuid());
                            if (!await release.Task.WaitAsync(ct)) return;
                        }
                        await PrivilegedHelperSocketFraming.WriteAsync(client, response, ct);
                        if (terminal) return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (IOException) when (ct.IsCancellationRequested) { }
            }
        }

        private static Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct) =>
            PrivilegedHelperSocketFraming.ReadAsync(stream, PrivilegedHelperTransportProtocol.MaximumFrameBytes, ct);

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            try { await run; }
            finally { listener.Dispose(); stopping.Dispose(); File.Delete(Path); }
        }
    }
}
