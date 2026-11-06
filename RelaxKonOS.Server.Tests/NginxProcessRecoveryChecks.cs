using System.Diagnostics;

internal static class NginxProcessRecoveryChecks
{
    private const string Instance = "isolated-nginx";
    private const string Key = "reload-crash";
    private const string Actor = "nginx-live-fixture";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("Expected an installed Linux Nginx executable.");

        var directory = Directory.CreateTempSubdirectory("relaxkonos-nginx-crash-");
        try
        {
            var root = directory.FullName;
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            WriteConfig(root, port, "before");
            using var nginx = StartNginx(executable, root);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                    { Timeout = TimeSpan.FromSeconds(2) };
                var address = new Uri($"http://127.0.0.1:{port}/");
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

                var store = OpenStore(root);
                var restored = await store.GetAsync(operationId, default);
                var replay = await store.StartAsync(Key, Instance, "reload", Actor,
                    _ => throw new InvalidOperationException("Recovered reload was executed again."), default);
                var reopened = await OpenStore(root).GetAsync(operationId, default);
                TestAssert.Assert(restored is { State: WebServerOperationState.Failed, Stage: "interrupted",
                        ProblemCode: "webserver.operation_interrupted" }
                    && replay == restored && reopened == restored && CountReloads(root) == 1,
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
        var store = OpenStore(root);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = await store.StartAsync(Key, Instance, "reload", Actor, async ct =>
        {
            using var command = StartNginx(executable, root, "-s", "reload");
            await command.WaitForExitAsync(ct);
            if (command.ExitCode != 0) throw new InvalidOperationException("Isolated Nginx reload failed.");
            accepted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return WebServerOperationResult.Success;
        }, default);
        await accepted.Task.WaitAsync(Budget);
        Console.WriteLine(receipt.OperationId);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static WebServerOperationStore OpenStore(string root)
    {
        var environment = new TestHostEnvironment(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
        return new(environment, new HostOperationJournal(environment, configuration), NullLogger<WebServerOperationStore>.Instance, new TestResidentBackgroundLeases());
    }

    private static Process StartNginx(string executable, string root, params string[] extra)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "-p", root + "/", "-c", Path.Combine(root, "nginx.conf") }.Concat(extra))
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start isolated Nginx.");
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
                    listen 127.0.0.1:{{port}};
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
