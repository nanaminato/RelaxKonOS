using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Proxy;

internal static class ProxyConnectionBenchmark
{
    private const int ConnectionCount = 512;
    private const int RequestsPerRoute = 24;
    private const int TrafficPolls = 10;

    internal static async Task RunAsync(string root, string socket, string baseline, string streaming, LoginRequest login,
        ProxySystemdGuardProbe guard, Func<RuntimeArchiveState> nativeState)
    {
        var revision = nativeState().Installation.StateRevision;
        await guard.VerifyTrafficAsync();
        await RunAsync(baseline, streaming, "buffered", "streaming", login, guard.RuntimeProxy, PrepareAsync, VerifyAsync,
            requireManaged: false);

        async Task<ProcessStartInfo> PrepareAsync(string assembly)
        {
            var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, socket, root, reuse: true);
            start.Environment["Modules__Proxy__Enabled"] = "true";
            start.Environment["Identity__LinuxPamTransport"] = "helper";
            return start;
        }

        async Task VerifyAsync()
        {
            await guard.VerifyTrafficAsync();
            if (nativeState().Installation.StateRevision != revision)
                throw new InvalidOperationException("The paired read workload advanced the original publication revision.");
        }
    }

    internal static async Task RunAsync(string baseline, string candidate, string baselineMode, string candidateMode,
        LoginRequest login, Uri runtimeProxy, Func<string, Task<ProcessStartInfo>> prepare, Func<Task> verify,
        bool requireManaged, int? pollingRuntimePid = null)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(requireManaged ? 420 : 85));
        var ct = budget.Token;
        using var runtime = pollingRuntimePid is { } pid ? Process.GetProcessById(pid) : null;
        using var origin = new TcpListener(IPAddress.Parse("93.184.216.34"), 0);
        origin.Start(ConnectionCount);
        var destination = ((IPEndPoint)origin.LocalEndpoint).ToString();
        using var proxy = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(runtimeProxy), UseProxy = true })
            { Timeout = Timeout.InfiniteTimeSpan };
        var replies = Enumerable.Range(0, ConnectionCount).Select(_ => proxy.GetStringAsync($"http://{destination}/", ct)).ToArray();
        var connections = new List<TcpClient>(ConnectionCount);
        try
        {
            for (var i = 0; i < ConnectionCount; i++)
                connections.Add(await origin.AcceptTcpClientAsync(ct));
            await Task.WhenAll(connections.Select(connection => ReadHeadersAsync(connection, ct)));

            var inputs = new[] { (Mode: baselineMode, Assembly: baseline), (Mode: candidateMode, Assembly: candidate) };
            foreach (var input in inputs)
                Console.WriteLine("PROXY BENCHMARK INPUT " + JsonSerializer.Serialize(new
                {
                    input.Mode,
                    Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input.Assembly, ct))),
                    ProxyModuleSha256 = requireManaged ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                        Path.Combine(Path.GetDirectoryName(input.Assembly)!, "Modules", "Proxy", "RelaxKonOS.ServerModule.Proxy.dll"), ct))) : null
                }));
            string[]? original = null;
            for (var pair = 1; pair <= 3; pair++)
            {
                foreach (var input in pair == 2 ? inputs.Reverse() : inputs)
                {
                    var start = await prepare(input.Assembly);
                    using var process = ProxyAuthenticatedPublicationChecks.Start(start);
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(process, login, ct);
                        var ids = await ObserveAsync(client, destination, requireManaged, ct);
                        original ??= ids;
                        if (!ids.SequenceEqual(original) || replies.Any(reply => reply.IsCompleted))
                            throw new InvalidOperationException("The paired workload changed a connection or the original publication revision.");
                        await Task.Delay(500, ct);
                        var idle = ReadMemory(process.Id);
                        var samples = new List<MemoryPoint> { idle };
                        var timings = new List<double>();
                        var serverCpuStart = ReadCpu(process);
                        var runtimeCpuStart = runtime is null ? TimeSpan.Zero : ReadCpu(runtime);
                        var load = runtime is null ? ObserveRepeatedlyAsync() : MeasureTrafficPollingAsync();
                        while (!load.IsCompleted)
                        {
                            await Task.Delay(25, ct);
                            samples.Add(ReadMemory(process.Id));
                        }
                        await load;
                        var serverCpuMs = (ReadCpu(process) - serverCpuStart).TotalMilliseconds;
                        var runtimeCpuMs = runtime is null ? 0 : (ReadCpu(runtime) - runtimeCpuStart).TotalMilliseconds;
                        var cooldown = new List<MemoryPoint>();
                        if (requireManaged)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(35), ct);
                            for (var sample = 0; sample < 5; sample++)
                            {
                                cooldown.Add(ReadMemory(process.Id));
                                await Task.Delay(100, ct);
                            }
                            if (!(await ObserveAsync(client, destination, requireManaged, ct)).SequenceEqual(original))
                                throw new InvalidOperationException("The original connections did not survive the cooldown.");
                        }
                        Console.WriteLine("PROXY BENCHMARK RUN " + JsonSerializer.Serialize(new
                        {
                            Pair = pair, input.Mode, ConnectionCount, RequestsPerRoute = runtime is null ? RequestsPerRoute : 0,
                            Idle = idle, Samples = samples, Cooldown = cooldown,
                            PairRequestMs = runtime is null ? timings : null, TrafficPollMs = runtime is null ? null : timings,
                            ServerCpuMs = serverCpuMs, RuntimeCpuMs = runtimeCpuMs
                        }));

                        async Task MeasureTrafficPollingAsync()
                        {
                            using var cadence = new PeriodicTimer(TimeSpan.FromSeconds(2));
                            long uploaded = 0, downloaded = 0;
                            for (var i = 0; i < TrafficPolls; i++)
                            {
                                if (input.Mode == candidateMode)
                                {
                                    var watch = Stopwatch.StartNew();
                                    var traffic = await client.GetFromJsonAsync<ProxyTrafficDto>(ProxyApiRoutes.Traffic, RelaxKonOSJsonOptions.Default, ct);
                                    timings.Add(watch.Elapsed.TotalMilliseconds);
                                    if (traffic is not { ProblemCode: "", MemoryBytes: >= 0, UploadTotalBytes: > 0 }
                                        || traffic.UploadTotalBytes < uploaded || traffic.DownloadTotalBytes < downloaded)
                                        throw new InvalidOperationException("The polling run did not return monotonic native traffic totals.");
                                    uploaded = traffic.UploadTotalBytes;
                                    downloaded = traffic.DownloadTotalBytes;
                                }
                                await cadence.WaitForNextTickAsync(ct);
                            }
                        }

                        async Task ObserveRepeatedlyAsync()
                        {
                            for (var i = 0; i < RequestsPerRoute; i++)
                            {
                                var watch = Stopwatch.StartNew();
                                var current = await ObserveAsync(client, destination, requireManaged, ct);
                                timings.Add(watch.Elapsed.TotalMilliseconds);
                                if (!current.SequenceEqual(original))
                                    throw new InvalidOperationException("A measured request saw a different active connection set.");
                            }
                        }
                    }
                    finally { await ProxyAuthenticatedPublicationChecks.StopAsync(process); }
                    await verify();
                }
            }
            await Task.WhenAll(connections.Select(connection => connection.GetStream().WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nproxy-ok"u8.ToArray(), ct).AsTask()));
            if ((await Task.WhenAll(replies)).Any(reply => reply != "proxy-ok"))
                throw new InvalidOperationException("An original benchmark connection failed to forward its response.");
            await verify();
            Console.WriteLine("PASS PAIRED MIHOMO CONNECTION BENCHMARK: 512 original connections, six authenticated Server runs, all origin responses delivered");
        }
        finally
        {
            foreach (var connection in connections) connection.Dispose();
            await budget.CancelAsync();
            try { await Task.WhenAll(replies); }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
        }
    }

    private static async Task<string[]> ObserveAsync(HttpClient client, string destination, bool requireManaged, CancellationToken ct)
    {
        var overview = await client.GetFromJsonAsync<ProxyOverviewDto>(ProxyApiRoutes.Overview, RelaxKonOSJsonOptions.Default, ct);
        var connections = await client.GetFromJsonAsync<ProxyConnectionDto[]>(ProxyApiRoutes.Connections, RelaxKonOSJsonOptions.Default, ct);
        if (overview is not { ActiveConnections: ConnectionCount, Health.ControllerReachable: true } || connections?.Length != ConnectionCount
            || (requireManaged && overview.Runtime is not { Mode: ProxyRuntimeMode.Managed, State: ProxyRuntimeState.Running, IntegrityVerified: true })
            || connections.Any(connection => connection.Destination != destination))
            throw new InvalidOperationException($"Expected {ConnectionCount} held native connections; overview={overview?.ActiveConnections}, details={connections?.Length}.");
        return connections.Select(connection => connection.Id).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task ReadHeadersAsync(TcpClient connection, CancellationToken ct)
    {
        using var reader = new StreamReader(connection.GetStream(), leaveOpen: true);
        var length = 0;
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line)
            if ((length += line.Length) > 8192) throw new InvalidOperationException("Benchmark request headers were oversized.");
    }

    private static MemoryPoint ReadMemory(int pid)
    {
        var fields = File.ReadLines($"/proc/{pid}/smaps_rollup")
            .Where(line => line.StartsWith("Pss:", StringComparison.Ordinal) || line.StartsWith("Rss:", StringComparison.Ordinal)
                || line.StartsWith("Private_Clean:", StringComparison.Ordinal) || line.StartsWith("Private_Dirty:", StringComparison.Ordinal))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(fields => fields[0], fields => long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        return new(fields["Rss:"], fields["Pss:"], fields["Private_Clean:"] + fields["Private_Dirty:"]);
    }

    private static TimeSpan ReadCpu(Process process)
    {
        process.Refresh();
        return process.TotalProcessorTime;
    }

    private sealed record MemoryPoint(long RssKiB, long PssKiB, long PrivateKiB);
}
