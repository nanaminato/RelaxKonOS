using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Docker;

/// <summary>Operations that download nothing still have to name the route they are not using.</summary>
internal sealed class DirectDownloads : RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider
{
    public Task<RuntimeDownloadProxy?> ResolveAsync(CancellationToken ct) => Task.FromResult<RuntimeDownloadProxy?>(null);
}

/// <summary>A saved host preference the Server has to interpret, including the refusals.</summary>
internal sealed class SavedProxyResolution(DockerProxyResolution value) : IDockerProxyResolver
{
    public Task<DockerProxyResolution> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    public void Invalidate() { }
}

/// <summary>
/// Host download proxy and bounded retry. The transport checks run against a real HTTPS origin on a
/// private address behind a counting forwarder and a real CONNECT proxy, so attempt counts and the
/// bytes a proxy or origin observes are measured rather than assumed.
/// </summary>
internal static class DownloadProxyChecks
{
    private static readonly string[] BypassSamples = ["localhost", ".internal.example.test", "mirror.internal.example.test:8443"];

    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject(string? endpoint, string? bypass, string name)
        {
            try { RuntimeDownloadPolicy.Create(endpoint, bypass); throw new Exception("Accepted " + name); }
            catch (RuntimePolicyException ex) { if (ex.ProblemCode != "runtime.download_proxy_invalid") throw; count++; }
        }

        // The route itself: only a plain HTTP(S) proxy endpoint with a host and no path.
        var plain = RuntimeDownloadPolicy.Create("http://127.0.0.1:7890", "");
        Check(plain.Endpoint == "http://127.0.0.1:7890" && plain.Bypass!.Count == 0, "A loopback proxy endpoint is a valid route");
        Check(RuntimeDownloadPolicy.Create("  http://127.0.0.1:7890  ", null).Endpoint == "http://127.0.0.1:7890",
            "A saved value is trimmed instead of rejected for surrounding padding");
        var credentialed = RuntimeDownloadPolicy.Create("https://user:p%40ss@proxy.example.test:8443",
            "localhost; .internal.example.test;mirror.internal.example.test:8443");
        Check(credentialed.Bypass!.SequenceEqual(BypassSamples), "Bypass entries are normalized from the saved separator styles");
        Reject("", null, "an empty endpoint");
        Reject("127.0.0.1:7890", null, "an endpoint without a scheme");
        Reject("socks5://127.0.0.1:1080", null, "a SOCKS proxy the transport cannot carry");
        Reject("http://127.0.0.1:0", null, "a zero port");
        Reject("http://127.0.0.1:7890/proxy", null, "a proxy endpoint with a path");
        Reject("http://127.0.0.1:7890/?x=1", null, "a proxy endpoint with a query");
        Reject("http://127.0.0.1:7890#fragment", null, "a proxy endpoint with a fragment");
        Reject("http://127.0.0.1:78 90", null, "an endpoint with an embedded space");
        Reject("http://" + new string('a', 600) + ":8080", null, "an over-long endpoint");
        Reject("http://127.0.0.1:7890", string.Join(';', Enumerable.Repeat("host", 33)), "more bypass entries than the closed limit");
        Reject("http://127.0.0.1:7890", "bad_host!", "a bypass entry that is not a host");
        Reject("http://127.0.0.1:7890", "host:99999", "a bypass entry with an impossible port");
        Reject("http://127.0.0.1:7890", "host:port", "a bypass entry with a non-numeric port");

