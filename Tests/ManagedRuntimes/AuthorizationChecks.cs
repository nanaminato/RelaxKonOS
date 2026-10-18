using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;

internal static class AuthorizationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Denied(string code, Func<Task> action)
        {
            try { await action(); throw new Exception("Authorization unexpectedly succeeded: " + code); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == code) { count++; }
        }
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-authorization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ProxyServerStore(new Paths(root), DataProtectionProvider.Create(new DirectoryInfo(root + "/keys")));
            var source = new Certificates();
            var provider = new ProxyServerRuntimeAuthorizationProvider(store, source);
            var empty = await provider.ReadAsync(default);
            Check(empty.Published is null && empty.ValidUntil > DateTimeOffset.UtcNow && empty.ValidUntil <= DateTimeOffset.UtcNow.AddSeconds(30),
                "Absent publication receives a short-lived empty grant");
            using var key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=proxy.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("proxy.example.com");
            certificateRequest.CertificateExtensions.Add(names.Build());
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            var inbound = new ProxyServerInbound(Guid.NewGuid(), "Published", ProxyServerProtocol.Trojan,
                "127.0.0.1", 24443, "proxy.example.com", 24443, "proxy.example.com", Guid.NewGuid());
            source.Current = new(inbound.Id, certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), "cert-1");
            var credential = new StoredCredential(Guid.NewGuid(), Guid.NewGuid(), "Credential", [inbound.Id], new string('a', 48), true, null);
            var published = new ProxyServerMaterial(new(3, [inbound]), [new(credential.Id, credential.InboundIds, credential.Value)], [], [source.Current]);
            var state = ProxyServerState.Empty with { Configuration = new(4, [inbound with { ListenPort = 24444 }]),
                Credentials = [credential, credential with { Id = Guid.NewGuid(), Value = new string('b', 48) }], Published = published };
            await store.WriteAsync(state, default);
            var grant = await provider.ReadAsync(default);
            Check(grant.Published!.Configuration.Revision == 3 && grant.Published.Configuration.Inbounds.Single().ListenPort == 24443
                && grant.Published.Credentials.Count == 1, "Saved inbound and new credential drafts never enter a runtime grant");
            Check(source.Reads == 1 && grant.Published.Certificates!.Single().Version == "cert-1", "Current certificate authorization is checked");
            await store.WriteAsync(state with { PendingPublication = published }, default);
            await Denied("runtime.publication_unconfirmed", async () => await provider.ReadAsync(default));
            foreach (var current in new[] { credential with { Enabled = false }, credential with { Value = "rotated" },
                credential with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, credential with { InboundIds = [] } })
            {
                await store.WriteAsync(state with { Credentials = [current] }, default);
                await Denied("runtime.credentials_revoked", async () => await provider.ReadAsync(default));
            }
            await store.WriteAsync(state with { Credentials = [] }, default);
            await Denied("runtime.credentials_revoked", async () => await provider.ReadAsync(default));
            var expires = DateTimeOffset.UtcNow.AddSeconds(15);
            await store.WriteAsync(state with { Credentials = [credential with { ExpiresAt = expires }] }, default);
            Check((await provider.ReadAsync(default)).ValidUntil == expires, "Grant cannot outlive its earliest credential");
            await store.WriteAsync(state, default);
            var original = source.Current;
            source.Current = original with { Version = "cert-2" };
            await Denied("runtime.certificate_changed", async () => await provider.ReadAsync(default));
            source.Unavailable = true;
            await Denied("runtime.certificate_unavailable", async () => await provider.ReadAsync(default));
            source.Unavailable = false; source.Current = original;
            await store.WriteAsync(state with { Published = published with { Certificates = [] } }, default);
            await Denied("runtime.certificate_unavailable", async () => await provider.ReadAsync(default));
            await store.WriteAsync(state, default);

            // Real OS pipe round trips; every challenge rereads Server state, never a cached grant.
            var reads = 0;
            await using (var server = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(Guid.NewGuid(), async ct => { reads++; return await provider.ReadAsync(ct); }))
            {
                var received = await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default);
                Check(received.Published!.Configuration.Revision == 3 && received.Published.Credentials.Single().Value == credential.Value,
                    "Authenticated local channel delivers actual protected material");
                await store.WriteAsync(state with { Credentials = [] }, default);
                await Denied("runtime.credentials_revoked", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
                Check(reads == 2, "Helper second read observes revocation after the first challenge");
                await Denied("runtime.authorization_unavailable", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, Guid.NewGuid(), default));
                await Denied("runtime.authorization_unavailable", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel with { Secret = new string('0', 64) }, server.Channel.OperationId, default));
                Check(reads == 2, "Wrong operation or secret cannot invoke the protected provider");
                await store.WriteAsync(state, default);
                Check((await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default)).Published is not null,
                    "Rejected channel clients do not poison the next authenticated request");
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var beforeReplay = reads;
                await RawChallengeAsync(server.Channel, nonce);
                try { await RawChallengeAsync(server.Channel, nonce); throw new Exception("Captured challenge was replayed"); }
                catch (EndOfStreamException) { count++; }
                Check(reads == beforeReplay + 1, "Authenticated nonce replay cannot reread protected material");
            }
            await using (var server = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(Guid.NewGuid(), _ => Task.FromException<SingBoxCurrentAuthorization>(new Exception("private-diagnostic"))))
                await Denied("runtime.authorization_unavailable", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
            await using (var server = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(Guid.NewGuid(), _ => Task.FromResult(new SingBoxCurrentAuthorization(null, DateTimeOffset.UtcNow.AddMinutes(-1)))))
                await Denied("runtime.authorization_unavailable", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
            await using (var server = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(Guid.NewGuid(), _ => Task.FromResult(new SingBoxCurrentAuthorization(null, DateTimeOffset.UtcNow.AddMinutes(6)))))
                await Denied("runtime.authorization_unavailable", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));

            var forgedChannel = new RuntimeAuthorizationChannel(Guid.NewGuid(), Guid.NewGuid(), "sing-box", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await using (var fake = new NamedPipeServerStream("RelaxKonOS.RuntimeAuthorization." + forgedChannel.ChannelId.ToString("N"),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var responding = Task.Run(async () =>
                {
                    await fake.WaitForConnectionAsync();
                    await ReadFrameAsync(fake);
                    await WriteFrameAsync(fake, new { Content = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                        { Grant = new SingBoxCurrentAuthorization(null, DateTimeOffset.UtcNow.AddSeconds(30)), ProblemCode = (string?)null })), Proof = new string('0', 64) });
                });
                await Denied("runtime.authorization_unavailable", async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(forgedChannel, forgedChannel.OperationId, default));
                await responding;
            }

            await store.WriteAsync(state, default);
            RuntimeAuthorizationChannel? observedChannel = null;
            var serviceOperation = Guid.NewGuid();
            var transport = new Transport(async (request, ct) =>
            {
                Check(store.Gate.CurrentCount == 0, "Lifecycle execution holds the same Server gate as configuration and credential mutation");
                observedChannel = request.RuntimeLifecycle!.Authorization!;
                Check((await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(observedChannel, serviceOperation, ct)).Published!.Configuration.Revision == 3,
                    "Production Server composition serves live authorization without gate recursion");
                return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new RuntimeSwitchResult(serviceOperation, RuntimeSwitchOutcome.Succeeded, null))));
            });
            var runtime = new SingBoxRuntimeService(transport, store, provider, new TestMode(ServerMode.System));
            var artifact = new RuntimeArtifact("fixture", new string('a', 64), 1024, "zip", "sing-box", "key", new(1024, 8, [new("sing-box", 4, new string('b', 64), true)]));
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var release = new RuntimeRelease("fixture", "sing-box", "v1.14.3", "1.14.3", RuntimeChannel.Stable, "fixture", target,
                artifact, SingBoxRuntimeSupport.Capability, true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, null);
            var plan = new RuntimeChangePlan(new(Guid.NewGuid(), "host", RuntimeChange.Install, 0, 1, 1, "fixture", "fixture"), "sing-box", release, null);
            Check((await runtime.ExecuteAsync(serviceOperation, plan, default)).Outcome == RuntimeSwitchOutcome.Succeeded && store.Gate.CurrentCount == 1,
                "Server composition returns Helper evidence and releases the domain gate");
            using (var ended = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try { await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(observedChannel!, serviceOperation, ended.Token); throw new Exception("Completed callback channel was reused"); }
                catch (OperationCanceledException) { count++; }
            }
            var user = new SingBoxRuntimeService(transport, store, provider, new TestMode(ServerMode.User));
            try { await user.QueryAsync(default); throw new Exception("User mode obtained a host runtime channel"); }
            catch (RuntimeLifecycleException ex) { Check(ex.ProblemCode == "runtime.platform_unsupported", "User mode cannot reach the Helper lifecycle"); }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }

    private static async Task RawChallengeAsync(RuntimeAuthorizationChannel channel, string nonce)
    {
        await using var pipe = new NamedPipeClientStream(".", "RelaxKonOS.RuntimeAuthorization." + channel.ChannelId.ToString("N"), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(deadline.Token);
        var context = Encoding.UTF8.GetBytes($"RelaxKonOS.RuntimeAuthorization.v2\n{channel.RuntimeId}\nclient\n{channel.ChannelId:N}\n{channel.OperationId:N}\n{nonce}\n");
        var proof = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(channel.Secret), context));
        await WriteFrameAsync(pipe, new { OperationId = channel.OperationId, Nonce = nonce, Proof = proof });
        await ReadFrameAsync(pipe);
    }
    private static async Task WriteFrameAsync<T>(Stream stream, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header); await stream.WriteAsync(bytes); await stream.FlushAsync();
    }
    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header);
        var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await stream.ReadExactlyAsync(bytes); return bytes;
    }
    private sealed class Transport(Func<PrivilegedOperationRequest, CancellationToken, Task<PrivilegedOperationResult>> execute) : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default) => execute(request, ct);
    }
    private sealed class TestMode(ServerMode mode) : IServerModeResolver
    {
        public ServerMode Mode => mode;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => mode == ServerMode.System;
    }

    private sealed class Certificates : IProxyServerCertificateSource
    {
        internal ProxyServerTlsMaterial Current = null!;
        internal bool Unavailable;
        internal int Reads;
        public Task<ProxyServerTlsMaterial> ReadAsync(ProxyServerInbound inbound, CancellationToken ct)
        {
            Reads++; ct.ThrowIfCancellationRequested();
            if (Unavailable) throw new ProxyServerException("proxy_server.certificate_invalid");
            return Task.FromResult(Current);
        }
    }
    private sealed class Paths(string root) : IProxyPlatformPaths
    {
        public string GetEngineVersionsDirectory(string engineId) => root + "/versions";
        public string GetProtectedConfigurationDirectory() => root + "/config";
        public string GetEngineDataDirectory(string engineId) => root + "/engine";
        public string GetStateDirectory() => root + "/state";
        public string GetSanitizedLogDirectory() => root + "/logs";
    }
}
