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
        bool requireManaged, int? pollingRuntimePid = null, bool controlReads = false, bool compareSplitConfiguration = false,
        MihomoObservationCancellationRelay? sharedTrafficRelay = null, MihomoObservationCancellationRelay? overviewBurstRelay = null,
        bool mixedRuntimeReads = false, MihomoObservationCancellationRelay? connectionBurstRelay = null)
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
            JsonElement[]? originalControl = null;
            JsonElement? originalOverview = null;
            ProxyConnectionDto[]? originalDetails = null;
            for (var pair = 1; pair <= 3; pair++)
            {
                foreach (var input in pair == 2 ? inputs.Reverse() : inputs)
                {
                    var splitConfiguration = compareSplitConfiguration && input.Mode == baselineMode;
                    var start = await prepare(input.Assembly);
                    using var process = ProxyAuthenticatedPublicationChecks.Start(start);
                    try
                    {
                        using var client = await ProxyAuthenticatedPublicationChecks.ConnectAsync(process, login, ct);
                        var ids = await ObserveAsync(client, destination, requireManaged, ct);
                        original ??= ids;
                        if (!ids.SequenceEqual(original) || replies.Any(reply => reply.IsCompleted))
                            throw new InvalidOperationException("The paired workload changed a connection or the original publication revision.");
                        if (controlReads) await ObserveControlAsync();
                        await Task.Delay(500, ct);
                        var idle = ReadMemory(process.Id);
                        var samples = new List<MemoryPoint> { idle };
                        var timings = new List<double>();
                        var readers = sharedTrafficRelay is null ? 1 : 4;
                        var trafficBefore = sharedTrafficRelay?.TrafficRequests;
                        var connectionRelay = sharedTrafficRelay ?? overviewBurstRelay ?? connectionBurstRelay;
                        var connectionsBefore = connectionRelay?.ConnectionRequests;
                        var versionsBefore = overviewBurstRelay?.VersionRequests;
                        var serverCpuStart = ReadCpu(process);
                        var runtimeCpuStart = runtime is null ? TimeSpan.Zero : ReadCpu(runtime);
                        var load = runtime is null ? ObserveRepeatedlyAsync() : MeasureTrafficPollingAsync();
                        while (!load.IsCompleted)
                        {
                            await Task.Delay(25, ct);
                            samples.Add(ReadMemory(process.Id));
                        }
                        await load;
                        var trafficRequests = sharedTrafficRelay?.TrafficRequests - trafficBefore;
                        var connectionRequests = connectionRelay?.ConnectionRequests - connectionsBefore;
                        var versionRequests = overviewBurstRelay?.VersionRequests - versionsBefore;
                        var expectedSamples = input.Mode == baselineMode ? readers * TrafficPolls : TrafficPolls;
                        if (sharedTrafficRelay is not null && (trafficRequests != expectedSamples || connectionRequests != expectedSamples))
                            throw new InvalidOperationException($"Expected {expectedSamples} native traffic and connection samples, got {trafficRequests} and {connectionRequests}.");
                        if (overviewBurstRelay is not null && ((mixedRuntimeReads ? connectionRequests != RequestsPerRoute : versionRequests != connectionRequests)
                            || (input.Mode == baselineMode ? versionRequests != 4 * RequestsPerRoute
                                : versionRequests is null || versionRequests < RequestsPerRoute || versionRequests >= 4 * RequestsPerRoute)))
                            throw new InvalidOperationException($"The overview burst counted {versionRequests} version and {connectionRequests} connection reads.");
                        if (connectionBurstRelay is not null && (input.Mode == baselineMode ? connectionRequests != 4 * RequestsPerRoute
                            : connectionRequests is null || connectionRequests < RequestsPerRoute || connectionRequests >= 4 * RequestsPerRoute))
                            throw new InvalidOperationException($"The detail burst counted {connectionRequests} native connection reads.");
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
                            Pair = pair, input.Mode, ConnectionCount, ControlReads = controlReads, RequestsPerRoute = runtime is null ? RequestsPerRoute : 0,
                            ConfigurationRequestsPerBatch = controlReads ? (splitConfiguration ? 2 : 1) : 0,
                            Idle = idle, Samples = samples, Cooldown = cooldown,
                            PairRequestMs = runtime is null ? timings : null, TrafficPollMs = runtime is null ? null : timings,
                            TrafficReaders = runtime is null ? 0 : readers, TrafficRequests = trafficRequests, ConnectionRequests = connectionRequests,
                            OverviewReaders = overviewBurstRelay is null ? 0 : mixedRuntimeReads ? 1 : 4,
                            RuntimeReaders = mixedRuntimeReads ? 3 : 0, VersionRequests = versionRequests,
                            ConnectionReaders = connectionBurstRelay is null ? 0 : 4,
                            ServerCpuMs = serverCpuMs, RuntimeCpuMs = runtimeCpuMs
                        }));

                        async Task MeasureTrafficPollingAsync()
                        {
                            using var cadence = new PeriodicTimer(TimeSpan.FromSeconds(2));
                            long uploaded = 0, downloaded = 0;
                            for (var i = 0; i < TrafficPolls; i++)
                            {
                                if (input.Mode == candidateMode || sharedTrafficRelay is not null)
                                {
                                    var watch = Stopwatch.StartNew();
                                    var responses = await Task.WhenAll(Enumerable.Range(0, readers).Select(_ =>
                                        client.GetFromJsonAsync<ProxyTrafficDto>(ProxyApiRoutes.Traffic, RelaxKonOSJsonOptions.Default, ct)));
                                    timings.Add(watch.Elapsed.TotalMilliseconds);
                                    if (responses.Any(traffic => traffic is not { ProblemCode: "", MemoryBytes: >= 0, UploadTotalBytes: > 0 }
                                            || traffic.UploadTotalBytes < uploaded || traffic.DownloadTotalBytes < downloaded)
                                        || (sharedTrafficRelay is not null && input.Mode == candidateMode && responses.Any(traffic => traffic != responses[0])))
                                        throw new InvalidOperationException("The polling run did not return monotonic native traffic totals.");
                                    uploaded = responses.Max(traffic => traffic!.UploadTotalBytes);
                                    downloaded = responses.Max(traffic => traffic!.DownloadTotalBytes);
                                }
                                await cadence.WaitForNextTickAsync(ct);
                            }
                        }

                        async Task ObserveRepeatedlyAsync()
                        {
                            for (var i = 0; i < RequestsPerRoute; i++)
                            {
                                var watch = Stopwatch.StartNew();
                                if (connectionBurstRelay is not null) await ObserveConnectionBurstAsync();
                                else if (mixedRuntimeReads) await ObserveMixedRuntimeAsync();
                                else if (overviewBurstRelay is not null) await ObserveOverviewBurstAsync();
                                else if (controlReads) await ObserveControlAsync();
                                else if (!(await ObserveAsync(client, destination, requireManaged, ct)).SequenceEqual(original))
                                    throw new InvalidOperationException("A measured request saw a different active connection set.");
                                timings.Add(watch.Elapsed.TotalMilliseconds);
                            }
                        }

                        async Task ObserveConnectionBurstAsync()
                        {
                            var values = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                                client.GetFromJsonAsync<ProxyConnectionDto[]>(ProxyApiRoutes.Connections, RelaxKonOSJsonOptions.Default, ct)));
                            foreach (var details in values)
                            {
                                if (details?.Length != ConnectionCount || details.Any(item => item.Destination != destination))
                                    throw new InvalidOperationException("The detail burst did not return the original native connection set.");
                                var ordered = details.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
                                originalDetails ??= ordered;
                                if (!ordered.Select(item => item.Id).SequenceEqual(original) || !ordered.SequenceEqual(originalDetails))
                                    throw new InvalidOperationException("The detail burst changed a connection identifier or its public projection.");
                            }
                        }

                        async Task ObserveOverviewBurstAsync()
                        {
                            var values = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                                client.GetFromJsonAsync<JsonElement>(ProxyApiRoutes.Overview, RelaxKonOSJsonOptions.Default, ct)));
                            originalOverview ??= values[0];
                            if (values[0].Deserialize<ProxyOverviewDto>(RelaxKonOSJsonOptions.Default)
                                    is not { ActiveConnections: ConnectionCount, Health.ControllerReachable: true,
                                        Runtime: { Mode: ProxyRuntimeMode.Managed, State: ProxyRuntimeState.Running, IntegrityVerified: true } }
                                || values.Any(value => !JsonElement.DeepEquals(value, originalOverview.Value)))
                                throw new InvalidOperationException("The overview burst did not preserve the full native observation.");
                        }

                        async Task ObserveMixedRuntimeAsync()
                        {
                            var overview = client.GetFromJsonAsync<JsonElement>(ProxyApiRoutes.Overview, RelaxKonOSJsonOptions.Default, ct);
                            var runtime = Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                                client.GetFromJsonAsync<ProxyRuntimeDto>(ProxyApiRoutes.Runtime, RelaxKonOSJsonOptions.Default, ct)));
                            await Task.WhenAll(overview, runtime);
                            var value = await overview;
                            originalOverview ??= value;
                            if (value.Deserialize<ProxyOverviewDto>(RelaxKonOSJsonOptions.Default)
                                    is not { ActiveConnections: ConnectionCount, Health.ControllerReachable: true,
                                        Runtime: { Mode: ProxyRuntimeMode.Managed, State: ProxyRuntimeState.Running, IntegrityVerified: true } measured }
                                || (await runtime).Any(item => item != measured)
                                || !JsonElement.DeepEquals(value, originalOverview.Value))
                                throw new InvalidOperationException("The mixed workload changed the overview or standalone runtime response.");
                        }

                        async Task ObserveControlAsync()
                        {
                            JsonElement[] values =
                            [
                                await ReadAsync(ProxyApiRoutes.Runtime), await ReadAsync(ProxyApiRoutes.Groups),
                                await ReadConfigurationAsync(),
                            ];
                            if (values[0].Deserialize<ProxyRuntimeDto>(RelaxKonOSJsonOptions.Default)
                                    is not { Mode: ProxyRuntimeMode.Managed, State: ProxyRuntimeState.Running, IntegrityVerified: true, ProblemCode: "" }
                                || values[1].ValueKind != JsonValueKind.Array
                                || values[2].Deserialize<ProxyControllerConfigurationDto>(RelaxKonOSJsonOptions.Default)
                                    is not { Routing.ProblemCode: "", Dns.ProblemCode: "" })
                                throw new InvalidOperationException("The control workload did not observe the real running Mihomo and its configuration.");
                            originalControl ??= values;
                            if (!values.Zip(originalControl, JsonElement.DeepEquals).All(equal => equal))
                                throw new InvalidOperationException("The paired read workload returned different control observations.");

                            Task<JsonElement> ReadAsync(string route) => client.GetFromJsonAsync<JsonElement>(route, RelaxKonOSJsonOptions.Default, ct);

                            async Task<JsonElement> ReadConfigurationAsync()
                            {
                                if (!splitConfiguration) return await ReadAsync(ProxyApiRoutes.ControllerConfiguration);
                                // This workload intentionally drives the previous Release's two reads in parallel.
                                var routing = client.GetFromJsonAsync<ProxyRoutingModeDto>(ProxyApiRoutes.Routing, RelaxKonOSJsonOptions.Default, ct);
                                var dns = client.GetFromJsonAsync<ProxyDnsStatusDto>(ProxyApiRoutes.Proxy + "/dns", RelaxKonOSJsonOptions.Default, ct);
                                await Task.WhenAll(routing, dns);
                                return JsonSerializer.SerializeToElement(new ProxyControllerConfigurationDto(
                                    await routing ?? throw new InvalidOperationException("The baseline returned no routing result."),
                                    await dns ?? throw new InvalidOperationException("The baseline returned no DNS result.")), RelaxKonOSJsonOptions.Default);
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
