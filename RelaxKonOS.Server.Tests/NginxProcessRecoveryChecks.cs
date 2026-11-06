using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

internal static class NginxProcessRecoveryChecks
{
    private const string PackageExecutable = "/usr/sbin/nginx";
    private const string Key = "reload-crash";
    private const string Actor = "nginx-live-fixture";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsLinux() || executable != PackageExecutable || !File.Exists(executable))
            throw new ArgumentException("Expected an installed Linux Nginx executable.");

        var directory = Directory.CreateTempSubdirectory("relaxkonos-nginx-crash-");
        try
        {
            var root = directory.FullName;
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            await File.WriteAllTextAsync(NginxManagedPaths.MarkerPath(new() { InstallationRoot = root }), NginxManagedPaths.MarkerContent);
            using var certificate = CreateCertificate(root);
            WriteConfig(root, port, "before");
            using var nginx = StartNginx(executable, root);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = false,
                    SslOptions = new()
                    {
                        RemoteCertificateValidationCallback = (_, peer, _, errors) =>
                            peer is not null && peer.GetCertHashString() == certificate.Thumbprint
                            && errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                    }
                })
                    { Timeout = TimeSpan.FromSeconds(2) };
                var address = new Uri($"https://127.0.0.1:{port}/");
                await ObserveAsync(client, address, "before", nginx);
                WriteConfig(root, port, "after");

                Guid operationId;
                using (var worker = DockerProcessRecoveryChecks.StartWorker("--nginx-recovery-worker", executable, root))
                {
                    try
                    {
                        var response = await worker.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                        TestAssert.Assert(Guid.TryParse(response, out operationId), $"Missing Nginx reload receipt: {response}");
                        await ObserveAsync(client, address, "after", nginx);
                        using var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "webserver-operations.json")));
                        var running = ledger.RootElement.EnumerateArray().Single();
                        var reloads = CountReloads(root);
                        TestAssert.Assert(!worker.HasExited && reloads == 1
                            && running.GetProperty("operationId").GetGuid() == operationId
                            && running.GetProperty("state").GetString() == "running",
                            $"The real reload and original Running receipt must precede the crash. Reloads={reloads}; receipt={running}.");
                        worker.Kill();
                        await worker.WaitForExitAsync().WaitAsync(Budget);
                        TestAssert.Assert(worker.ExitCode != 0, "The original Nginx operation process must exit abnormally.");
                    }
                    finally
                    {
                        await DockerProcessRecoveryChecks.StopWorkerAsync(worker);
                    }
                }

                using var store = await OpenStoreAsync(root);
                var restored = await store.GetAsync(operationId, default);
                var transport = new IsolatedReloadTransport(executable, root, allowReload: false);
                var provider = CreateProvider(root, store, transport);
                File.Delete(NginxManagedPaths.MarkerPath(new() { InstallationRoot = root }));
                TestAssert.Assert(restored is not null, "Original operation was absent after startup recovery.");
                TestAssert.Assert(!(await provider.DiscoverAsync(default)).Any(instance => instance.Id == restored!.InstanceId),
                    "The original instance must be absent from discovery when retrying.");
                var manager = new WebServerManager([provider]);
                var replay = await manager.ApplyLifecycleAsync(restored!.InstanceId, WebServerLifecycleAction.Reload, Key, Actor, default);
                var reloadReplay = await manager.ReloadAsync(restored.InstanceId, Key, Actor, default);
                using var reopenedStore = await OpenStoreAsync(root);
                var reopened = await reopenedStore.GetAsync(operationId, default);
                TestAssert.Assert(restored is { State: WebServerOperationState.Failed, Stage: "interrupted",
                        ProblemCode: "webserver.operation_interrupted" }
                    && replay == restored && reloadReplay == restored && reopened == restored && CountReloads(root) == 1 && transport.Calls == 0,
                    "The original interrupted receipt must persist and the retry must preserve the single reload.");
                await ObserveAsync(client, address, "after", nginx);
                Console.WriteLine($"PASS NGINX LIVE CRASH RECOVERY: operation={operationId}; interrupted receipt persisted, reloads=1, original Nginx still serving");
            }
            finally
            {
                if (!nginx.HasExited)
                {
                    try
                    {
                        using var quit = StartNginx(executable, root, "-s", "quit");
                        await quit.WaitForExitAsync().WaitAsync(Budget);
                        await nginx.WaitForExitAsync().WaitAsync(Budget);
                    }
                    finally
                    {
                        if (!nginx.HasExited) nginx.Kill(entireProcessTree: true);
                        await nginx.WaitForExitAsync().WaitAsync(Budget);
                    }
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static async Task RunWorkerAsync(string executable, string root)
    {
        using var store = await OpenStoreAsync(root);
        var transport = new IsolatedReloadTransport(executable, root, allowReload: true);
        var provider = CreateProvider(root, store, transport);
        var instance = await FindManagedInstanceAsync(provider);
        var receipt = await new WebServerManager([provider]).ApplyLifecycleAsync(instance.Id, WebServerLifecycleAction.Reload, Key, Actor, default)
            ?? throw new InvalidOperationException("The production provider refused the isolated reload.");
        await transport.Accepted.Task.WaitAsync(Budget);
        Console.WriteLine(receipt.OperationId);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task<WebServerDto> FindManagedInstanceAsync(NginxWebServerManager provider) =>
        (await provider.DiscoverAsync(default)).Single(instance => instance.ManagementMode == WebServerManagementMode.Managed
            && instance.ExecutablePath == PackageExecutable);

    private static NginxWebServerManager CreateProvider(string root, WebServerOperationStore store, IsolatedReloadTransport transport)
    {
        var environment = new TestHostEnvironment(root);
        var configuration = TestConfiguration();
        return new(new PrivilegedNginxOperations(transport), store, new WebServerMetadataRepository(environment, configuration),
            new TestApplicationLifetime(), new NginxManagedOptions { InstallationRoot = root },
            null!, null!, null!, null!, NullLogger<NginxWebServerManager>.Instance);
    }

    // Intercept only the closed reload request. No Helper connection or systemd call is available to this fixture.
    private sealed class IsolatedReloadTransport(string executable, string root, bool allowReload) : IPrivilegedOperationTransport
    {
        internal int Calls;
        internal readonly TaskCompletionSource Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (!allowReload || request.Operation != PrivilegedOperationKind.NginxSystemServiceAction
                || request.NginxServiceAction != NginxSystemServiceAction.Reload)
                throw new InvalidOperationException("Unexpected privileged request in isolated Nginx fixture.");
            using var command = StartNginx(executable, root, "-s", "reload");
            await command.WaitForExitAsync(cancellationToken);
            if (command.ExitCode != 0) throw new InvalidOperationException("Isolated Nginx reload failed.");
            Accepted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Crash barrier exited without cancellation.");
        }
    }

    private static IConfiguration TestConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();

    private static async Task<WebServerOperationStore> OpenStoreAsync(string root)
    {
        var environment = new TestHostEnvironment(root);
        var configuration = TestConfiguration();
        var store = new WebServerOperationStore(environment, new HostOperationJournal(environment, configuration),
            NullLogger<WebServerOperationStore>.Instance, new TestResidentBackgroundLeases());
        await store.StartAsync(default);
        return store;
    }

    private static Process StartNginx(string executable, string root, params string[] extra)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "-p", root + "/", "-c", Path.Combine(root, "nginx.conf") }.Concat(extra))
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start isolated Nginx.");
    }

    private static X509Certificate2 CreateCertificate(string root)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(Path.Combine(root, "fixture.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(root, "fixture.key"), key.ExportPkcs8PrivateKeyPem());
        return certificate;
    }

    private static void WriteConfig(string root, int port, string body) =>
        File.WriteAllText(Path.Combine(root, "nginx.conf"), $$"""
            daemon off;
            worker_processes 1;
            pid nginx.pid;
            error_log nginx.log notice;
            events { worker_connections 16; }
            http {
                client_body_temp_path client-body;
                proxy_temp_path proxy;
                fastcgi_temp_path fastcgi;
                uwsgi_temp_path uwsgi;
                scgi_temp_path scgi;
                access_log off;
                server {
                    listen 127.0.0.1:{{port}} ssl;
                    ssl_certificate fixture.pem;
                    ssl_certificate_key fixture.key;
                    location / { return 200 "{{body}}"; }
                }
            }
            """);

    private static int CountReloads(string root) =>
        File.ReadLines(Path.Combine(root, "nginx.log")).Count(line => line.EndsWith(": reconfiguring", StringComparison.Ordinal));

    private static async Task ObserveAsync(HttpClient client, Uri address, string expected, Process nginx)
    {
        using var budget = new CancellationTokenSource(Budget);
        while (!nginx.HasExited)
        {
            try
            {
                if (await client.GetStringAsync(address, budget.Token) == expected) return;
            }
            catch (HttpRequestException) when (!budget.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested) { }
            await Task.Delay(50, budget.Token);
        }
        throw new InvalidOperationException("The isolated Nginx process exited before serving the expected configuration.");
    }
}
