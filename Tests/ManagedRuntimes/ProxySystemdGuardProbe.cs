using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting.Internal;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Privileged;

internal sealed class ProxySystemdGuardProbe(Process mihomo, int proxyPort, ProxyTunSafetyService safety, ProxyManagementRouteSnapshot route, string rules) : IAsyncDisposable
{
    private const string EnvironmentPath = "/etc/environment";
    private const string JournalPath = "/var/lib/relaxkonos-system-proxy/recovery.json";
    private const string Device = "rk-mb04-tun";
    private const string Original = "http://127.0.0.1:1234";
    private string ProxyUrl => $"http://127.0.0.1:{proxyPort}";

    internal static async Task<ProxySystemdGuardProbe> StartAsync(string work, string root)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var paths = new FixturePaths(Path.Combine(root, "server"));
        Directory.CreateDirectory(paths.GetStateDirectory());
        var config = Path.Combine(Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory()).FullName, "active.yaml");
        var protection = ManagedServiceChecks.OpenProtection(root);
        var secrets = new DataProtectionProxyControllerSecretStore(
            new HostingEnvironment { ContentRootPath = Path.Combine(root, "full-server") }, protection);
        var secret = await secrets.GetOrCreateAsync(default);
        var settings = new ProxySettingsDto(true, false, false, true, false, "error", number,
            Tun: ProxyTunSettingsDto.Default with { DeviceName = Device, AutoRoute = true, AutoDetectInterface = false },
            SystemProxy: new(GuardEnabled: true, GuardIntervalSeconds: 5));
        await File.WriteAllTextAsync(Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json"), JsonSerializer.Serialize(settings));
        var platform = new HostProxyNetworkSafetyPlatform();
        var address = IPAddress.Parse("192.0.2.3");
        var route = await platform.CaptureManagementRouteAsync(address, default)
            ?? throw new InvalidOperationException("The fixture has no management route.");
        var rules = await ReadRulesAsync();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var controller = new MihomoControllerClient(http, secrets, new MihomoControllerOptions());
        var runtime = new MihomoTunRuntimeController(paths, new ProxySettingsReader(paths), controller,
            new MihomoPublicationStore(paths, protection), new HostProxyTunDeviceObserver());
        var safety = new ProxyTunSafetyService(paths, platform, runtime);
        await File.WriteAllTextAsync(config, $"mixed-port: {number}\nallow-lan: false\nbind-address: 127.0.0.1\nmode: direct\nlog-level: error\nexternal-controller: 127.0.0.1:9090\nsecret: {JsonSerializer.Serialize(secret)}\ntun:\n  enable: false\n");
        var start = new ProcessStartInfo(Path.Combine(work, "mihomo")) { UseShellExecute = false };
        foreach (var arg in new[] { "-d", work, "-f", config }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated Mihomo.");
        var probe = new ProxySystemdGuardProbe(process, number, safety, route, rules);
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!process.HasExited)
            {
                using var listener = new TcpClient();
                try { await listener.ConnectAsync(IPAddress.Loopback, number, budget.Token); break; }
                catch (SocketException) { await Task.Delay(50, budget.Token); }
            }
            if (process.HasExited) throw new InvalidOperationException("Isolated Mihomo exited before opening its proxy listener.");
            var problem = await safety.EnableAsync(Guid.NewGuid(), address, budget.Token);
            if (problem is not null || !TunActive() || await ReadRulesAsync() == rules)
                throw new InvalidOperationException($"The real TUN activation did not commit: {problem}.");
            var uid = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_UID");
            var gid = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_GID");
            await SingBoxOperations.RunAsync("/usr/bin/chown", ["-R", uid + ":" + gid, "--", paths.GetProtectedConfigurationDirectory()]);
            Console.WriteLine("PASS REAL TUN ACTIVATION: durable marker, native device and changed policy rules");
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    internal async Task ApplyAsync(IPrivilegedOperationTransport transport, bool enabled)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.LinuxSystemProxyApply,
            LinuxSystemProxy: new(enabled, "127.0.0.1", proxyPort, true, "", true)));
        if (!result.Success) throw new InvalidOperationException($"System proxy apply failed: {result.ProblemCode}.");
    }

    internal void SetCompetingValue()
    {
        if (!File.Exists(JournalPath)) throw new InvalidOperationException("The Helper lost its original proxy journal.");
        File.WriteAllText(EnvironmentPath, "RELAXKONOS_PROXY_FIXTURE=isolated\nhttp_proxy=http://127.0.0.1:4321\n");
    }

    internal async Task VerifyPendingTunAsync()
    {
        if (!TunActive() || (await safety.CaptureRecoveryAsync(default)) is null)
            throw new InvalidOperationException("The Helper outage lost the TUN device or recovery marker.");
    }

    internal async Task VerifyRecoveryAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (ReadProxy() != ProxyUrl) await Task.Delay(100, budget.Token);
        var marker = "/var/lib/relaxkonos/proxy/state/proxy-tun-recovery.json";
        while (File.Exists(marker) || TunActive()) await Task.Delay(100, budget.Token);
        if (await ReadRulesAsync() != rules || !await new HostProxyNetworkSafetyPlatform().VerifyManagementRouteAsync(route, budget.Token))
            throw new InvalidOperationException("Core recovery did not restore the original management route and rules.");
        await VerifyTrafficAsync();
        Console.WriteLine("PASS FULL SERVER TUN: Core recovered original marker, device and policy rules without stopping Mihomo");
        Console.WriteLine("PASS FULL SERVER SYSTEM PROXY: Core guard corrected drift and real Mihomo forwarded HTTP");
    }

    internal async Task VerifyTrafficAsync()
    {
        if (mihomo.HasExited || ReadProxy() != ProxyUrl || !File.Exists(JournalPath))
            throw new InvalidOperationException("The system proxy endpoint or recovery journal was lost.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var origin = new TcpListener(IPAddress.Parse("93.184.216.34"), 0);
        origin.Start();
        using var http = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(ProxyUrl) });
        var response = http.GetStringAsync($"http://93.184.216.34:{((IPEndPoint)origin.LocalEndpoint).Port}/", budget.Token);
        using var connection = await origin.AcceptTcpClientAsync(budget.Token);
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(budget.Token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nproxy-ok"u8.ToArray(), budget.Token);
        if (await response != "proxy-ok") throw new InvalidOperationException("Isolated HTTP forwarding did not return the origin response.");
    }

    internal void VerifyOriginalRestored()
    {
        if (ReadProxy() != Original || File.Exists(JournalPath) || mihomo.HasExited)
            throw new InvalidOperationException("The original system proxy value was not restored cleanly.");
        Console.WriteLine("PASS SYSTEM PROXY RESTORE: original environment restored and Mihomo remained active");
    }

    private static string? ReadProxy() => File.ReadLines(EnvironmentPath)
        .FirstOrDefault(line => line.StartsWith("http_proxy=", StringComparison.Ordinal))?["http_proxy=".Length..].Trim('"');

    private static bool TunActive() => NetworkInterface.GetAllNetworkInterfaces().Any(network => network.Name == Device);

    private static async Task<string> ReadRulesAsync() =>
        await SingBoxOperations.RunAsync("/usr/sbin/ip", ["-4", "rule", "show"])
        + await SingBoxOperations.RunAsync("/usr/sbin/ip", ["-6", "rule", "show"]);

    private sealed class FixturePaths(string state) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => state;
        public string GetProtectedConfigurationDirectory() => "/etc/relaxkonos/proxy";
        public string GetEngineVersionsDirectory(string engineId) => state;
        public string GetEngineDataDirectory(string engineId) => state;
        public string GetSanitizedLogDirectory() => state;
    }

    public async ValueTask DisposeAsync()
    {
        using (mihomo)
        {
            if (!mihomo.HasExited) mihomo.Kill();
            await mihomo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
