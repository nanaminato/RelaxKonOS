using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Privileged;

internal static class LinuxSystemdHostChecks
{
    internal static async Task RunFixtureAsync(string configuration)
    {
        var port = JsonDocument.Parse(await File.ReadAllTextAsync(configuration)).RootElement.GetProperty("port").GetInt32();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        await Task.Delay(Timeout.Infinite);
    }

    [SupportedOSPlatform("linux")]
    internal static async Task<int> RunAsync()
    {
        var host = new LinuxSingBoxArchiveHost();
        if ((await host.ObserveAsync(default)).Exists || Directory.Exists(LinuxSingBoxArchiveHost.Root))
            throw new Exception("Systemd fixture requires an absent service and absent runtime root");
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception($"systemd host evidence mismatch at check {count + 1}"); count++; }
        var root = LinuxSingBoxArchiveHost.Root;
        var directory = root + "/versions/" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        foreach (var path in new[] { root, root + "/versions", directory }) File.SetUnixFileMode(path, (UnixFileMode)493);
        // The renamed apphost is a controlled fixture, not an authorized sing-box release.
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var binary = directory + "/sing-box";
        File.Copy(Environment.ProcessPath!, binary);
        File.SetUnixFileMode(binary, (UnixFileMode)493);
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var configuration = root + "/config.json";
        await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new { port }));
        File.SetUnixFileMode(configuration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var identity = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(configuration)));
        try
        {
            await host.ConfigureAsync(binary, configuration, identity, false, default);
            var stopped = await host.ObserveAsync(default);
            Check(stopped is { Owned: true, Running: false, BootEnabled: false });
            await host.StartAsync(default);
            SingBoxServiceObservation? running = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { running = await host.ObserveAsync(default); if (running.Running) break; }
                catch (IOException) { }
                await Task.Delay(100);
            }
            Check(running is { Running: true, BootEnabled: false } && running.AppliedConfigurationIdentity == identity);
            var inbound = new ProxyServerInbound(Guid.NewGuid(), "Fixture", ProxyServerProtocol.VlessReality,
                "127.0.0.1", port, "example.com", port, "example.com");
            bool healthy = false;
            for (var attempt = 0; attempt < 20 && !healthy; attempt++)
            {
                healthy = await host.ListenersHealthyAsync(new(1, [inbound]), default);
                if (!healthy) await Task.Delay(100);
            }
            Check(healthy);
            Check(!await host.ListenersHealthyAsync(new(1, [inbound with { ListenPort = port == 65535 ? port - 1 : port + 1 }]), default));
            await File.WriteAllTextAsync(configuration, "{}");
            Check((await host.ObserveAsync(default)).AppliedConfigurationIdentity == identity);
            await host.StopAsync(default);
            Check(!(await host.ObserveAsync(default)).Running);
            try { await host.StartAsync(default); throw new Exception("Drift accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.configuration_drift"); }
            File.SetUnixFileMode(binary, (UnixFileMode)511);
            try { await host.StartAsync(default); throw new Exception("Writable program accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.service_scope_invalid"); }
            finally { File.SetUnixFileMode(binary, (UnixFileMode)493); }
            await host.RemoveOwnedRegistrationAsync(default);
            Check(!(await host.ObserveAsync(default)).Exists);
            var states = new RuntimeArchiveStateStore(root + "/state", path => Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            using (var held = states.Acquire())
            {
                var blocked = await SingBoxOperations.ExecuteAsync(new(PrivilegedOperationKind.ProxyServerStatus));
                Check(!blocked.Success && blocked.Error == "runtime.instance_busy");
            }
        }
        finally
        {
            await host.RemoveOwnedRegistrationAsync(default);
            Directory.Delete(root, true);
        }
        return count;
    }
}
