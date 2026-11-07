using Microsoft.AspNetCore.DataProtection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

internal static class MihomoIpcStartupAcceptance
{
    internal static void RequirePrivateHost()
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
            throw new InvalidOperationException("Mihomo IPC acceptance requires private Linux systemd.");
        var mounts = File.ReadLines("/proc/self/mountinfo")
            .Where(line => line.Contains(" - tmpfs ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[4]).ToHashSet();
        if (!mounts.IsSupersetOf(["/var/lib", "/run", "/etc/relaxkonos", "/etc/systemd/system"]))
            throw new InvalidOperationException("Mihomo IPC acceptance requires private policy, state, units and sockets.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static async Task RunAsync(MihomoRuntimeLifecycle lifecycle, MihomoPublishedRuntime original,
        Func<int, Task<bool>> probe, int port, string helperAssembly, string serverAssembly)
    {
        RequirePrivateHost();
        var work = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK")
            ?? throw new InvalidOperationException("The dedicated runner directory is required.");
        var root = Directory.CreateDirectory(Path.Combine(work, "mihomo-ipc")).FullName;
        var statePath = Path.Combine(root, "server");
        var paths = new Paths(statePath);
        var protection = ManagedServiceChecks.OpenProtection(root);
        var content = Path.Combine(root, "full-server", "data");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "proxy-controller.secret"),
            protection.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1").Protect(original.ControllerSecret));
        var secrets = new DataProtectionProxyControllerSecretStore(
            new HostingEnvironment { ContentRootPath = Path.GetDirectoryName(content)! }, protection);
        var store = new MihomoPublicationStore(paths, protection);
        var before = await lifecycle.QueryAsync(default);
        var identity = MihomoPublicationStore.Identity(original.Yaml);
        if (before.ConfigurationIdentity != identity || before.ActiveRevisionId is null)
            throw new InvalidOperationException("The original systemd publication is unavailable.");
        var bootstrap = Guid.NewGuid();
        await store.BeginAsync(bootstrap, 0, original, default);
        var configRoot = Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory()).FullName;
        var active = Path.Combine(configRoot, "active.yaml");
        await File.WriteAllTextAsync(active, original.Yaml);
        var confirmed = await store.ConfirmAsync(bootstrap, identity, default);

        var uid = uint.Parse(Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID")!);
        var gid = uint.Parse(Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID")!);
        if (uid == 0) throw new InvalidOperationException("The full Server must use a non-root UID.");
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown", ["-R", $"{uid}:{gid}", "--", configRoot], 256);
        var socket = Path.Combine(root, "ipc", "helper.sock");
        var config = Path.Combine(root, "helper.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new LinuxPrivilegedHelperConfiguration(socket, gid, [0, uid])));
        var transport = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var operation = Guid.NewGuid();
        var candidate = MihomoConfigurationPolicy.Describe(original.Yaml.Replace("MATCH,DIRECT", "UNSUPPORTED-RULE,DIRECT", StringComparison.Ordinal)).Material;
        var plan = new RuntimeConfigurationPlan(before.Installation.InstallationId, before.Installation.HostId, "mihomo",
            before.Installation.StateRevision, before.ActiveRevisionId.Value, identity, MihomoPublicationStore.Identity(candidate.Yaml), "ipc-startup");
        var authorization = new MihomoRuntimeAuthorizationProvider(store, paths, secrets);
        var publication = new MihomoConfigurationPublicationService(store, authorization,
            new(new LoseConfigureReply(transport), store, authorization, new SystemMode(), new DirectDownloads()), paths);

        string firstLog;
        await using (var helper = await StartHelperAsync(helperAssembly, config, transport))
        {
            try
            {
                await publication.PublishAsync(operation, confirmed.Revision, plan, candidate, default);
                throw new InvalidOperationException("The response-loss barrier was not reached.");
            }
            catch (IOException error) when (error.Message == LoseConfigureReply.Message) { }
            firstLog = await helper.StopAsync(drainCompletedRequests: true);
        }
        var receipt = (await lifecycle.QueryAsync(default, operation)).Receipt;
        if (receipt is not { Outcome: RuntimeSwitchOutcome.Failed } || receipt.OperationId != operation
            || (await store.ReadAsync(default)).Pending?.OperationId != operation || !await probe(port)
            || !firstLog.Contains($"operation=MihomoRuntimeConfigure id={operation} ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real Helper compensation did not leave the original pending receipt.");

        var start = await ManagedServiceChecks.PrepareFullServerAsync(serverAssembly, socket, root);
        start.Environment["Proxy__Mihomo__Controller__Endpoint"] = $"http://127.0.0.1:{original.ControllerPort}/";
        var startupStore = new MihomoPublicationStore(new ProxyPlatformPaths(), protection);
        await CheckStartupAsync(start, operation, uid, ready: false);
        if ((await startupStore.ReadAsync(default)).Pending?.OperationId != operation)
            throw new InvalidOperationException("Blocked startup discarded the original Mihomo publication.");
        await using (var restarted = await StartHelperAsync(helperAssembly, config, transport))
        {
            await CheckStartupAsync(start, operation, uid, ready: true);
            var log = await restarted.StopAsync(drainCompletedRequests: true);
            if (log.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)
                || log.Contains("operation=MihomoRuntimeRecover ", StringComparison.Ordinal)
                || !log.Contains($"operation=MihomoRuntimeQuery id={operation} uid={uid} success=True", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The restarted Server did not exclusively query the original Helper receipt.");
        }
        var restored = await startupStore.ReadAsync(default);
        if (restored.Pending is not null || restored.Identity != identity || !await probe(port)
            || (await lifecycle.QueryAsync(default, operation)).Receipt != receipt)
            throw new InvalidOperationException("Recovered publication or original runtime traffic changed.");
        Console.WriteLine($"PASS MIHOMO IPC DISABLED STARTUP: original Failed receipt {operation}, one configure and no replay");

        var interrupted = Guid.NewGuid();
        var liveCandidate = MihomoConfigurationPolicy.Describe(original.Yaml.Replace($"mixed-port: {port}",
            $"mixed-port: {port + 6}", StringComparison.Ordinal)).Material;
        var observed = await lifecycle.QueryAsync(default);
        var interruptedPlan = plan with
        {
            ExpectedStateRevision = observed.Installation.StateRevision,
            CandidateIdentity = MihomoPublicationStore.Identity(liveCandidate.Yaml),
            IdempotencyKey = "ipc-interrupted"
        };
        await startupStore.BeginConfigurationAsync(interrupted, restored.Revision, liveCandidate, interruptedPlan, default);
        await NativeRuntimeProcess.RunAsync("/usr/bin/chown", ["-R", $"{uid}:{gid}", "--", ManagedServiceChecks.FullServerState], 256);
        var helperState = new RuntimeArchiveStateStore(Path.Combine(LinuxMihomoArchiveHost.Root, "state"), RuntimeSourceProtection.Protect);
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var helper = await StartHelperAsync(helperAssembly, config, transport))
        await using (var grant = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(interrupted, async ct =>
        {
            if (helperState.Read()?.Configuration?.Stage == RelaxKonOS.Protocol.Installations.InstallationStage.HealthChecking)
            {
                verifying.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new(interrupted, DateTimeOffset.UtcNow.AddSeconds(30), original, liveCandidate);
        }))
        {
            var publishing = transport.ExecuteAsync(new(PrivilegedOperationKind.MihomoRuntimeConfigure, OperationId: interrupted,
                RuntimeLifecycle: new(Authorization: grant.Channel, ConfigurationPlan: interruptedPlan)));
            var barrier = verifying.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(barrier, publishing) == publishing)
                throw new InvalidOperationException("Helper returned before reaching the native health-check barrier.");
            await barrier;
            if (!await probe(port + 6))
                throw new InvalidOperationException("The candidate did not forward real traffic before Helper termination.");
            await helper.StopAsync();
            if ((await publishing.WaitAsync(TimeSpan.FromSeconds(10))).Success)
                throw new InvalidOperationException("The interrupted Helper unexpectedly returned a configure receipt.");
        }
        var unresolved = await lifecycle.QueryAsync(default, interrupted);
        if (unresolved.PendingOperationId != interrupted || unresolved.Receipt is not null || !await probe(port + 6))
            throw new InvalidOperationException("The actual Helper interruption did not leave a live candidate and original journal.");
        await CheckStartupAsync(start, interrupted, uid, ready: false);
        await using (var helper = await StartHelperAsync(helperAssembly, config, transport))
        {
            await CheckStartupAsync(start, interrupted, uid, ready: true);
            var log = await helper.StopAsync(drainCompletedRequests: true);
            if (log.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)
                || log.Split('\n').Count(line => line.Contains("event=request-completed operation=MihomoRuntimeRecover ", StringComparison.Ordinal)
                    && line.Contains($"uid={uid} success=True", StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("Disabled startup did not perform exactly one named compensation.");
        }
        var final = await lifecycle.QueryAsync(default, interrupted);
        if (final.Receipt is not { Outcome: RuntimeSwitchOutcome.Interrupted } recovered || recovered.OperationId != interrupted
            || final.PendingOperationId is not null || final.ConfigurationIdentity != identity
            || (await startupStore.ReadAsync(default)).Pending is not null || !await probe(port))
            throw new InvalidOperationException("Compensation did not durably restore the original native publication.");
        Console.WriteLine($"PASS MIHOMO IPC INTERRUPTED COMPENSATION: original Interrupted receipt {interrupted}, one recover and no configure replay");

        var tunPaths = new ProxyPlatformPaths();
        using var requests = new ControllerRequestCount();
        using var http = new HttpClient(requests, disposeHandler: false);
        var controller = new MihomoControllerClient(http, secrets,
            new() { Endpoint = new($"http://127.0.0.1:{original.ControllerPort}/") });
        var platform = new HostProxyNetworkSafetyPlatform();
        var safety = new ProxyTunSafetyService(tunPaths, platform,
            new MihomoTunRuntimeController(tunPaths, new ProxySettingsReader(tunPaths), controller, startupStore,
                new HostProxyTunDeviceObserver()));
        var managementAddress = IPAddress.Parse("192.0.2.3");
        var route = await platform.CaptureManagementRouteAsync(managementAddress, default)
            ?? throw new InvalidOperationException("The private systemd fixture has no management route.");
        var networkBefore = await ReadNetworkAsync();
        var pidBefore = await ReadRuntimePidAsync();
        if (!int.TryParse(pidBefore, out var runtimePid) || runtimePid <= 0)
            throw new InvalidOperationException("The confirmed Mihomo systemd process is not running.");
        var activeBefore = await File.ReadAllBytesAsync(active);
        var publicationBefore = await startupStore.ReadAsync(default);
        var rejected = await safety.EnableAsync(Guid.NewGuid(), managementAddress, default);
        var after = await lifecycle.QueryAsync(default, interrupted);
        var publicationAfter = await startupStore.ReadAsync(default);
        var activeAfter = await File.ReadAllBytesAsync(active);
        if (rejected != ProxyProblemCodes.NotSupported || await safety.CaptureRecoveryAsync(includeCurrentSession: true, default) is not null
            || requests.Count != 0 || !activeBefore.SequenceEqual(activeAfter)
            || after.Receipt != final.Receipt || after.Installation.StateRevision != final.Installation.StateRevision
            || after.ConfigurationIdentity != final.ConfigurationIdentity
            || publicationAfter.Revision != publicationBefore.Revision || publicationAfter.Identity != publicationBefore.Identity
            || publicationAfter.Pending is not null
            || await ReadNetworkAsync() != networkBefore || !await platform.VerifyManagementRouteAsync(route, default)
            || await ReadRuntimePidAsync() != pidBefore || !await probe(port))
            throw new InvalidOperationException("Rejected TUN activation changed the confirmed native publication, network or recovery authority.");
        Console.WriteLine($"PASS MIHOMO CONFIRMED TUN GATE: original compensation {interrupted}, native PID preserved, no marker or controller request");

        var baseline = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_MIHOMO_OVERVIEW_BASELINE");
        var trafficPolling = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_MIHOMO_TRAFFIC_POLLING") == "1";
        var subscriptionsEnabled = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_MIHOMO_SUBSCRIPTION") == "1";
        var login = subscriptionsEnabled || trafficPolling || !string.IsNullOrEmpty(baseline)
            ? ProxyAuthenticatedPublicationChecks.ReadLogin() : null;
        if (trafficPolling || baseline is { Length: > 0 })
        {
            start.Environment["Modules__Proxy__Enabled"] = "true";
            start.Environment["Identity__LinuxPamTransport"] = "helper";
            await using var helper = await StartHelperAsync(helperAssembly, config, transport);
            await ProxyConnectionBenchmark.RunAsync(trafficPolling ? serverAssembly : baseline!, serverAssembly,
                trafficPolling ? "no-polling" : "baseline", trafficPolling ? "traffic-polling" : "candidate",
                login!, new Uri($"http://127.0.0.1:{port}/"), PrepareAsync, VerifyAsync, requireManaged: true,
                pollingRuntimePid: trafficPolling ? runtimePid : null);
            var log = await helper.StopAsync(drainCompletedRequests: true);
            if (log.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)
                || log.Contains("operation=MihomoRuntimeRecover ", StringComparison.Ordinal))
                throw new InvalidOperationException("The read workload dispatched a native publication action.");
            var measurement = trafficPolling ? "TRAFFIC POLLING" : "OVERVIEW";
            Console.WriteLine($"PASS MANAGED MIHOMO {measurement} BENCHMARK: native PID {runtimePid}, original receipt {interrupted}, no configure or recover");

            Task<ProcessStartInfo> PrepareAsync(string assembly)
            {
                start.ArgumentList[^1] = Path.GetFullPath(assembly);
                return Task.FromResult(start);
            }

            async Task VerifyAsync()
            {
                var state = await lifecycle.QueryAsync(default, interrupted);
                var publication = await startupStore.ReadAsync(default);
                if (state.Receipt != final.Receipt || state.Installation.StateRevision != final.Installation.StateRevision
                    || state.ConfigurationIdentity != final.ConfigurationIdentity || publication.Revision != publicationBefore.Revision
                    || publication.Identity != publicationBefore.Identity || publication.Pending is not null
                    || await ReadRuntimePidAsync() != pidBefore || !await probe(port))
                    throw new InvalidOperationException("The read workload changed the original runtime, publication or recovery receipt.");
            }
        }

        await VerifyPublicationShutdownAsync();
        if (subscriptionsEnabled)
        {
            await using var helper = await StartHelperAsync(helperAssembly, config, transport);
            var native = await lifecycle.QueryAsync(default, interrupted);
            var application = await startupStore.ReadAsync(default);
            var pid = await ReadRuntimePidAsync();
            await MihomoSubscriptionQueueAcceptance.RunAsync(start, tunPaths, port, startupStore, lifecycle, login!, probe, async ct =>
            {
                var observed = await lifecycle.QueryAsync(ct, interrupted);
                var published = await startupStore.ReadAsync(ct);
                if (observed.Receipt != native.Receipt || observed.Installation.StateRevision != native.Installation.StateRevision
                    || observed.ConfigurationIdentity != native.ConfigurationIdentity || published.Revision != application.Revision
                    || published.Identity != application.Identity || published.Pending is not null
                    || await ReadRuntimePidAsync() != pid || !await probe(port))
                    throw new InvalidOperationException("The subscription queue changed the original native publication or traffic.");
            });
            var log = await helper.StopAsync(drainCompletedRequests: true);
            if (log.Split('\n').Count(line => line.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)) != 4
                || log.Contains("operation=MihomoRuntimeRecover ", StringComparison.Ordinal))
                throw new InvalidOperationException("Subscription activation and each refresh must configure once without native replay.");

            // The remaining archive checks reuse this private fixture's original published configuration.
            await using var cleanupHelper = await StartHelperAsync(helperAssembly, config, transport);
            var current = await lifecycle.QueryAsync(default);
            var currentPublication = await startupStore.ReadAsync(default);
            var cleanupId = Guid.NewGuid();
            var cleanupPlan = new RuntimeConfigurationPlan(current.Installation.InstallationId, current.Installation.HostId,
                "mihomo", current.Installation.StateRevision, current.ActiveRevisionId!.Value, currentPublication.Identity!, identity,
                cleanupId.ToString("N"));
            var cleanupAuthorization = new MihomoRuntimeAuthorizationProvider(startupStore, tunPaths, secrets);
            var cleanup = new MihomoConfigurationPublicationService(startupStore, cleanupAuthorization,
                new(transport, startupStore, cleanupAuthorization, new SystemMode(), new DirectDownloads()), tunPaths);
            if ((await cleanup.PublishAsync(cleanupId, currentPublication.Revision, cleanupPlan, original, default)).Outcome
                != MihomoPublicationOutcome.Confirmed || !await probe(port))
                throw new InvalidOperationException("Could not restore the dedicated fixture for the remaining archive checks.");
        }

        async Task VerifyPublicationShutdownAsync()
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var ct = budget.Token;
            var drainId = Guid.NewGuid();
            var pendingBase = await startupStore.ReadAsync(ct);
            var nativeBefore = await lifecycle.QueryAsync(ct);
            var drainPlan = plan with
            {
                ExpectedStateRevision = nativeBefore.Installation.StateRevision,
                IdempotencyKey = "ipc-shutdown-drain"
            };
            var provider = new MihomoRuntimeAuthorizationProvider(startupStore, tunPaths, secrets);
            var writer = new MihomoConfigurationPublicationService(startupStore, provider,
                new(new LoseConfigureReply(transport), startupStore, provider, new SystemMode(), new DirectDownloads()), tunPaths);
            await using (var preparing = await StartHelperAsync(helperAssembly, config, transport))
            {
                try
                {
                    await writer.PublishAsync(drainId, pendingBase.Revision, drainPlan, candidate, ct);
                    throw new InvalidOperationException("The real configure reply was not lost before application confirmation.");
                }
                catch (IOException error) when (error.Message == LoseConfigureReply.Message) { }
                var preparationLog = await preparing.StopAsync(drainCompletedRequests: true);
                if (preparationLog.Split('\n').Count(line => line.Contains($"operation=MihomoRuntimeConfigure id={drainId} ", StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidOperationException("The original configure command did not execute exactly once.");
            }
            var nativeReceipt = await lifecycle.QueryAsync(ct, drainId);
            var pending = await startupStore.ReadAsync(ct);
            var nativePid = await ReadRuntimePidAsync();
            if (nativeReceipt.Receipt is not { Outcome: RuntimeSwitchOutcome.Failed } failed || failed.OperationId != drainId
                || pending.Pending?.OperationId != drainId || nativeReceipt.ConfigurationIdentity != identity || !await probe(port))
                throw new InvalidOperationException("The original Helper outcome or live compensation was unavailable before shutdown.");
            await NativeRuntimeProcess.RunAsync("/usr/bin/chown", ["-R", $"{uid}:{gid}", "--", ManagedServiceChecks.FullServerState], 256);

            await using var helper = await StartHelperAsync(helperAssembly, config, transport);
            await using var relay = new ProxyAuthenticatedPublicationChecks.ReplyRelay("/run/relaxkonos-mihomo-drain.sock", socket, ct,
                (kind, id) => kind == "MihomoRuntimeQuery" && id == drainId);
            start.ArgumentList[^1] = Path.GetFullPath(serverAssembly);
            start.Environment["Modules__Proxy__Enabled"] = "true";
            if (login is not null)
            {
                start.Environment["Identity__LinuxPamTransport"] = "helper";
                start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location;
            }
            start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            start.Environment["PrivilegedHelper__SocketPath"] = relay.Path;
            var shuttingDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parserEvents = Channel.CreateUnbounded<string>();
            using var process = ProxyAuthenticatedPublicationChecks.Start(start, line =>
            {
                if (line.StartsWith(StartupHook.ParserEventPrefix, StringComparison.Ordinal))
                    parserEvents.Writer.TryWrite(line[StartupHook.ParserEventPrefix.Length..]);
            }, onOutput: line =>
            {
                if (line.Contains("Application is shutting down", StringComparison.Ordinal))
                    shuttingDown.TrySetResult();
            });
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            try
            {
                if (await relay.Committed.Task.WaitAsync(ct) != drainId)
                    throw new InvalidOperationException("The relay did not hold the original Helper response.");
                string? parserEvidence = null;
                if (login is not null) parserEvidence = await VerifyParserAsync(login);
                else
                {
                    var healthUrl = ManagedServiceChecks.FullServerUrl + "/healthz";
                    while (!process.HasExited)
                    {
                        try { using var health = await http.GetAsync(healthUrl, ct); if (health.IsSuccessStatusCode) break; }
                        catch (HttpRequestException) { }
                        await Task.Delay(50, ct);
                    }
                    if (process.HasExited) throw new InvalidOperationException("Server exited before SIGTERM.");
                    await DrainAsync();
                }
                var completed = await startupStore.ReadAsync(ct);
                var observedAfter = await lifecycle.QueryAsync(ct, drainId);
                if (process.ExitCode != 0 || completed.Pending is not null || completed.Identity != identity
                    || observedAfter.Receipt != failed || observedAfter.Installation.StateRevision != nativeReceipt.Installation.StateRevision
                    || await ReadRuntimePidAsync() != nativePid || !await probe(port))
                    throw new InvalidOperationException("Server exited without durably draining the original recovery.");
                start.Environment["PrivilegedHelper__SocketPath"] = socket;
                await CheckStartupAsync(start, drainId, uid, ready: true);
                var restarted = await startupStore.ReadAsync(ct);
                var log = await helper.StopAsync(drainCompletedRequests: true);
                if (restarted.Pending is not null || restarted.Revision != completed.Revision || restarted.Identity != identity
                    || log.Contains("operation=MihomoRuntimeConfigure ", StringComparison.Ordinal)
                    || log.Contains("operation=MihomoRuntimeRecover ", StringComparison.Ordinal)
                    || log.Split('\n').Count(line => line.Contains($"operation=MihomoRuntimeQuery id={drainId} uid=0 success=True", StringComparison.OrdinalIgnoreCase)) != 1
                    || !await probe(port))
                    throw new InvalidOperationException("Shutdown and restart must not replay the original configure or recovery.");
                Console.WriteLine($"PASS MIHOMO FULL SERVER SIGTERM DRAIN: original Failed receipt {drainId}, one query, durable cleanup, same runtime PID and live traffic");
                if (parserEvidence is not null)
                    Console.WriteLine($"PASS MIHOMO PARSER IDLE DURING RECOVERY: {drainId}, {parserEvidence}");
            }
            finally
            {
                relay.ReleaseHeldResponse();
                await ProxyAuthenticatedPublicationChecks.StopAsync(process);
                start.Environment.Remove("DOTNET_STARTUP_HOOKS");
            }

            async Task<string> VerifyParserAsync(LoginRequest credentials)
            {
                using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(process, credentials, ct);
                using var origin = new TcpListener(IPAddress.Parse("93.184.216.34"), 0);
                origin.Start();
                var destination = origin.LocalEndpoint.ToString();
                using var proxy = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}") })
                    { Timeout = Timeout.InfiniteTimeSpan };
                var forwarding = proxy.GetStringAsync($"http://{destination}/", ct);
                using var upstream = await origin.AcceptTcpClientAsync(ct);
                using var stream = upstream.GetStream();
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }

                async Task<string> ObserveConnectionAsync()
                {
                    var connections = await client.GetFromJsonAsync<ProxyConnectionDto[]>(ProxyApiRoutes.Connections, RelaxKonOSJsonOptions.Default, ct);
                    return connections?.SingleOrDefault(item => item.Destination == destination)?.Id
                        ?? throw new InvalidOperationException("The held native connection was absent from the production read route.");
                }

                var originalConnection = await ObserveConnectionAsync();
                var loaded = await parserEvents.Reader.ReadAsync(ct);
                var unloaded = await parserEvents.Reader.ReadAsync(ct);
                if (!loaded.StartsWith("load ", StringComparison.Ordinal) || unloaded != $"unload {loaded[5..]}"
                    || (await startupStore.ReadAsync(ct)).Pending?.OperationId != drainId || forwarding.IsCompleted)
                    throw new InvalidOperationException("The original parser did not idle-unload while Core and the connection remained active.");
                var sameConnection = await ObserveConnectionAsync();
                var woke = await parserEvents.Reader.ReadAsync(ct);
                if (!woke.StartsWith("load ", StringComparison.Ordinal) || woke == loaded || sameConnection != originalConnection
                    || process.HasExited || (await startupStore.ReadAsync(ct)).Pending?.OperationId != drainId
                    || await ReadRuntimePidAsync() != nativePid || forwarding.IsCompleted)
                    throw new InvalidOperationException("The new parser generation did not observe the original connection during recovery.");
                var logRead = client.GetFromJsonAsync<ProxyLogEntryDto[]>(ProxyApiRoutes.Logs + "?limit=1", RelaxKonOSJsonOptions.Default, ct);
                var trafficRead = client.GetFromJsonAsync<ProxyTrafficDto>(ProxyApiRoutes.Traffic, RelaxKonOSJsonOptions.Default, ct);
                for (var attempt = 0; attempt < 8 && !logRead.IsCompleted; attempt++)
                {
                    if (!await probe(port)) throw new InvalidOperationException("Mihomo did not forward while the controller logs were sampled.");
                    await Task.Delay(100, ct);
                }
                var logs = await logRead;
                var traffic = await trafficRead;
                if (logs is not { Length: 1 } || traffic is not { ProblemCode: "", MemoryBytes: >= 0, UploadTotalBytes: > 0 }
                    || (await startupStore.ReadAsync(ct)).Pending?.OperationId != drainId || forwarding.IsCompleted)
                    throw new InvalidOperationException($"Live observation failed during recovery. Logs={logs?.Length}, traffic={traffic}, pending={(await startupStore.ReadAsync(ct)).Pending?.OperationId == drainId}, forwardingCompleted={forwarding.IsCompleted}.");
                await DrainAsync();
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nproxy-ok"u8.ToArray(), ct);
                if (await forwarding != "proxy-ok")
                    throw new InvalidOperationException("The original connection did not receive its upstream response after recovery.");
                Console.WriteLine($"PASS MIHOMO LIVE TRAFFIC AND LOGS DURING RECOVERY: {drainId}; {traffic}, forwarding completed");
                return $"{loaded}, {woke}, same native connection {originalConnection}";
            }

            async Task DrainAsync()
            {
                await ProxyAuthenticatedPublicationChecks.SignalShutdownAsync(process, http, shuttingDown.Task, ct);
                if (process.HasExited || (await startupStore.ReadAsync(ct)).Pending?.OperationId != drainId
                    || await ReadRuntimePidAsync() != nativePid || !await probe(port))
                    throw new InvalidOperationException("SIGTERM dropped a pending recovery or its original native traffic.");
                relay.ReleaseHeldResponse();
                await process.WaitForExitAsync(ct);
            }
        }
    }

    private static async Task<string> ReadNetworkAsync() =>
        await NativeRuntimeProcess.RunAsync("/usr/sbin/ip", ["-4", "rule", "show"], 4096)
        + await NativeRuntimeProcess.RunAsync("/usr/sbin/ip", ["-6", "rule", "show"], 4096)
        + string.Join(',', NetworkInterface.GetAllNetworkInterfaces().Select(item => item.Name).Order(StringComparer.Ordinal));

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static Task<string> ReadRuntimePidAsync() =>
        NativeRuntimeProcess.RunAsync("/usr/bin/systemctl", ["show", LinuxMihomoArchiveHost.Unit, "--property=MainPID", "--value"], 64);

    private sealed class ControllerRequestCount() : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        internal int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task CheckStartupAsync(ProcessStartInfo start, Guid operation, uint uid, bool ready)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start full Server.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        try
        {
            while (!process.HasExited)
            {
                try
                {
                    using var response = await http.GetAsync(ManagedServiceChecks.FullServerUrl + "/healthz", deadline.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        if (!ready) throw new InvalidOperationException("Server admitted unknown Mihomo publication.");
                        var identity = File.ReadLines($"/proc/{process.Id}/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal));
                        if (!identity.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).All(value => value == uid.ToString()))
                            throw new InvalidOperationException("Server did not run under its non-root UID.");
                        return;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            if (ready || process.ExitCode == 0 || !(await errors).Contains(operation.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected full Server exit {process.ExitCode}.");
            Console.WriteLine("PASS MIHOMO IPC UNAVAILABLE: startup refused with original operation");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!(await output).Contains("Now listening on", StringComparison.Ordinal) && ready)
                Console.WriteLine(await errors);
        }
    }

    private static async Task<HelperProcess> StartHelperAsync(string assembly, string config, IPrivilegedOperationTransport transport)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { assembly, "--linux-service", "--config", config }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start production Helper.");
        var helper = new HelperProcess(process, process.StandardError.ReadToEndAsync());
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!process.HasExited)
            {
                if ((await transport.ExecuteAsync(new(PrivilegedOperationKind.MihomoRuntimeQuery), deadline.Token)).Success)
                    return helper;
                await Task.Delay(100, deadline.Token);
            }
            throw new InvalidOperationException("Helper exited before readiness.");
        }
        catch { await helper.DisposeAsync(); throw; }
    }

    private sealed class HelperProcess(Process process, Task<string> stderr) : IAsyncDisposable
    {
        public async Task<string> StopAsync(bool drainCompletedRequests = false)
        {
            if (!process.HasExited)
            {
                // The Helper writes the completion event after the wire response. A forced
                // stop in a completed-request case can kill it in that gap.
                if (drainCompletedRequests)
                    await NativeRuntimeProcess.RunAsync("/usr/bin/kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], 256);
                else process.Kill();
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return await stderr;
        }

        public async ValueTask DisposeAsync()
        {
            using (process) Console.WriteLine(await StopAsync());
        }
    }

    private sealed class LoseConfigureReply(IPrivilegedOperationTransport transport) : IPrivilegedOperationTransport
    {
        internal const string Message = "Lost real configure reply before Server publication confirmation";
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            var result = await transport.ExecuteAsync(request, ct);
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure && result.Success) throw new IOException(Message);
            return result;
        }
    }

    private sealed class Paths(string state) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => state;
        public string GetProtectedConfigurationDirectory() => "/etc/relaxkonos/proxy";
        public string GetEngineVersionsDirectory(string engineId) => state;
        public string GetEngineDataDirectory(string engineId) => state;
        public string GetSanitizedLogDirectory() => state;
    }

    private sealed class SystemMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => feature == ServerHostFeature.PrivilegedOperations;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
