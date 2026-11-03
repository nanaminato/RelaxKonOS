using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Startup;

internal static class ProxySettingsBoundaryChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        { if (!value) throw new Exception(label); Console.WriteLine($"PASS PROXY BOUNDARY {++count}: {label}"); }
        var root = Path.Combine(Path.GetTempPath(), "proxy-read-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.AddProxyRuntimeServices();
            var paths = new TestProxyPaths(root);
            builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
            var heavyLoads = 0;
            builder.Services.AddSingleton<MihomoSettingsService>(_ =>
            { heavyLoads++; throw new Exception("A read-only consumer constructed the mutation graph."); });
            var preferences = new InMemoryDockerProxySettingsRepository();
            builder.Services.AddSingleton<IDockerProxySettingsRepository>(preferences);
            builder.Services.AddSingleton<IDockerProxyResolver, DockerProxyResolver>();
            await using var services = builder.Services.BuildServiceProvider();
            var reader = services.GetRequiredService<IProxySettingsReader>();
            Check(reader is ProxySettingsReader && reader is not IProxySettingsWriter && reader is not IProxySystemProxyReconciler,
                "Core reader carries no settings mutation or host reconciliation contract");
            Check((await reader.GetAsync(default)).MixedPort == 7890 && !Directory.Exists(paths.GetStateDirectory()),
                "an absent settings file yields defaults without creating state or executing management code");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Directory.CreateDirectory(paths.GetStateDirectory());
            var file = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
            var value = new ProxySettingsDto(false, false, true, true, false, "warning", port);
            var wire = JsonSerializer.Serialize(value);
            await File.WriteAllTextAsync(file, wire);
            var current = await reader.GetAsync(default);
            Check(current.MixedPort == port && current.Tun is not null && current.SystemProxy is not null,
                "reader normalizes optional settings without constructing a controller or publication executor");
            await preferences.SaveAsync(new DockerProxySetting
            { Enabled = true, Source = DockerProxySource.ManagedProxy, ApplyToBuild = true, ApplyToRuntimeDownloads = true });
            var resolver = services.GetRequiredService<IDockerProxyResolver>();
            Check((await resolver.ResolveAsync()).RuntimeDownloadsActive,
                "Docker and runtime-download routing read the live managed listener while the Proxy module is not registered");
            Check(heavyLoads == 0, "outbound reads do not instantiate the settings writer, TUN controller or system reconciliation graph");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(value with { SystemProxyHost = "203.0.113.1" }));
            resolver.Invalidate();
            Check(!(await resolver.ResolveAsync()).IsUsable,
                "an unrelated local listener cannot establish availability for a remote managed-proxy address");
            await File.WriteAllTextAsync(file, wire);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var refused = false;
                try { await reader.GetAsync(cancelled.Token); }
                catch (OperationCanceledException) { refused = true; }
                Check(refused, "settings cancellation is propagated instead of converted into defaults");
            }
            foreach (var invalid in new[] { "", "null", "{", wire[..^1] + ",\"mixedPort\":1234}",
                wire[..^1] + ",\"unexpected\":true}", JsonSerializer.Serialize(value with { MixedPort = 0 }),
                JsonSerializer.Serialize(value with { SystemProxyHost = "example.invalid" }),
                JsonSerializer.Serialize(value with { SystemProxyHost = "https://example.invalid" }),
                JsonSerializer.Serialize(value with { LogLevel = "trace" }), JsonSerializer.Serialize(value with { SystemProxyHost = new string('a', 129) }),
                new string(' ', ProxySettingsReader.MaximumBytes + 1) })
            {
                await File.WriteAllTextAsync(file, invalid);
                var refused = false;
                try { await reader.GetAsync(default); }
                catch (InvalidDataException) { refused = true; }
                Check(refused, "invalid, duplicate, unknown or oversized settings fail closed");
                resolver.Invalidate();
                var route = await resolver.ResolveAsync();
                Check(!route.IsUsable && route.ProblemCode == DockerProxyProblem.ManagedProxyUnavailable,
                    "corrupt managed settings cannot become a default-port or direct route");
            }
            File.Delete(file);
            Directory.CreateDirectory(file);
            var directoryRefused = false;
            try { await reader.GetAsync(default); }
            catch (InvalidDataException) { directoryRefused = true; }
            Check(directoryRefused, "a directory at the settings path is not treated as missing state");
            Directory.Delete(file);
            if (OperatingSystem.IsLinux())
            {
                var target = Path.Combine(root, "other.json");
                await File.WriteAllTextAsync(target, wire);
                File.CreateSymbolicLink(file, target);
                var refused = false;
                try { await reader.GetAsync(default); }
                catch (InvalidDataException) { refused = true; }
                Check(refused, "the read-only port rejects symlinked settings");
                File.Delete(file);
            }
            await File.WriteAllTextAsync(file, wire);
            resolver.Invalidate();
            Check((await resolver.ResolveAsync()).IsUsable && heavyLoads == 0,
                "repairing metadata restores the selected route without waking Proxy management");
            Console.WriteLine($"Proxy settings boundary checks passed: {count}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