        // Bypass matching decides per target, never per transfer.
        var route = RuntimeDownloadPolicy.Create("http://127.0.0.1:7890", string.Join(';', BypassSamples));
        Check(RuntimeDownloadPolicy.Bypasses(route, new Uri("https://mirror.internal.example.test/catalog/manifest.json")),
            "A bypass entry names its own host");
        Check(RuntimeDownloadPolicy.Bypasses(route, new Uri("https://cdn.mirror.internal.example.test/x.json")),
            "A bypass entry also names its subdomains");
        Check(!RuntimeDownloadPolicy.Bypasses(route, new Uri("https://notmirrorinternal.example.test/x.json")),
            "A suffix match needs a label boundary");
        var portScoped = RuntimeDownloadPolicy.Create("http://127.0.0.1:7890", "mirror.internal.example.test:8443");
        Check(RuntimeDownloadPolicy.Bypasses(portScoped, new Uri("https://mirror.internal.example.test:8443/x.json")),
            "A port-scoped bypass entry covers its own port");
        Check(!RuntimeDownloadPolicy.Bypasses(portScoped, new Uri("https://mirror.internal.example.test:9443/x.json")),
            "A port-scoped bypass entry does not cover another port");
        Check(!RuntimeDownloadPolicy.Bypasses(plain, new Uri("http://anything.example.test/")), "No bypass list never bypasses");
        Check(RuntimeDownloadPolicy.Describe(credentialed) == "https://proxy.example.test:8443",
            "Diagnostics never echo proxy credentials");
        var (address, user, password) = RuntimeDownloadPolicy.Decompose(credentialed);
        Check(address.AbsoluteUri == "https://proxy.example.test:8443/" && address.UserInfo.Length == 0
            && user == "user" && password == "p@ss", "Proxy credentials are split from the tunnel address");

        // Retry decisions are closed: transport faults repeat, trust and policy refusals never do.
        foreach (var code in new[] { "runtime.network_unavailable", "runtime.source_timeout", "runtime.source_rate_limited", "runtime.source_unavailable" })
            Check(RuntimeDownloadPolicy.IsRetryable(code), "Transport fault is retryable: " + code);
        foreach (var code in new[] { "runtime.address_forbidden", "runtime.redirect_forbidden", "runtime.source_disabled",
                     "runtime.source_http_failed", "runtime.source_tls_invalid", "runtime.artifact_integrity_failed", null })
            Check(!RuntimeDownloadPolicy.IsRetryable(code), "Policy refusal is final: " + (code ?? "none"));
        var now = DateTimeOffset.UtcNow;
        Check(RuntimeDownloadPolicy.Delay(1, now, null) == TimeSpan.FromMilliseconds(500)
            && RuntimeDownloadPolicy.Delay(2, now, null) == TimeSpan.FromMilliseconds(1500),
            "Backoff grows with the attempt instead of hammering the source");
        Check(RuntimeDownloadPolicy.Delay(2, now, now.AddMinutes(5)) == TimeSpan.FromSeconds(10),
            "A hostile Retry-After cannot stall an operation past the cap");
        Check(RuntimeDownloadPolicy.Delay(1, now, now.AddSeconds(-30)) == TimeSpan.Zero,
            "An already-elapsed Retry-After does not delay the next attempt");
        Check(RuntimeDownloadPolicy.MaximumAttempts == 3, "A source sees one attempt plus two retries");

        // Server interpretation of the saved preference.
        async Task<RuntimeDownloadProxy?> Resolve(DockerProxyResolution resolution) =>
            await new RelaxKonOS.Server.ManagedRuntimes.RuntimeDownloadProxyProvider(() => new SavedProxyResolution(resolution)).ResolveAsync(default);
        DockerProxyResolution Saved(bool enabled, bool apply, string proxy, string problem = "", string noProxy = "") =>
            new(enabled, DockerProxySource.Custom, proxy, proxy, noProxy, false, false, false, apply, "", false, problem);
        Check(await Resolve(Saved(false, true, "http://127.0.0.1:7890")) is null, "A disabled preference keeps the direct route");
        Check(await Resolve(Saved(true, false, "http://127.0.0.1:7890")) is null, "A preference that excludes runtime downloads keeps the direct route");
        var savedRoute = await Resolve(Saved(true, true, "http://127.0.0.1:7890", noProxy: "localhost"));
        Check(savedRoute is { Endpoint: "http://127.0.0.1:7890" } && savedRoute.Bypass!.SequenceEqual(["localhost"]),
            "A usable preference becomes the operation route with its bypass list");
        await Denied("runtime.download_proxy_unavailable", () => Resolve(Saved(true, true, "http://127.0.0.1:7890", "managed-proxy-unavailable")));
        await Denied("runtime.download_proxy_invalid", () => Resolve(Saved(true, true, "socks5://127.0.0.1:1080")));

