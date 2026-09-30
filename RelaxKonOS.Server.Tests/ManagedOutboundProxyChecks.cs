internal static class ManagedOutboundProxyChecks
{
    internal static async Task RunAsync(string root)
    {
        await DockerChecks.VerifyDockerProxyAsync(root);
        await using var origin = new LocalHttpResponder("origin");
        await using var first = new LocalHttpResponder("proxy-one");
        await using var second = new LocalHttpResponder("proxy-two");
        var preferences = new InMemoryDockerProxySettingsRepository();
        var saved = new DockerProxySetting
        {
            Enabled = true, Source = DockerProxySource.ManagedProxy, ApplyToEngine = true,
            ApplyToBuild = true, ApplyToImageTags = true, ApplyToRuntimeDownloads = true,
        };
        await preferences.SaveAsync(saved);
        var runtimeSettings = new MutableProxySettings(first.Port);
        var resolver = new DockerProxyResolver(preferences, runtimeSettings);
        var factory = new OutboundProxyHttpClientFactory(resolver);
        var fixture = DockerChecks.CreateDockerProxyService(preferences, resolver, DispatchProxy.Create<IDockerEngineService, StubDockerEngine>());
        foreach (var target in new[] { RelaxKonOS.Server.Docker.OutboundProxyTarget.ImageTags, RelaxKonOS.Server.Docker.OutboundProxyTarget.RuntimeDownloads })
        {
            using var client = await factory.CreateAsync(target, TimeSpan.FromSeconds(5));
            TestAssert.Assert(await client.GetStringAsync(origin.Url + "/download") == "proxy-one",
                "A selected managed download scope did not actually use its proxy.");
        }
        TestAssert.Assert(first.Requests.Count == 2 && first.Requests.All(line => line.Contains(origin.Url, StringComparison.Ordinal)),
            "Managed proxy fixtures did not receive absolute HTTP request targets.");

        saved.NoProxy = "127.0.0.1";
        await preferences.SaveAsync(saved); resolver.Invalidate();
        using (var bypassed = await factory.CreateAsync(RelaxKonOS.Server.Docker.OutboundProxyTarget.RuntimeDownloads, TimeSpan.FromSeconds(5)))
            TestAssert.Assert(await bypassed.GetStringAsync(origin.Url + "/bypass") == "origin", "NO_PROXY did not bypass the managed listener.");
        saved.NoProxy = ""; saved.ApplyToRuntimeDownloads = false;
        await preferences.SaveAsync(saved); resolver.Invalidate();
        using (var direct = await factory.CreateAsync(RelaxKonOS.Server.Docker.OutboundProxyTarget.RuntimeDownloads, TimeSpan.FromSeconds(5)))
            TestAssert.Assert(await direct.GetStringAsync(origin.Url + "/unselected") == "origin", "An unselected download scope still used the host proxy.");

        saved.ApplyToRuntimeDownloads = true;
        await preferences.SaveAsync(saved);
        runtimeSettings.Current = runtimeSettings.Current with { MixedPort = second.Port };
        var changed = await fixture.Service.GetStatusAsync();
        TestAssert.Assert(changed.ManagedProxyEndpoint == second.Url && changed.ManagedProxyAvailable,
            "An explicit host status read retained the previously cached Mihomo address.");
        using (var updated = await factory.CreateAsync(RelaxKonOS.Server.Docker.OutboundProxyTarget.RuntimeDownloads, TimeSpan.FromSeconds(5)))
            TestAssert.Assert(await updated.GetStringAsync(origin.Url + "/changed") == "proxy-two", "Download resolution did not adopt the updated managed listener.");

        await second.DisposeAsync();
        var stopped = await fixture.Service.GetStatusAsync();
        TestAssert.Assert(!stopped.ManagedProxyAvailable
            && stopped.Layers.Any(layer => layer.ProblemCode == DockerProxyProblem.ManagedProxyUnavailable),
            "A stopped managed listener was hidden by the resolution cache.");
        var before = origin.Requests.Count;
        foreach (var target in new[] { RelaxKonOS.Server.Docker.OutboundProxyTarget.ImageTags, RelaxKonOS.Server.Docker.OutboundProxyTarget.RuntimeDownloads })
        {
            try { using var rejected = await factory.CreateAsync(target, TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Unavailable managed proxy silently became a direct client."); }
            catch (HttpRequestException error) { TestAssert.Assert(error.Message == DockerProxyProblem.ManagedProxyUnavailable, "Unavailable source returned an unstable or sensitive failure."); }
        }
        TestAssert.Assert(origin.Requests.Count == before, "Unavailable selected scopes leaked a direct request.");
        Console.WriteLine("Managed outbound proxy HTTP, bypass, scope, live address and stopped-listener checks passed.");
    }

    private sealed class MutableProxySettings(int port) : IProxySettingsService
    {
        internal ProxySettingsDto Current { get; set; } = new(false, false, true, true, false, "warning", port);
        public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class LocalHttpResponder : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _requests = [];
        internal int Port { get; }
        internal string Url => $"http://127.0.0.1:{Port}";
        internal IReadOnlyList<string> Requests { get { lock (_requests) return _requests.ToArray(); } }
        internal LocalHttpResponder(string response)
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeAsync(response);
        }
        private async Task ServeAsync(string response)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    lock (_requests) _requests.Add(line ?? "");
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}");
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested) { _stop.Cancel(); _listener.Stop(); }
            await _loop;
        }
    }
}
