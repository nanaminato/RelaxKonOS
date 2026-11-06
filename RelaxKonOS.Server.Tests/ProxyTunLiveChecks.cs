using System.Diagnostics;

internal static class ProxyTunLiveChecks
{
    private const string Device = "rk-mb04-tun";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(string originalNamespace, string? mihomoExecutable = null)
    {
        await RequirePrivateNamespaceAsync(originalNamespace);
        if (System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(network => network.Name != "lo"))
            throw new InvalidOperationException("The private namespace already contains network devices.");
        await RunIpAsync("link", "add", "rk-egress", "type", "veth", "peer", "name", "rk-peer");
        try
        {
            await RunIpAsync("address", "add", "192.0.2.2/24", "dev", "rk-egress");
            await RunIpAsync("address", "add", "192.0.2.1/24", "dev", "rk-peer");
            await RunIpAsync("link", "set", "rk-egress", "up");
            await RunIpAsync("link", "set", "rk-peer", "up");
            await AddDefaultRouteAsync();
            await RunIpAsync("link", "set", "lo", "up");
            var directory = Directory.CreateTempSubdirectory("relaxkonos-tun-crash-");
            try
            {
                var paths = new TestProxyPaths(directory.FullName);
                var port = mihomoExecutable is null ? 0 : ReservePort();
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var mihomo = mihomoExecutable is null ? null : await StartMihomoAsync(mihomoExecutable, directory.FullName, paths, port);
                try
                {
                    var runtime = new NamespaceTunRuntime(inner: port == 0 ? null : CreateMihomoRuntime(paths, port, http));
                    var safety = new ProxyTunSafetyService(paths, new HostProxyNetworkSafetyPlatform(), runtime);
                    using (var worker = DockerProcessRecoveryChecks.StartWorker("--proxy-tun-live-worker", originalNamespace, directory.FullName, port.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    {
                        try
                        {
                            var response = await worker.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                            TestAssert.Assert(response == "TUN_ACTIVE" && !worker.HasExited && await DeviceExistsAsync(),
                                "The original worker must own a real TUN transition before termination.");
                            TestAssert.Assert((await safety.GetStatusAsync(default)) is { RecoveryRequired: true, HasRecoveryMarker: true },
                                "The original incomplete marker must be durable before the crash.");
                            worker.Kill();
                            await worker.WaitForExitAsync().WaitAsync(Budget);
                            TestAssert.Assert(worker.ExitCode != 0 && await DeviceExistsAsync(),
                                "The original worker must exit while leaving its persistent test TUN behind.");
                        }
                        finally { await DockerProcessRecoveryChecks.StopWorkerAsync(worker); }
                    }

                    var original = await safety.CaptureRecoveryAsync(default)
                        ?? throw new InvalidOperationException("The original TUN marker was lost.");
                    await RunIpAsync("route", "del", "default", "via", "192.0.2.1", "dev", "rk-egress");
                    TestAssert.Assert(await safety.RecoverAsync(original, default) == ProxyProblemCodes.RecoveryRequired
                        && !await DeviceExistsAsync() && await safety.CaptureRecoveryAsync(default) == original,
                        "Unknown management route must preserve the original recovery marker after real TUN removal.");
                    TestAssert.Assert(await safety.EnableAsync(Guid.NewGuid(), null, default) == ProxyProblemCodes.RecoveryRequired,
                        "An unresolved marker must block a new TUN activation.");

                    await AddDefaultRouteAsync();
                    TestAssert.Assert(await safety.RecoverAsync(original, default) is null
                        && await safety.CaptureRecoveryAsync(default) is null && runtime.Removals == 1,
                        "Verified route recovery must clear the original marker without a second TUN removal.");

                    TestAssert.Assert(await safety.EnableAsync(Guid.NewGuid(), null, default) is null,
                        "A fresh activation should succeed after the original route is verified.");
                    var replacement = await safety.CaptureRecoveryAsync(default);
                    TestAssert.Assert(replacement is not null && replacement != original
                        && await safety.RecoverAsync(original, default) is null
                        && await safety.CaptureRecoveryAsync(default) == replacement && await DeviceExistsAsync(),
                        "A stale recovery reference must leave a newer TUN session intact.");
                    TestAssert.Assert(await safety.DisableAsync(default) is null && !await DeviceExistsAsync(),
                        "The replacement TUN session must drain and remove its own device.");
                    if (mihomo is not null)
                        TestAssert.Assert(!mihomo.HasExited && (await runtime.IsEnabledAsync(default)) is { Succeeded: true, Enabled: false },
                            "The original Mihomo process must remain reachable with TUN disabled.");
                    Console.WriteLine($"PASS TUN LIVE RECOVERY: operation={original.OperationId}; real device removed once, route verified, stale recovery rejected; mihomo={mihomo is not null}");
                }
                finally { if (mihomo is not null) await DockerProcessRecoveryChecks.StopWorkerAsync(mihomo); }
            }
            finally
            {
                if (await DeviceExistsAsync()) await RunIpAsync("tuntap", "del", "dev", Device, "mode", "tun");
                directory.Delete(recursive: true);
            }
        }
        finally { await RunIpAsync("link", "del", "rk-egress"); }
    }

    internal static async Task RunWorkerAsync(string originalNamespace, string root, int port)
    {
        await RequirePrivateNamespaceAsync(originalNamespace);
        var paths = new TestProxyPaths(root);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var runtime = new NamespaceTunRuntime(pauseAfterCreate: true, inner: port == 0 ? null : CreateMihomoRuntime(paths, port, http));
        var safety = new ProxyTunSafetyService(paths, new HostProxyNetworkSafetyPlatform(), runtime);
        await safety.EnableAsync(Guid.NewGuid(), null, default);
        throw new InvalidOperationException("The TUN activation crash barrier was bypassed.");
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static MihomoControllerClient CreateController(int port, HttpClient http) =>
        new(http, new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new($"http://127.0.0.1:{port}/") });

    private static MihomoTunRuntimeController CreateMihomoRuntime(TestProxyPaths paths, int port, HttpClient http) =>
        new(paths, new NamespaceSettings(), CreateController(port, http),
            new MihomoPublicationStore(paths, new EphemeralDataProtectionProvider()), new HostProxyTunDeviceObserver());

    private static async Task<Process> StartMihomoAsync(string executable, string root, TestProxyPaths paths, int port)
    {
        const string expected = "/var/lib/relaxkonos/proxy/engines/mihomo/versions/v1.19.33-linux-x64/mihomo";
        if (executable != expected || !File.Exists(executable))
            throw new ArgumentException("Expected the installed Mihomo acceptance binary.");
        Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
        var config = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        await File.WriteAllTextAsync(config, $"""
            external-controller: 127.0.0.1:{port}
            secret: controller-secret
            mode: direct
            log-level: error
            tun:
              enable: false
            """);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "-d", root, "-f", config }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch isolated Mihomo.");
        try
        {
            // Use a separate client because the production controller configures its base address.
            using var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var controller = CreateController(port, probe);
            using var budget = new CancellationTokenSource(Budget);
            while (!process.HasExited)
            {
                if ((await controller.IsReachableAsync(budget.Token)).Succeeded) return process;
                await Task.Delay(50, budget.Token);
            }
            throw new InvalidOperationException($"Isolated Mihomo exited with {process.ExitCode}.");
        }
        catch
        {
            await DockerProcessRecoveryChecks.StopWorkerAsync(process);
            process.Dispose();
            throw;
        }
    }

    private sealed class NamespaceSettings : IProxySettingsReader
    {
        public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "error", 0,
                Tun: ProxyTunSettingsDto.Default with { DeviceName = Device, AutoRoute = false, AutoDetectInterface = false }));
    }

    private static async Task RequirePrivateNamespaceAsync(string originalNamespace)
    {
        if (!OperatingSystem.IsLinux() || !System.Text.RegularExpressions.Regex.IsMatch(originalNamespace, @"^net:\[\d+\]$"))
            throw new ArgumentException("Expected the host Linux network namespace identity.");
        var (code, current) = await RunToolAsync("/usr/bin/readlink", "/proc/self/ns/net");
        if (code != 0 || current.Trim() == originalNamespace || !System.Text.RegularExpressions.Regex.IsMatch(current.Trim(), @"^net:\[\d+\]$"))
            throw new InvalidOperationException("TUN live checks require a distinct private network namespace.");
    }

    private static Task AddDefaultRouteAsync() => RunIpAsync("route", "add", "default", "via", "192.0.2.1", "dev", "rk-egress");

    private static async Task<bool> DeviceExistsAsync() =>
        (await RunToolAsync("/usr/sbin/ip", "link", "show", "dev", Device)).Code == 0;

    private static async Task RunIpAsync(params string[] args)
    {
        var (code, output) = await RunToolAsync("/usr/sbin/ip", args);
        if (code != 0) throw new InvalidOperationException($"Isolated ip {string.Join(' ', args)} failed: {output}");
    }

    private static async Task<(int Code, string Output)> RunToolAsync(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch namespace tool.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Budget);
            return (process.ExitCode, await output + await errors);
        }
        finally { await DockerProcessRecoveryChecks.StopWorkerAsync(process); }
    }

    private sealed class NamespaceTunRuntime(bool pauseAfterCreate = false, IProxyTunRuntimeController? inner = null) : IProxyTunRuntimeController
    {
        internal int Removals;

        public async Task<string?> SetEnabledAsync(ProxyManagementRouteSnapshot snapshot, bool enabled, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inner is not null)
            {
                var existed = await DeviceExistsAsync();
                var problem = await inner.SetEnabledAsync(snapshot, enabled, cancellationToken);
                if (problem is not null) return problem;
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(Budget);
                while (await DeviceExistsAsync() != enabled) await Task.Delay(50, budget.Token);
                if (!enabled && existed) Removals++;
            }
            else if (enabled)
            {
                await RunIpAsync("tuntap", "add", "dev", Device, "mode", "tun");
                await RunIpAsync("link", "set", Device, "up");
            }
            else if (await DeviceExistsAsync())
            {
                await RunIpAsync("tuntap", "del", "dev", Device, "mode", "tun");
                Removals++;
            }
            if (enabled && pauseAfterCreate)
            {
                Console.WriteLine("TUN_ACTIVE");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return null;
        }

        public async Task<ProxyTunRuntimeObservation> IsEnabledAsync(CancellationToken cancellationToken) =>
            inner is null ? new(true, await DeviceExistsAsync()) : await inner.IsEnabledAsync(cancellationToken);
    }
}