        // Helper boundary: the route belongs to the verbs that fetch bytes.
        var misplaced = await MihomoRuntimeOperations.ExecuteAsync(new PrivilegedOperationRequest(
            PrivilegedOperationKind.MihomoRuntimeRecoveryQuery,
            DownloadProxy: RuntimeDownloadPolicy.Create("http://127.0.0.1:7890", "")));
        Check(!misplaced.Success && misplaced.ProblemCode == PrivilegedProblemCode.InvalidRequest,
            "A read that fetches nothing may not name a download route");

        // Helper boundary: the resolved endpoint is re-validated before any socket is opened.
        var sourceStoreRoot = Path.Combine(Path.GetTempPath(), "relaxkonos-proxy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuntimeSourceStore(sourceStoreRoot, path => Directory.CreateDirectory(path));
            var source = Source(IPAddress.Parse("10.0.0.1"), 443);
            store.Confirm(source, 0, source.ConfirmationFingerprint, "test-admin", DateTimeOffset.UtcNow);
            var invalidRoute = await RuntimeSourceOperations.ExecuteAsync(new(PrivilegedOperationKind.RuntimeCatalogRefresh,
                RuntimeSourceId: source.SourceId,
                DownloadProxy: new RuntimeDownloadProxy("http://127.0.0.1:7890/path", [])), store,
                (_, _, _) => Task.FromResult(Array.Empty<byte>()));
            Check(!invalidRoute.Success && invalidRoute.Error == "runtime.download_proxy_invalid",
                "A hand-built route is refused by the Helper, not by the network");
            RuntimeDownloadProxy? forwarded = null;
            await RuntimeSourceOperations.ExecuteAsync(new(PrivilegedOperationKind.RuntimeCatalogRefresh,
                RuntimeSourceId: source.SourceId,
                DownloadProxy: RuntimeDownloadPolicy.Create("http://127.0.0.1:7890", "mirror.example.test")), store,
                (_, route, _) => { forwarded = route; throw new RuntimePolicyException("runtime.network_unavailable"); });
            Check(forwarded is { Endpoint: "http://127.0.0.1:7890" }, "The Helper hands the validated route to the downloader");
            var refusedRead = await RuntimeSourceOperations.ExecuteAsync(new(PrivilegedOperationKind.RuntimeCatalogGet,
                RuntimeSourceId: source.SourceId,
                DownloadProxy: RuntimeDownloadPolicy.Create("http://127.0.0.1:7890", "")), store,
                (_, _, _) => Task.FromResult(Array.Empty<byte>()));
            Check(!refusedRead.Success && refusedRead.ProblemCode == PrivilegedProblemCode.InvalidRequest,
                "A cached read may not carry a route it would ignore");
        }
        finally { if (Directory.Exists(sourceStoreRoot)) Directory.Delete(sourceStoreRoot, true); }

