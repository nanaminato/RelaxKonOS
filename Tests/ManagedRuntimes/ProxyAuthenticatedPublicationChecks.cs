using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.ProxyServer;

internal static class ProxyAuthenticatedPublicationChecks
{
    internal static async Task RunAsync(string root, string assembly, string socket, ISingBoxArchiveHost host,
        ProxyFullServerTrafficProbe traffic, ProxyServerSecret retained, Func<RuntimeArchiveState> nativeState,
        Func<Task> stopHelper, Func<Task> restartHelper, ProxySystemdGuardProbe guard)
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
        if (Environment.GetEnvironmentVariable("RELAXKONOS_TEST_PROXY_BENCHMARK_BASELINE") is { Length: > 0 } baseline)
            await ProxyConnectionBenchmark.RunAsync(root, socket, baseline, assembly, login, guard, nativeState);
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

    private static LoginRequest ReadLogin()
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

    internal static Process Start(ProcessStartInfo start, Action<string>? onError = null)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the authenticated Server.");
        if (onError is not null)
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is { } line) onError(line);
            };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return process;
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

    private sealed class ReplyRelay : IAsyncDisposable
    {
        private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource stopping;
        private readonly Task run;
        private readonly string upstream;
        internal string Path { get; }
        internal TaskCompletionSource<Guid> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ReplyRelay(string path, string upstream, CancellationToken ct)
        {
            Path = path; this.upstream = upstream;
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            listener.Listen(16);
            stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            run = RunAsync();
        }

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
                    var apply = command.RootElement.GetProperty("operation").GetString() == "ProxyServerApply";
                    await PrivilegedHelperSocketFraming.WriteAsync(helper, request, ct);
                    while (await ReadFrameAsync(helper, ct) is { } response)
                    {
                        using var frame = JsonDocument.Parse(response);
                        var terminal = frame.RootElement.GetProperty("Type").GetString() == "result";
                        if (apply && terminal && frame.RootElement.GetProperty("Result").GetProperty("success").GetBoolean())
                        {
                            Committed.TrySetResult(command.RootElement.GetProperty("operationId").GetGuid());
                            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
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
