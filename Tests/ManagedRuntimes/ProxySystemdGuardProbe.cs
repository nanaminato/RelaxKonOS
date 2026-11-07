using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Privileged;

internal sealed class ProxySystemdGuardProbe(Process mihomo, int proxyPort) : IAsyncDisposable
{
    private const string EnvironmentPath = "/etc/environment";
    private const string JournalPath = "/var/lib/relaxkonos-system-proxy/recovery.json";
    private const string Original = "http://127.0.0.1:1234";
    private string ProxyUrl => $"http://127.0.0.1:{proxyPort}";

    internal static async Task<ProxySystemdGuardProbe> StartAsync(string work, string root)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var config = Path.Combine(work, "mihomo.yaml");
        await File.WriteAllTextAsync(config, $"mixed-port: {number}\nallow-lan: false\nbind-address: 127.0.0.1\nmode: direct\nlog-level: error\n");
        var start = new ProcessStartInfo(Path.Combine(work, "mihomo")) { UseShellExecute = false };
        foreach (var arg in new[] { "-d", work, "-f", config }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated Mihomo.");
        var probe = new ProxySystemdGuardProbe(process, number);
        try
        {
            var settingsPath = Directory.CreateDirectory(Path.Combine(root, "server")).FullName;
            var settings = new ProxySettingsDto(true, false, false, true, false, "error", number,
                SystemProxy: new(GuardEnabled: true, GuardIntervalSeconds: 5));
            await File.WriteAllTextAsync(Path.Combine(settingsPath, "mihomo-settings.json"), JsonSerializer.Serialize(settings));
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!process.HasExited)
            {
                using var listener = new TcpClient();
                try { await listener.ConnectAsync(IPAddress.Loopback, number, budget.Token); return probe; }
                catch (SocketException) { await Task.Delay(50, budget.Token); }
            }
            throw new InvalidOperationException("Isolated Mihomo exited before opening its proxy listener.");
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

    internal async Task VerifyRecoveryAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (ReadProxy() != ProxyUrl) await Task.Delay(100, budget.Token);
        await VerifyTrafficAsync();
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

    public async ValueTask DisposeAsync()
    {
        using (mihomo)
        {
            if (!mihomo.HasExited) mihomo.Kill();
            await mihomo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
