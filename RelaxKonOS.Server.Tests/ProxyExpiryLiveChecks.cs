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
            var protection = new EphemeralDataProtectionProvider();
            var store = new ProxyServerStore(paths, protection);
            var credentials = new StoredCredential[]
            {
                new(expiredId, Guid.NewGuid(), "expiring", [inboundId], expiring.ToString("D"), true, null),
                new(retainedId, Guid.NewGuid(), "retained", [inboundId], retained.ToString("D"), true, null)
            };
            await store.WriteAsync(ProxyServerState.Empty with { Published = material, Credentials = credentials }, default);

            await using var transport = new IsolatedTransport(executable, directory.FullName, inboundPort, originPort);
            await transport.ReplaceAsync(material);
            var services = new ServiceCollection();
            services.AddSingleton<IProxyServerExpiryExecutor>(_ => new ProxyServerPublicationService(store, transport, new SystemMode()));
            await using var provider = services.BuildServiceProvider();
            using var expiry = new ProxyServerExpiryService(provider.GetRequiredService<IServiceScopeFactory>(), store,
                NullLogger<ProxyServerExpiryService>.Instance);
            await expiry.ReconcileAsync(default);
            TestAssert.Assert(transport.ApplyCalls == 0, "Idle expiry must leave the live instance alone.");
            await AssertAuthorizedAsync(origin, inboundPort, fingerprint, expiring);

            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(2);
            await store.WriteAsync(ProxyServerState.Empty with
            {
                Published = material, Credentials = [credentials[0] with { ExpiresAt = expiresAt }, credentials[1]]
            }, default);
            using var clockBudget = new CancellationTokenSource(Budget);
            while (DateTimeOffset.UtcNow < expiresAt) await Task.Delay(50, clockBudget.Token);
            try
            {
                await expiry.ReconcileAsync(default);
                throw new InvalidOperationException("The test transport did not lose the apply response.");
            }
            catch (ProxyServerException) when (transport.ApplyCalls == 1) { }
            TestAssert.Assert((await store.ReadAsync(default)).PendingPublication is not null,
                "Response loss must retain the original pending publication.");

            await AssertAuthorizedAsync(origin, inboundPort, fingerprint, retained);
            using (var deadline = new CancellationTokenSource(Budget))
            using (var denied = await ConnectAsync(inboundPort, fingerprint, deadline.Token))
            {
                await SendAsync(denied, originPort, expiring, deadline.Token);
                var received = new byte[1];
                try { TestAssert.Assert(await denied.ReadAsync(received, deadline.Token) == 0, "Expired UUID received a VLESS response."); }
                catch (IOException) { /* The real peer can reset the rejected connection. */ }
            }

            TestAssert.Assert(!origin.Pending(), "The expired UUID reached the origin.");
            var reopened = new ProxyServerStore(paths, protection);
            await new ProxyServerPublicationService(reopened, transport, new SystemMode()).ReconcileExpiryAsync(default);
            var confirmed = await reopened.ReadAsync(default);
            TestAssert.Assert(confirmed.PendingPublication is null && confirmed.Published!.Credentials.Single().Id == retainedId
                && transport.ApplyCalls == 1, "Recovery must confirm the applied material without a second mutation.");
            Console.WriteLine("PASS SING-BOX LIVE EXPIRY: pre-expiry forwarded, expired rejected, retained forwarded, lost reply recovered; apply=1");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task AssertAuthorizedAsync(TcpListener origin, int inboundPort, string fingerprint, Guid uuid)
    {
        using var deadline = new CancellationTokenSource(Budget);
        using var stream = await ConnectAsync(inboundPort, fingerprint, deadline.Token);
        await SendAsync(stream, ((IPEndPoint)origin.LocalEndpoint).Port, uuid, deadline.Token);
        using var accepted = await origin.AcceptTcpClientAsync(deadline.Token);
        var data = new byte[1];
        await accepted.GetStream().ReadExactlyAsync(data, deadline.Token);
        TestAssert.Assert(data[0] == 42, "The authorized VLESS request did not reach its origin.");
        await accepted.GetStream().WriteAsync(data, deadline.Token);
        var response = new byte[3];
        await stream.ReadExactlyAsync(response, deadline.Token);
        TestAssert.Assert(response.AsSpan().SequenceEqual(new byte[] { 0, 0, 42 }), "The authorized VLESS response was not forwarded.");
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
            var status = new ProxyServerStatus(true, true, true, false, "fixture", _hash, true, null);
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
