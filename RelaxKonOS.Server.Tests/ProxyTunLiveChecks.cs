using System.Diagnostics;

internal static class ProxyTunLiveChecks
{
    private const string Device = "rk-mb04-tun";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(string originalNamespace)
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
            var directory = Directory.CreateTempSubdirectory("relaxkonos-tun-crash-");
            try
            {
                var paths = new TestProxyPaths(directory.FullName);
                var runtime = new NamespaceTunRuntime();
                var safety = new ProxyTunSafetyService(paths, new HostProxyNetworkSafetyPlatform(), runtime);
                using (var worker = DockerProcessRecoveryChecks.StartWorker("--proxy-tun-live-worker", originalNamespace, directory.FullName))
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
                Console.WriteLine($"PASS TUN LIVE RECOVERY: operation={original.OperationId}; real device removed once, route verified, stale recovery rejected");
            }
            finally
            {
                if (await DeviceExistsAsync()) await RunIpAsync("tuntap", "del", "dev", Device, "mode", "tun");
                directory.Delete(recursive: true);
            }
        }
        finally { await RunIpAsync("link", "del", "rk-egress"); }
    }

    internal static async Task RunWorkerAsync(string originalNamespace, string root)
    {
        await RequirePrivateNamespaceAsync(originalNamespace);
        var runtime = new NamespaceTunRuntime(pauseAfterCreate: true);
        var safety = new ProxyTunSafetyService(new TestProxyPaths(root), new HostProxyNetworkSafetyPlatform(), runtime);
        await safety.EnableAsync(Guid.NewGuid(), null, default);
        throw new InvalidOperationException("The TUN activation crash barrier was bypassed.");
    }

    private static async Task RequirePrivateNamespaceAsync(string originalNamespace)
    {
        if (!OperatingSystem.IsLinux() || !System.Text.RegularExpressions.Regex.IsMatch(originalNamespace, @"^net:\[\d+\]$"))
            throw new ArgumentException("Expected the host Linux network namespace identity.");
        var (code, current) = await RunAsync("/usr/bin/readlink", "/proc/self/ns/net");
        if (code != 0 || current.Trim() == originalNamespace || !System.Text.RegularExpressions.Regex.IsMatch(current.Trim(), @"^net:\[\d+\]$"))
            throw new InvalidOperationException("TUN live checks require a distinct private network namespace.");
    }

    private static Task AddDefaultRouteAsync() => RunIpAsync("route", "add", "default", "via", "192.0.2.1", "dev", "rk-egress");

    private static async Task<bool> DeviceExistsAsync() =>
        (await RunAsync("/usr/sbin/ip", "link", "show", "dev", Device)).Code == 0;

    private static async Task RunIpAsync(params string[] args)
    {
        var (code, output) = await RunAsync("/usr/sbin/ip", args);
        if (code != 0) throw new InvalidOperationException($"Isolated ip {string.Join(' ', args)} failed: {output}");
    }

    private static async Task<(int Code, string Output)> RunAsync(string executable, params string[] args)
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

    private sealed class NamespaceTunRuntime(bool pauseAfterCreate = false) : IProxyTunRuntimeController
    {
        internal int Removals;

        public async Task<string?> SetEnabledAsync(ProxyManagementRouteSnapshot snapshot, bool enabled, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled)
            {
                await RunIpAsync("tuntap", "add", "dev", Device, "mode", "tun");
                await RunIpAsync("link", "set", Device, "up");
                if (pauseAfterCreate)
                {
                    Console.WriteLine("TUN_ACTIVE");
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
            else if (await DeviceExistsAsync())
            {
                await RunIpAsync("tuntap", "del", "dev", Device, "mode", "tun");
                Removals++;
            }
            return null;
        }

        public async Task<ProxyTunRuntimeObservation> IsEnabledAsync(CancellationToken cancellationToken) =>
            new(true, await DeviceExistsAsync());
    }
}