        // Live transport: real HTTPS origins behind counting forwarders plus a real CONNECT proxy.
        using var trustedOrigin = PrivateOrigin.Create();
        using var rejectedOrigin = PrivateOrigin.Create();
        if (trustedOrigin is null || rejectedOrigin is null)
            Console.WriteLine("Download proxy: transport probes SKIPPED (this host has no private IPv4 address for a granted mirror)");
        else
        {
            await trustedOrigin.StartAsync();
            await rejectedOrigin.StartAsync();
            var trustedSource = Source(trustedOrigin.Address, trustedOrigin.Port);
            var rejectedSource = Source(rejectedOrigin.Address, rejectedOrigin.Port);

            // The trust override has to be in place before this process performs its first handshake.
            if (TryTrust(trustedOrigin))
            {
                try
                {
                    using var relay = new ConnectProxy(true);
                    var payload = await new RuntimeMetadataDownloader().DownloadAsync(trustedSource,
                        RuntimeDownloadPolicy.Create(relay.Endpoint, ""), default);
                    Check(payload.SequenceEqual(trustedOrigin.Payload), "A trusted origin is reached through the configured proxy");
                    Check(relay.Attempts == 1 && trustedOrigin.Requests == 1, "A successful proxied download is attempted once");
                    Check(relay.LastHeadersString.StartsWith("CONNECT " + trustedOrigin.Address + ":" + trustedOrigin.Port, StringComparison.Ordinal),
                        "The proxy is asked to tunnel to the declared origin");
                    Check(trustedOrigin.LastRequestHeaders.All(value => !value.Key.Contains("Authorization", StringComparison.OrdinalIgnoreCase)),
                        "The origin request carries no proxy or origin authorization");
                    var directPayload = await new RuntimeMetadataDownloader().DownloadAsync(trustedSource, null, default);
                    Check(directPayload.SequenceEqual(trustedOrigin.Payload) && trustedOrigin.Requests == 2,
                        "A direct download still works when no route is configured");
                }
                catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.source_tls_invalid")
                {
                    Console.WriteLine("Download proxy: trusted transport probe SKIPPED (this process cannot adopt the test CA)");
                }
            }
            else Console.WriteLine("Download proxy: trusted transport probe SKIPPED (this host has no per-process CA override)");

            var directFailure = await Failure(() => new RuntimeMetadataDownloader().DownloadAsync(rejectedSource, null, default));
            Check(directFailure == "runtime.source_tls_invalid",
                "An untrusted certificate is a trust decision, not a transient fault: " + directFailure);
            Check(rejectedOrigin.Accepted == 1, "A rejected handshake is not retried: " + rejectedOrigin.Accepted);

            using var tunnel = new ConnectProxy(true);
            var tunnelBefore = rejectedOrigin.Accepted;
            var tunnelFailure = await Failure(() => new RuntimeMetadataDownloader().DownloadAsync(rejectedSource,
                RuntimeDownloadPolicy.Create(tunnel.Endpoint, ""), default));
            Check(tunnelFailure == "runtime.source_tls_invalid",
                "A tunnel that reaches the origin fails only on trust, not on routing: " + tunnelFailure);
            Check(tunnel.Attempts == 1 && rejectedOrigin.Accepted == tunnelBefore + 1,
                "The proxy tunnel carried exactly one handshake to the origin and was not retried");

            using var rejecting = new ConnectProxy(false);
            var proxiedFailure = await Failure(() => new RuntimeMetadataDownloader().DownloadAsync(rejectedSource,
                RuntimeDownloadPolicy.Create(rejecting.Endpoint, ""), default));
            Check(proxiedFailure == "runtime.network_unavailable", "A failing proxy tunnel is reported as a network fault: " + proxiedFailure);
            Check(rejecting.Attempts == RuntimeDownloadPolicy.MaximumAttempts,
                "A transient tunnel failure is retried exactly to the attempt limit: " + rejecting.Attempts);
            Check(rejecting.LastTarget == rejectedOrigin.Address + ":" + rejectedOrigin.Port,
                "The proxy is asked for the declared origin, not for anything it chose");
            Check(!rejecting.LastHeadersString.Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase),
                "A route without credentials sends no proxy authorization");

            using var bypassed = new ConnectProxy(false);
            var before = rejectedOrigin.Accepted;
            var bypassFailure = await Failure(() => new RuntimeMetadataDownloader().DownloadAsync(rejectedSource,
                RuntimeDownloadPolicy.Create(bypassed.Endpoint, rejectedOrigin.Address.ToString()), default));
            Check(bypassed.Attempts == 0, "A bypassed target never touches the proxy");
            Check(bypassFailure == "runtime.source_tls_invalid" && rejectedOrigin.Accepted == before + 1,
                "A bypassed target is contacted directly, once");

