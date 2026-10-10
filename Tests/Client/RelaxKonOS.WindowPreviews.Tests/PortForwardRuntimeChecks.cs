using RelaxKonOS.Client.Apps.PortForwarding;
using System.Security.Cryptography;

internal static class PortForwardRuntimeChecks
{
    public static async Task RunAsync()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing runtime test environment: " + name);
        var host = Required("RK_FORWARD_TEST_HOST");
        var user = Required("RK_FORWARD_TEST_USER");
        var password = Required("RK_FORWARD_TEST_PASSWORD");
        var fingerprint = Required("RK_FORWARD_TEST_TLS_SHA256");
        var directory = Path.Combine(Path.GetTempPath(), "rk-forward-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var service = new PortForwardingService(null!, new PortForwardingSettingsStore(path));
        PortForwardInfo? forward = null;
        try
        {
            service.SaveSettings(new PortForwardingSettings(host, user, 22));
            forward = await service.StartAsync(new("localhost", 5000, "https", 17500), password);
            password = string.Empty;
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
                    && certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(fingerprint, StringComparison.OrdinalIgnoreCase) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            Exception? last = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, forward.LocalUri);
                    using var response = await http.SendAsync(request);
                    Console.WriteLine("PASS: Real SSH forward reached the fingerprint-verified server (HTTP " + (int)response.StatusCode + ").");
                    last = null; break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                { last = ex; await Task.Delay(250); }
            }
            if (last is not null) throw new InvalidOperationException("Real SSH forward did not become reachable.", last);
        }
        finally
        {
            password = string.Empty;
            if (forward is not null) await service.RemoveAsync(forward.Id);
            if (service.List().Count != 0) throw new Exception("Runtime test left a forward registered.");
            File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("PASS: Real SSH forward removed and isolated settings cleaned.");
    }
}
