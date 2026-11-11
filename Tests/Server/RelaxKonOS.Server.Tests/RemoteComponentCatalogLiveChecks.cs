using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ModulePackaging;

internal static class RemoteComponentCatalogLiveChecks
{
    public static async Task RunAsync(string address)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Run isolated CA/HTTPS acceptance on the authorized Linux host.");
        var ip = IPAddress.Parse(address);
        RuntimeNetworkPolicy.ValidateGrant(new(address + "/32", 443));
        using var fixture = new RemoteComponentCatalogChecks.Fixture();
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        fixture.Publish(1);
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Isolated catalog acceptance CA", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=Isolated catalog HTTPS", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(ip); leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var leafPublic = leafRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(4), RandomNumberGenerator.GetBytes(16));
        using var leaf = leafPublic.CopyWithPrivateKey(leafKey);
        using var rogueKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rogueRequest = new CertificateRequest("CN=Untrusted replacement", rogueKey, HashAlgorithmName.SHA256);
        rogueRequest.CertificateExtensions.Add(san.Build());
        using var rogue = rogueRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(4));
        var mode = Path.Combine(fixture.Root, "mode"); File.WriteAllText(mode, "normal");
        var counter = Path.Combine(fixture.Root, "connections"); File.WriteAllText(counter, "0");
        var caPath = Path.Combine(fixture.Root, "test-ca.pem"); File.WriteAllText(caPath, root.ExportCertificatePem());
        var emptyRoots = Path.Combine(fixture.Root, "empty-roots"); Directory.CreateDirectory(emptyRoots);
        await using var server = new HttpsServer(ip, leaf, rogue, fixture.Transport.Files, mode, counter);
        var policy = fixture.Policy() with { BaseUri = $"https://{address}:{server.Port}/catalog/", PrivateNetworks = [new(address + "/32", server.Port)] };
        var policyPath = Path.Combine(fixture.Root, "policy.json");
        File.WriteAllBytes(policyPath, JsonSerializer.SerializeToUtf8Bytes(policy, ModulePackageJson.Options));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(RemoteComponentCatalogLiveChecks).Assembly.Location);
        start.ArgumentList.Add("--component-sources-http-child"); start.ArgumentList.Add(policyPath); start.ArgumentList.Add(fixture.Root);
        // Trust is confined to the child test process. Never modify the system certificate store.
        start.Environment["SSL_CERT_FILE"] = caPath; start.Environment["SSL_CERT_DIR"] = emptyRoots;
        using var child = Process.Start(start) ?? throw new Exception("Unable to start HTTPS test child");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { child.Kill(entireProcessTree: true); throw; }
        Console.Write(await output); Console.Write(await error);
        if (child.ExitCode != 0) throw new Exception("Real HTTPS acceptance failed");
        if (server.SawCredentials) throw new Exception("A catalog request leaked authorization headers");
        Console.WriteLine("Real HTTPS source acceptance passed; isolated CA removed with fixture, no machine trust-store changes.");
    }

    public static async Task RunChildAsync(string policyPath, string root)
    {
        var policy = ModulePackageJson.Read<ComponentCatalogSourcePolicy>(File.ReadAllBytes(policyPath));
        var stateRoot = Path.Combine(root, "live-trust");
        using var store = new ComponentCatalogSourceStore(stateRoot, "isolated-https-client");
        store.Approve(policy, store.Revision, policy.Fingerprint);
        await store.RefreshAsync(policy.SourceId);
        var count = 1;
        var source = new HttpCatalogSource(store, policy.SourceId); source.Load();
        await using (var input = await source.OpenAsync("../packages/app.roapp", source.ManifestReference("example.app")))
        { if (input.Length != 3) throw new Exception("Wrong artifact bytes"); count++; }
        var mode = Path.Combine(root, "mode"); var connections = Path.Combine(root, "connections");
        async Task Refuse(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is RuntimePolicyException or IOException or OperationCanceledException) { count++; return; }
            throw new Exception("An invalid network source was accepted");
        }
        File.WriteAllText(mode, "bad-metadata"); await Refuse(() => store.RefreshAsync(policy.SourceId));
        File.WriteAllText(mode, "bad-artifact");
        foreach (var file in Directory.GetFiles(Path.Combine(stateRoot, "cache"))) File.Delete(file);
        await Refuse(async () => { await using var file = await source.OpenAsync("../packages/app.roapp", source.ManifestReference("example.app")); });
        File.WriteAllText(mode, "bad-tls"); await Refuse(() => store.RefreshAsync(policy.SourceId));
        File.WriteAllText(mode, "normal");
        var denied = policy with { SourceId = "ungranted-source", PrivateNetworks = [] };
        store.Approve(denied, store.Revision, denied.Fingerprint);
        var prior = File.ReadAllText(connections);
        await Refuse(() => store.RefreshAsync(denied.SourceId));
        if (File.ReadAllText(connections) != prior) throw new Exception("Private-network refusal happened after connecting"); count++;
        var redirected = policy with { SourceId = "redirect-source", CatalogPath = "redirect.json" };
        store.Approve(redirected, store.Revision, redirected.Fingerprint);
        var before = int.Parse(File.ReadAllText(connections), System.Globalization.CultureInfo.InvariantCulture);
        await Refuse(() => store.RefreshAsync(redirected.SourceId));
        if (int.Parse(File.ReadAllText(connections), System.Globalization.CultureInfo.InvariantCulture) != before + 1)
            throw new Exception("Redirect escaped the approved origin"); count++;
        var oversized = policy with { SourceId = "large-source", CatalogPath = "large.json" };
        store.Approve(oversized, store.Revision, oversized.Fingerprint); await Refuse(() => store.RefreshAsync(oversized.SourceId));
        var slow = policy with { SourceId = "slow-source", CatalogPath = "slow.json" };
        store.Approve(slow, store.Revision, slow.Fingerprint);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250))) await Refuse(() => store.RefreshAsync(slow.SourceId, cancel.Token));
        if (store.Snapshot(policy.SourceId).Publication.Sequence != 1) throw new Exception("Network failure erased accepted state"); count++;
        Console.WriteLine($"Real HTTPS catalog checks passed: {count} (signature, bytes, TLS, private network, redirect, size, cancellation, retained cache)");
    }

    private sealed class HttpsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly List<Task> _clients = [];
        private readonly X509Certificate2 _good, _bad;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly string _mode, _counter;
        private int _connections;
        public bool SawCredentials;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public HttpsServer(IPAddress ip, X509Certificate2 good, X509Certificate2 bad, IReadOnlyDictionary<string, byte[]> files, string mode, string counter)
        {
            _good = good; _bad = bad; _files = files; _mode = mode; _counter = counter;
            _listener = new(ip, 0); _listener.Start(); _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    File.WriteAllText(_counter, Interlocked.Increment(ref _connections).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _clients.Add(ServeAsync(client));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            await using (var tls = new SslStream(client.GetStream()))
            {
                try
                {
                    var mode = File.ReadAllText(_mode);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = mode == "bad-tls" ? _bad : _good,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token) ?? "";
                    var path = request.Split(' ').ElementAtOrDefault(1) ?? "";
                    int bytes = 0;
                    for (string? header; !string.IsNullOrEmpty(header = await reader.ReadLineAsync(_stop.Token));)
                    {
                        if ((bytes += header.Length) > 16384) throw new IOException("Oversized request");
                        if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
                            || header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)) SawCredentials = true;
                    }
                    if (path == "/catalog/slow.json") { await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token); return; }
                    if (path == "/catalog/redirect.json")
                    { await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1:{Port}/outside\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token); return; }
                    if (path == "/catalog/large.json")
                    { await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {RemoteCatalogRules.EnvelopeLimit + 1}\r\nConnection: close\r\n\r\n"), _stop.Token); return; }
                    var relative = path.StartsWith("/catalog/", StringComparison.Ordinal) ? path[9..] : "";
                    var body = _files.TryGetValue(relative, out var content) ? content.ToArray() : [];
                    if (body.Length > 0 && (mode == "bad-metadata" && relative == "manifests/app.json" || mode == "bad-artifact" && relative.EndsWith(".roapp"))) body[0] ^= 1;
                    await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await tls.WriteAsync(body, _stop.Token);
                }
                catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _accept;
            await Task.WhenAll(_clients); _stop.Dispose();
        }
    }
}