            using var credentialedProxy = new ConnectProxy(false, challengeCredentials: true);
            var authenticated = RuntimeDownloadPolicy.Create(
                credentialedProxy.Endpoint.Replace("http://", "http://relay:secret@", StringComparison.Ordinal), "");
            var authFailure = await Failure(() => new RuntimeMetadataDownloader().DownloadAsync(rejectedSource, authenticated, default));
            Check(authFailure == "runtime.network_unavailable", "Proxy credentials do not change the reported fault: " + authFailure);
            var heads = credentialedProxy.Heads;
            Check(heads.Length >= 2 && !heads[0].Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase),
                "Proxy credentials are not volunteered before the proxy asks for them");
            Check(heads[^1].Contains("Proxy-Authorization: Basic", StringComparison.OrdinalIgnoreCase),
                "Proxy credentials are offered to the proxy connection itself");
            Check(heads.All(head => head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("Authorization", StringComparison.OrdinalIgnoreCase))
                    .All(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))),
                "Only the proxy connection sees credentials; the origin request carries none");
        }

        return count;
    }

    /// <summary>
    /// Trusts the throwaway origin certificate for this process only. Linux reaches OpenSSL's default
    /// verify paths through the standard file variable; Windows offers no per-process equivalent, where the
    /// success-path check is reported as skipped instead of pretending the route works.
    /// </summary>
    private static bool TryTrust(PrivateOrigin origin)
    {
        if (!OperatingSystem.IsLinux()) return false;
        Environment.SetEnvironmentVariable("SSL_CERT_FILE", origin.CertificatePath);
        return true;
    }

    private static async Task<string> Failure(Func<Task> action)
    {
        try { await action(); return "no failure"; }
        catch (RuntimePolicyException ex) { return ex.ProblemCode; }
    }

    private static async Task Denied(string code, Func<Task> action)
    {
        try { await action(); throw new Exception("Expected " + code); }
        catch (RelaxKonOS.Server.ManagedRuntimes.RuntimeLifecycleException ex) when (ex.ProblemCode == code) { }
    }

    private static RuntimeSource Source(IPAddress address, int port) => new("mirror-catalog", "sing-box", "test",
        RuntimeSourceKind.HttpsManifest, $"https://{address}:{port}/catalog/", "manifest.json", "key-1",
        Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo()), true, 0,
        [RuntimeChannel.Stable], 65536, 86400, [$"https://{address}:{port}/artifacts/"], [],
        [new RuntimePrivateNetwork(address + "/32", port)]);

    /// <summary>
    /// A trusted HTTPS endpoint on a private address, which is the only kind of address an administrator
    /// can grant. Connections are counted by a forwarder so attempts are observed rather than assumed.
    /// </summary>
    private sealed class PrivateOrigin : IDisposable
    {
        private readonly TcpListener forwarder;
        private readonly CancellationTokenSource stopped = new();
        private X509Certificate2? certificate;
        private WebApplication? app;
        private Task forwarding = Task.CompletedTask;
        internal byte[] Payload { get; } = Encoding.UTF8.GetBytes("{\"revision\":1}");
        internal int Accepted;
        internal int Requests;
        /// <summary>The headers of the last request this origin actually served.</summary>
        internal IReadOnlyList<KeyValuePair<string, string>> LastRequestHeaders { get; private set; } = [];
        internal string CertificatePath { get; private set; } = string.Empty;
        internal IPAddress Address { get; }
        internal int Port => ((IPEndPoint)forwarder.LocalEndpoint).Port;

        private PrivateOrigin(IPAddress address)
        {
            Address = address;
            forwarder = new TcpListener(address, 0);
        }

        internal static PrivateOrigin? Create()
        {
            var address = PrivateAddress();
            return address is null ? null : new PrivateOrigin(address);
        }

        /// <summary>Granted mirrors are administrator-declared private networks; loopback is never grantable.</summary>
        private static IPAddress? PrivateAddress()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(item => item.GetIPProperties().UnicastAddresses)
                    .Select(item => item.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address));
            }
            catch (NetworkInformationException) { return null; }
        }

        private static bool IsPrivate(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        }

        internal async Task StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + Address, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(Address);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            CertificatePath = Path.Combine(Path.GetTempPath(), "relaxkonos-origin-" + Guid.NewGuid().ToString("N") + ".pem");
            File.WriteAllText(CertificatePath, selfSigned.ExportCertificatePem());
            certificate = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(Address, 0, listen => listen.UseHttps(certificate)));
            app = builder.Build();
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref Requests);
                LastRequestHeaders = context.Request.Headers
                    .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value ?? string.Empty)))
                    .ToArray();
                await next();
            });
            app.MapGet("/catalog/manifest.json", () => Results.Bytes(Payload, "application/json"));
            await app.StartAsync();
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var bound = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses
                .Select(value => new Uri(value)).First().Port;
            forwarder.Start();
            forwarding = ForwardAsync(bound);
        }

        /// <summary>Counts accepted TLS connections while relaying them to the Kestrel listener.</summary>
        private async Task ForwardAsync(int port)
        {
            while (!stopped.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await forwarder.AcceptTcpClientAsync(stopped.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                Interlocked.Increment(ref Accepted);
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var upstream = new TcpClient())
                    {
                        try
                        {
                            await upstream.ConnectAsync(Address, port, stopped.Token);
                            var inward = client.GetStream().CopyToAsync(upstream.GetStream(), stopped.Token);
                            var outward = upstream.GetStream().CopyToAsync(client.GetStream(), stopped.Token);
                            await Task.WhenAny(inward, outward);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    }
                }, stopped.Token);
            }
        }

        public void Dispose()
        {
            stopped.Cancel();
            forwarder.Stop();
            try { forwarding.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            if (app is not null)
            {
                try { app.StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
                try { app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            }
            certificate?.Dispose();
            stopped.Dispose();
            try { if (File.Exists(CertificatePath)) File.Delete(CertificatePath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A real HTTP CONNECT endpoint. It records what a proxy would be asked for, answers with a failure or
    /// relays bytes, and counts attempts, so a retry policy is observed at the socket instead of trusted.
    /// </summary>
    private sealed class ConnectProxy : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly bool tunnel;
        private readonly bool challengeCredentials;
        private readonly Task serving;
        private readonly List<string> headers = [];
        internal int Attempts;
        internal string? LastTarget;
        internal string LastHeadersString = string.Empty;

        internal string Endpoint => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        /// <summary>Every CONNECT head this proxy received, in arrival order.</summary>
        internal string[] Heads { get { lock (headers) return [.. headers]; } }

        internal ConnectProxy(bool tunnel, bool challengeCredentials = false)
        {
            this.tunnel = tunnel;
            this.challengeCredentials = challengeCredentials;
            listener.Start();
            serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!stopped.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stopped.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(() => HandleAsync(client), stopped.Token);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream, stopped.Token);
                    if (head is null) return;
                    var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0 || !lines[0].StartsWith("CONNECT ", StringComparison.Ordinal)) return;
                    var parts = lines[0].Split(' ');
                    Interlocked.Increment(ref Attempts);
                    LastTarget = parts[1];
                    LastHeadersString = head;
                    lock (headers) headers.Add(head);
                    // A real proxy asks for credentials before it refuses; the transport must answer with its
                    // own proxy authorization and never with an origin credential.
                    if (challengeCredentials && !head.Contains("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"relaxkonos\"\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), stopped.Token);
                        return;
                    }
                    if (!tunnel)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"), stopped.Token);
                        return;
                    }
                    var separator = parts[1].LastIndexOf(':');
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Parse(parts[1][..separator]),
                        int.Parse(parts[1][(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture), stopped.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), stopped.Token);
                    var up = upstream.GetStream();
                    var inward = stream.CopyToAsync(up, stopped.Token);
                    var outward = up.CopyToAsync(stream, stopped.Token);
                    await Task.WhenAny(inward, outward);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                    or ObjectDisposedException or ArgumentException or InvalidOperationException) { }
            }
        }

        private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new List<byte>(1024);
            var single = new byte[1];
            while (buffer.Count < 8192)
            {
                if (await stream.ReadAsync(single, ct) == 0) return null;
                buffer.Add(single[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                    return Encoding.ASCII.GetString([.. buffer]);
            }
            return null;
        }

        public void Dispose()
        {
            stopped.Cancel();
            listener.Stop();
            try { serving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            stopped.Dispose();
        }
    }
}
