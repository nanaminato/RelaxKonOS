using Microsoft.AspNetCore.Builder;
using RelaxKonOS.Server.Startup;
using System.Diagnostics;
using System.Net.Security;
using System.Text.Json.Nodes;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.ProxyServer;

internal static class ProxyExpiryLiveChecks
{
    private const string ServerName = "expiry.example.test";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsLinux() || executable != "/usr/bin/sing-box" || !File.Exists(executable))
            throw new ArgumentException("Expected the installed Linux sing-box binary.");

        var directory = Directory.CreateTempSubdirectory("relaxkonos-singbox-expiry-");
        try
        {
            using var origin = new TcpListener(IPAddress.Loopback, 0);
            origin.Start();
            var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var inboundPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={ServerName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(ServerName);
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

            var inboundId = Guid.NewGuid();
            var expiredId = Guid.NewGuid();
            var retainedId = Guid.NewGuid();
            var expiring = Guid.NewGuid();
            var retained = Guid.NewGuid();
            var inbound = new ProxyServerInbound(inboundId, "expiry-fixture", ProxyServerProtocol.VlessTls, "127.0.0.1",
                inboundPort, "127.0.0.1", inboundPort, ServerName, CertificateId: Guid.NewGuid());
            var material = new ProxyServerMaterial(new(1, [inbound]),
                [new(expiredId, [inboundId], expiring.ToString("D")), new(retainedId, [inboundId], retained.ToString("D"))], [],
                [new(inboundId, certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), "fixture")]);
            var paths = new TestProxyPaths(directory.FullName);
            var protection = OpenProtection(directory.FullName);
            var store = new ProxyServerStore(paths, protection);
            var credentials = new StoredCredential[]
            {
                new(expiredId, Guid.NewGuid(), "expiring", [inboundId], expiring.ToString("D"), true, null),
                new(retainedId, Guid.NewGuid(), "retained", [inboundId], retained.ToString("D"), true, null)
            };
            await store.WriteAsync(ProxyServerState.Empty with { Published = material, Credentials = credentials }, default);

            await using var transport = new IsolatedTransport(executable, directory.FullName, inboundPort, originPort);
            await transport.ReplaceAsync(material);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.AddProxyRuntimeServices();
            builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
            builder.Services.AddSingleton(protection);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<IPrivilegedOperationTransport>(transport);
            builder.Services.AddSingleton<IServerModeResolver>(new SystemMode());
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            var catalog = ServerModules.CreateManaged(new HashSet<string>());
            var modules = ServerModules.CreateManaged(catalog.KnownModuleIds.Where(id => id != ServerModules.ProxyId).ToHashSet(StringComparer.Ordinal));
            modules.ConfigureServices(builder, context);
            await using var owner = builder.Build();
            await modules.ValidateDeactivationAsync(owner.Services, context, default);
            TestAssert.Assert(owner.Services.GetService<IProxyEngineRegistry>() is null,
                "The disabled Proxy management graph must remain absent while existing grants expire.");
            using var expiry = ActivatorUtilities.CreateInstance<ProxyServerExpiryService>(owner.Services);
            await expiry.ReconcileAsync(default);
            TestAssert.Assert(transport.ApplyCalls == 0, "Idle expiry must leave the live instance alone.");
            using var initialBudget = new CancellationTokenSource(Budget);
            using var expiringConnection = await ConnectAsync(inboundPort, fingerprint, initialBudget.Token);
            using var expiringOrigin = await ForwardAsync(origin, expiringConnection, expiring, initialBudget.Token);
            using var retainedConnection = await ConnectAsync(inboundPort, fingerprint, initialBudget.Token);
            using var retainedOrigin = await ForwardAsync(origin, retainedConnection, retained, initialBudget.Token);

            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(2);
            await store.WriteAsync(ProxyServerState.Empty with
            {
                Published = material, Credentials = [credentials[0] with { ExpiresAt = expiresAt }, credentials[1]]
            }, default);
            using var clockBudget = new CancellationTokenSource(Budget);
            while (DateTimeOffset.UtcNow < expiresAt) await Task.Delay(50, clockBudget.Token);
            using (var worker = DockerProcessRecoveryChecks.StartWorker("--proxy-expiry-crash-worker", directory.FullName))
            {
                try
                {
                    var marker = await worker.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                    TestAssert.Assert(marker == "apply-pending", $"Missing expiry worker barrier: {marker}");
                    var pending = (await store.ReadAsync(default)).PendingPublication
                        ?? throw new InvalidOperationException("The worker did not persist the pending publication.");
                    TestAssert.Assert(!worker.HasExited && pending.Credentials.Single().Id == retainedId,
                        "The original pending candidate must contain only the retained credential.");
                    await transport.ExecuteAsync(new(PrivilegedOperationKind.ProxyServerApply, ProxyServer: new(Material: pending)));
                    worker.Kill();
                    await worker.WaitForExitAsync().WaitAsync(Budget);
                    TestAssert.Assert(worker.ExitCode != 0 && (await store.ReadAsync(default)).PendingPublication is not null,
                        "Worker termination must leave the original pending publication.");
                }
                finally { await DockerProcessRecoveryChecks.StopWorkerAsync(worker); }
            }

            // A live listener alone cannot confirm a pending publication.
            transport.HideConfigurationHash = true;
            try
            {
                await modules.ValidateDeactivationAsync(owner.Services, context, default);
                throw new InvalidOperationException("An unresolved inbound publication passed the disabled startup gate.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("Inbound publication remains unconfirmed", StringComparison.Ordinal))
            {
                TestAssert.Assert(transport.ApplyCalls == 1 && (await store.ReadAsync(default)).PendingPublication is not null,
                    "The startup gate must preserve the original candidate and avoid another apply.");
            }

            using var closureBudget = new CancellationTokenSource(Budget);
            await AssertClosedAsync(expiringConnection, closureBudget.Token);
            await AssertClosedAsync(expiringOrigin.GetStream(), closureBudget.Token);
            await AssertClosedAsync(retainedConnection, closureBudget.Token);
            await AssertClosedAsync(retainedOrigin.GetStream(), closureBudget.Token);

            using var retainedProbe = await ConnectAsync(inboundPort, fingerprint, closureBudget.Token);
            using var retainedProbeOrigin = await ForwardAsync(origin, retainedProbe, retained, closureBudget.Token);
            using (var deadline = new CancellationTokenSource(Budget))
            using (var denied = await ConnectAsync(inboundPort, fingerprint, deadline.Token))
            {
                await SendAsync(denied, originPort, expiring, deadline.Token);
                await AssertClosedAsync(denied, deadline.Token);
            }

            TestAssert.Assert(!origin.Pending(), "The expired UUID reached the origin.");
            File.Delete(Path.Combine(paths.GetStateDirectory(), "proxy-server.protected.expiry"));
            var reopened = new ProxyServerStore(paths, OpenProtection(directory.FullName));
            TestAssert.Assert((await reopened.ReadExpiryScheduleAsync(default)).PendingPublication,
                "The recovery process must discover the pending publication without its index.");
            transport.HideConfigurationHash = false;
            await modules.ValidateDeactivationAsync(owner.Services, context, default);
            var confirmed = await reopened.ReadAsync(default);
            TestAssert.Assert(confirmed.PendingPublication is null && confirmed.Published!.Credentials.Single().Id == retainedId
                && transport.ApplyCalls == 1, "Recovery must confirm the applied material without a second mutation.");
            Console.WriteLine("PASS SING-BOX LIVE DISABLED STARTUP GATE: pending publication blocked; original candidate reconciled; startup permitted");
            Console.WriteLine("PASS SING-BOX LIVE EXPIRY: management disabled; pre-expiry forwarded, old streams closed, expired rejected, retained reconnected, worker crashed before reply; original pending publication recovered; apply=1");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<TcpClient> ForwardAsync(TcpListener origin, Stream stream, Guid uuid, CancellationToken ct)
    {
        await SendAsync(stream, ((IPEndPoint)origin.LocalEndpoint).Port, uuid, ct);
        var accepted = await origin.AcceptTcpClientAsync(ct);
        try
        {
            var data = new byte[1];
            await accepted.GetStream().ReadExactlyAsync(data, ct);
            TestAssert.Assert(data[0] == 42, "The authorized VLESS request did not reach its origin.");
            await accepted.GetStream().WriteAsync(data, ct);
            var response = new byte[3];
            await stream.ReadExactlyAsync(response, ct);
            TestAssert.Assert(response.AsSpan().SequenceEqual(new byte[] { 0, 0, 42 }), "The authorized VLESS response was not forwarded.");
            return accepted;
        }
        catch { accepted.Dispose(); throw; }
    }

    private static async Task AssertClosedAsync(Stream stream, CancellationToken ct)
    {
        try { TestAssert.Assert(await stream.ReadAsync(new byte[1], ct) == 0, "Expected the peer to close without forwarding another byte."); }
        catch (IOException) when (!ct.IsCancellationRequested) { /* A peer reset also terminates the stream. */ }
    }

    private static async Task<SslStream> ConnectAsync(int port, string fingerprint, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(IPAddress.Loopback, port, ct); }
        catch { socket.Dispose(); throw; }
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), false, (_, certificate, _, errors) =>
            certificate?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ServerName }, ct);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    private static async Task SendAsync(Stream stream, int port, Guid uuid, CancellationToken ct)
    {
        // VLESS v0, no addons, TCP, big-endian port, IPv4 loopback, one byte of fixture payload.
        byte[] packet = [0, .. uuid.ToByteArray(bigEndian: true), 0, 1, (byte)(port >> 8), (byte)port, 1, 127, 0, 0, 1, 42];
        await stream.WriteAsync(packet, ct);
    }

    private static IDataProtectionProvider OpenProtection(string root) =>
        DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(root, "fixture-keys")));

    internal static async Task RunCrashWorkerAsync(string root)
    {
        var store = new ProxyServerStore(new TestProxyPaths(root), OpenProtection(root));
        await new ProxyServerPublicationService(store, new CrashTransport(), new SystemMode()).ReconcileExpiryAsync(default);
        throw new InvalidOperationException("Expiry worker exited before the crash boundary.");
    }

    private sealed class CrashTransport : IPrivilegedOperationTransport
    {
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct)
        {
            if (request.Operation == PrivilegedOperationKind.ProxyServerStatus)
            {
                var status = new ProxyServerStatus(true, true, true, false, "fixture", null, true, null);
                return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status)));
            }
            if (request.Operation != PrivilegedOperationKind.ProxyServerApply)
                throw new InvalidOperationException("Unexpected expiry worker request.");
            Console.WriteLine("apply-pending");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The expiry crash barrier returned.");
        }
    }

    private sealed class SystemMode : IServerModeResolver
    {
        public RelaxKonOS.Protocol.Common.ServerMode Mode => RelaxKonOS.Protocol.Common.ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }

    private sealed class IsolatedTransport(string executable, string root, int inboundPort, int originPort) : IPrivilegedOperationTransport, IAsyncDisposable
    {
        private Process? _process;
        private string? _hash;
        internal int ApplyCalls;
        internal bool HideConfigurationHash;

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply)
            {
                ApplyCalls++;
                await ReplaceAsync(request.ProxyServer!.Material!);
                return new(false, Error: "proxy_server.helper_unavailable");
            }
            if (request.Operation != PrivilegedOperationKind.ProxyServerStatus)
                throw new InvalidOperationException("Unexpected privileged fixture operation.");
            var status = new ProxyServerStatus(true, true, true, false, "fixture", HideConfigurationHash ? null : _hash, true, null);
            return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status)));
        }

        internal async Task ReplaceAsync(ProxyServerMaterial material)
        {
            var rendered = SingBoxConfiguration.Render(material, false);
            var config = JsonNode.Parse(rendered)!.AsObject();
            // Permit only this loopback origin in the fixture. The real rendered inbound/TLS/users are used verbatim.
            config["route"] = JsonSerializer.SerializeToNode(new
            {
                rules = new object[]
                {
                    new { ip_cidr = new[] { "127.0.0.1/32" }, port = originPort, action = "route", outbound = "direct" },
                    new { action = "reject" }
                }
            });
            var path = Path.Combine(root, "isolated.json");
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var writer = new StreamWriter(new FileStream(path, options)))
                await writer.WriteAsync(config.ToJsonString());
            await DisposeAsync();
            var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in new[] { "run", "-c", path }) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch isolated sing-box.");
            using var deadline = new CancellationTokenSource(Budget);
            while (!_process.HasExited)
            {
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, inboundPort, deadline.Token); _hash = ProxyServerPublicationService.Hash(rendered); return; }
                catch (SocketException) { await Task.Delay(50, deadline.Token); }
            }
            throw new InvalidOperationException("Isolated sing-box exited: " + await _process.StandardError.ReadToEndAsync());
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is not { } process) return;
            _process = null;
            using (process)
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync().WaitAsync(Budget);
            }
        }
    }
}
