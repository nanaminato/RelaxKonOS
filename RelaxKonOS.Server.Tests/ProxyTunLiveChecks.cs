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
            await RunIpAsync("address", "add", "192.0.2.3/24", "dev", "rk-peer");
            await RunIpAsync("link", "set", "rk-egress", "up");
            await RunIpAsync("link", "set", "rk-peer", "up");
            await AddDefaultRouteAsync();
            await RunIpAsync("link", "set", "lo", "up");
            var baselineRules = await ReadRulesAsync();
            var directory = Directory.CreateTempSubdirectory("relaxkonos-tun-crash-");
            try
            {
                var paths = new TestProxyPaths(directory.FullName);
                var port = mihomoExecutable is null ? 0 : ReservePort();
                var calls = new ControllerRequestCounter();
                using var http = new HttpClient(calls);
                using var mihomo = mihomoExecutable is null ? null : await StartMihomoAsync(mihomoExecutable, directory.FullName, paths, port);
                try
                {
                    var publication = new MihomoPublicationStore(paths, new EphemeralDataProtectionProvider());
                    var runtime = new NamespaceTunRuntime(inner: port == 0 ? null : CreateMihomoRuntime(paths, port, http, publication));
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
                            if (mihomo is not null)
                            {
                                var tunneled = await RunIpAsync("route", "get", "198.51.100.53");
                                var management = await RunIpAsync("route", "get", "192.0.2.1");
                                TestAssert.Assert(tunneled.Contains($" dev {Device} ", StringComparison.Ordinal)
                                    && management.Contains(" dev rk-egress ", StringComparison.Ordinal)
                                    && await ReadRulesAsync() != baselineRules,
                                    $"Automatic routing must select TUN while excluding the management gateway. Probe={tunneled}; gateway={management}");
                                await VerifyDnsHijackAsync();
                            }
                            worker.Kill();
                            await worker.WaitForExitAsync().WaitAsync(Budget);
                            TestAssert.Assert(worker.ExitCode != 0 && await DeviceExistsAsync(),
                                "The original worker must exit while leaving its persistent test TUN behind.");
                        }
                        finally { await DockerProcessRecoveryChecks.StopWorkerAsync(worker); }
                    }

                    var original = await safety.CaptureRecoveryAsync(default)
                        ?? throw new InvalidOperationException("The original TUN marker was lost.");
                    if (mihomo is not null)
                    {
                        var publicationId = Guid.NewGuid();
                        var activePath = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
                        var yaml = await File.ReadAllTextAsync(activePath);
                        var candidate = new RelaxKonOS.Protocol.ManagedRuntimes.MihomoPublishedRuntime(
                            yaml, [], port, "fixture-schedule-secret", [new("127.0.0.1", port)]);
                        await publication.Gate.WaitAsync();
                        try { await publication.BeginAsync(publicationId, 0, candidate, default); }
                        finally { publication.Gate.Release(); }
                        var schedulePath = Path.Combine(paths.GetStateDirectory(), "mihomo-publication.protected.schedule");
                        File.Delete(schedulePath);
                        TestAssert.Assert(await publication.ReadPendingIdentityAsync(default) == publicationId && File.Exists(schedulePath),
                            "The schedule must rebuild the original pending publication identity.");
                        TestAssert.Assert(await safety.RecoverAsync(original, default) == ProxyProblemCodes.RecoveryRequired
                            && await DeviceExistsAsync() && calls.Reloads == 0
                            && await File.ReadAllTextAsync(activePath) == yaml
                            && await safety.CaptureRecoveryAsync(default) == original,
                            "Pending publication must block a competing live TUN reload and preserve both authorities.");
                        await publication.Gate.WaitAsync();
                        try { await publication.AbortAsync(publicationId, null, notStarted: true, default); }
                        finally { publication.Gate.Release(); }
                        TestAssert.Assert(await publication.ReadPendingIdentityAsync(default) is null,
                            "The schedule must observe the exact not-started publication closure.");
                        Console.WriteLine($"PASS MIHOMO LIVE PUBLICATION GATE: publication={publicationId}; TUN recovery={original.OperationId}; competing reloads=0");
                    }
                    if (mihomo is not null)
                    {
                        calls.BlockNextReload = true;
                        TestAssert.Assert(await safety.RecoverAsync(original, default) == ProxyProblemCodes.RecoveryRequired
                            && await DeviceExistsAsync() && calls.Reloads == 0 && calls.BlockedReloads == 1
                            && await safety.CaptureRecoveryAsync(default) == original
                            && (await runtime.IsEnabledAsync(default)) is { Succeeded: true, Enabled: true },
                            "A controller that reconnects but still reports TUN enabled cannot close the original recovery.");
                        Console.WriteLine($"PASS MIHOMO LIVE UNAPPLIED RELOAD: operation={original.OperationId}; marker retained; native reloads=0");
                    }
                    calls.DropNextReloadResponse = mihomo is not null;
                    await RunIpAsync("route", "del", "default", "via", "192.0.2.1", "dev", "rk-egress");
                    TestAssert.Assert(await safety.RecoverAsync(original, default) == ProxyProblemCodes.RecoveryRequired
                        && !await DeviceExistsAsync() && await safety.CaptureRecoveryAsync(default) == original,
                        "Unknown management route must preserve the original recovery marker after real TUN removal.");
                    TestAssert.Assert(await safety.EnableAsync(Guid.NewGuid(), null, default) == ProxyProblemCodes.RecoveryRequired,
                        "An unresolved marker must block a new TUN activation.");

                    var disableRequests = calls.Reloads;
                    if (mihomo is not null) TestAssert.Assert(disableRequests == 1 && calls.DroppedResponses == 1, "Initial recovery must issue exactly one real disable reload.");
                    var recoveryLog = new RecoveryAttemptLog();
                    using (var recovery = new ProxyRecoveryHostedService(safety, recoveryLog))
                    {
                        await recovery.StartAsync(default);
                        try
                        {
                            await recoveryLog.Pending.Task.WaitAsync(Budget);
                            TestAssert.Assert(!recovery.ExecuteTask!.IsCompleted
                                && await safety.CaptureRecoveryAsync(default) == original
                                && calls.Reloads == disableRequests,
                                "The same Core worker must retain the original marker while the route is missing.");
                            await AddDefaultRouteAsync();
                            await recovery.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(45));
                        }
                        finally { await recovery.StopAsync(default).WaitAsync(Budget); }
                    }
                    TestAssert.Assert(await safety.CaptureRecoveryAsync(default) is null && runtime.Removals == 1
                        && calls.Reloads == disableRequests,
                        "Core startup recovery must clear the original marker without a second TUN removal.");
                    if (mihomo is not null)
                    {
                        var restoredRoute = await RunIpAsync("route", "get", "198.51.100.53");
                        TestAssert.Assert(await ReadRulesAsync() == baselineRules
                            && restoredRoute.Contains(" via 192.0.2.1 dev rk-egress ", StringComparison.Ordinal),
                            $"Recovery must remove TUN policy rules and restore the baseline route. Probe={restoredRoute}");
                    }

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
                    Console.WriteLine($"PASS TUN LIVE RECOVERY: operation={original.OperationId}; real device removed once, same Core worker retried and verified route, stale recovery rejected; initial recovery reloads={disableRequests}; auto-route={mihomo is not null}; dns={mihomo is not null}; mihomo={mihomo is not null}");
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

    private static async Task VerifyDnsHijackAsync()
    {
        using var budget = new CancellationTokenSource(Budget);
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Connect(IPAddress.Parse("198.51.100.53"), 53);
        // DNS query ID 0x4d04, one recursive question for mb04.test IN A.
        byte[] query = [0x4d, 4, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            4, (byte)'m', (byte)'b', (byte)'0', (byte)'4',
            4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 1, 0, 1];
        await socket.SendAsync(query.AsMemory(), budget.Token);
        var answer = (await socket.ReceiveAsync(budget.Token)).Buffer;
        TestAssert.Assert(answer.Length >= query.Length + 16 && answer[0] == query[0] && answer[1] == query[1]
            && (answer[2] & 0x80) != 0 && (answer[3] & 0x0f) == 0 && answer[6] == 0 && answer[7] == 1
            && answer.AsSpan(answer.Length - 4).SequenceEqual(new byte[] { 203, 0, 113, 42 }),
            "Mihomo TUN must return the configured A answer for the isolated DNS query.");
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static MihomoControllerClient CreateController(int port, HttpClient http) =>
        new(http, new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new($"http://127.0.0.1:{port}/") });

    private static MihomoTunRuntimeController CreateMihomoRuntime(TestProxyPaths paths, int port, HttpClient http, MihomoPublicationStore? publication = null) =>
        new(paths, new NamespaceSettings(), CreateController(port, http),
            publication ?? new MihomoPublicationStore(paths, new EphemeralDataProtectionProvider()), new HostProxyTunDeviceObserver());

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
            hosts:
              "mb04.test": 203.0.113.42
            dns:
              enable: true
              enhanced-mode: redir-host
              use-hosts: true
              nameserver:
                - udp://127.0.0.1:65534
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

    private sealed class RecoveryAttemptLog : ILogger<ProxyRecoveryHostedService>
    {
        internal readonly TaskCompletionSource Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning && formatter(state, exception).StartsWith(
                "Proxy TUN recovery evaluation requires operator attention:", StringComparison.Ordinal))
                Pending.TrySetResult();
        }
    }

    private sealed class ControllerRequestCounter() : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        internal int Reloads;
        internal int DroppedResponses;
        internal bool DropNextReloadResponse;
        internal bool BlockNextReload;
        internal int BlockedReloads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && BlockNextReload)
            {
                BlockNextReload = false;
                Interlocked.Increment(ref BlockedReloads);
                throw new HttpRequestException("Isolated fixture interrupted the connection before forwarding the reload.");
            }
            if (request.Method == HttpMethod.Put) Interlocked.Increment(ref Reloads);
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Put && DropNextReloadResponse && response.IsSuccessStatusCode)
            {
                DropNextReloadResponse = false;
                Interlocked.Increment(ref DroppedResponses);
                response.Dispose();
                throw new HttpRequestException("Isolated fixture dropped the real reload response.");
            }
            return response;
        }
    }

    private sealed class NamespaceSettings : IProxySettingsReader
    {
        public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "error", 0,
                Tun: ProxyTunSettingsDto.Default with { DeviceName = Device, AutoRoute = true, AutoDetectInterface = false }));
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

    private static async Task<string> ReadRulesAsync() =>
        await RunIpAsync("-4", "rule", "show") + await RunIpAsync("-6", "rule", "show");

    private static async Task<string> RunIpAsync(params string[] args)
    {
        var (code, output) = await RunToolAsync("/usr/sbin/ip", args);
        if (code != 0) throw new InvalidOperationException($"Isolated ip {string.Join(' ', args)} failed: {output}");
        return output;
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
