using System.Runtime.Loader;

internal static class DockerComposeExecutionChecks
{
    internal static async Task VerifyRealParserAsync()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var data = Path.GetFullPath(Path.Combine(temp, $"relaxkonos-compose-config-{Guid.NewGuid():N}"));
        if (!string.Equals(Path.GetDirectoryName(data), temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compose config test directory escaped its temporary root.");
        Directory.CreateDirectory(data);
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var compose = new DockerComposeService(data, new DockerComposeExecution(TimeSpan.FromMilliseconds(30)));
            const string normal = """
                services:
                  web:
                    image: nginx:alpine
                    volumes:
                      - data:/data
                volumes:
                  data:
                """;
            var preview = await compose.PreviewAsync(new("ordinary", normal), budget.Token);
            TestAssert.Assert(preview.Services.Count == 1 && preview.Volumes.SequenceEqual(["ordinary_data"]),
                "The real parser must admit an ordinary named-volume document.");

            const string escaped = """
                services:
                  web:
                    image: nginx:alpine
                    volumes:
                      - data:/data
                volumes:
                  data:
                    name: "relaxkonos-\x63a-other_data"
                """;
            try
            {
                await compose.DeployAsync(new("ordinary", escaped), budget.Token);
                throw new InvalidOperationException("An escaped name reached the Engine mutation stage.");
            }
            catch (DockerStackException error) when (error.ProblemCode == DockerStackProblem.ApplicationResourceManaged)
            {
                TestAssert.Assert(!Directory.EnumerateFiles(Path.Combine(data, "ordinary")).Any(),
                    "A rejected definition must not leave a published configuration or a pending draft.");
            }
            Console.WriteLine("Real Docker Compose config parsed an ordinary project and refused an escaped application volume before up.");
        }
        finally { Directory.Delete(data, recursive: true); }
    }

    internal static async Task RunAsync()
    {
        var gate = new DockerComposeExecution(TimeSpan.FromMilliseconds(30));
        var data = Path.GetTempPath();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = budget.Token;
        var retained = new List<Task>();
        for (var index = 0; index < 5; index++)
        {
            var (code, completed, failed) = await ExecuteAsync(gate, data, ct);
            retained.Add(completed);
            retained.Add(failed);
            while (gate.CurrentCode is not null) await Task.Delay(20, ct);
            while (code.IsAlive)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20, ct);
            }
        }
        GC.KeepAlive(retained);
        TestAssert.Assert(!AssemblyLoadContext.Default.Assemblies.Any(assembly => assembly.GetName().Name == "RelaxKonOS.ServerModule.Docker"),
            "Compose execution must never load into the default context.");

        using (var lease = gate.Acquire(data)) lease.RequireRecovery();
        var quarantined = gate.CurrentCode;
        await Task.Delay(100, ct);
        TestAssert.Assert(quarantined is { IsAlive: true } && ReferenceEquals(quarantined, gate.CurrentCode),
            "Unconfirmed cleanup must pin its original execution generation.");
        try { using var refused = gate.Acquire(data); throw new Exception("Expected recovery admission refusal."); }
        catch (DockerComposeCleanupException) { }
        Console.WriteLine("PASS DOCKER COMPOSE COLLECTIBLE: five idle generations collected with retained tasks, unknown cleanup pins its generation");
    }

    private static async Task<(WeakReference Code, Task Completed, Task Failed)> ExecuteAsync(DockerComposeExecution gate,
        string data, CancellationToken ct)
    {
        using var first = new DockerComposeService(data, gate);
        using var second = new DockerComposeService(data, gate);
        var code = gate.CurrentCode ?? throw new InvalidOperationException("Compose execution did not load.");
        var completed = first.ListServicesAsync("invalid/name", ct);
        TestAssert.Assert((await completed).Count == 0, "Invalid project observation must avoid calling Docker.");
        var failed = second.PreviewAsync(new DockerStackDefinitionDto("invalid/name", "services: {}"), ct);
        try { await failed; throw new Exception("Expected invalid project rejection."); }
        catch (DockerStackException error) when (error.ProblemCode == DockerStackProblem.InvalidName && error.StatusCode == 400)
        {
            TestAssert.Assert(error.InnerException is null, "The Host must receive a copied Compose problem.");
        }
        first.Dispose();
        await Task.Delay(100, ct);
        TestAssert.Assert(ReferenceEquals(code, gate.CurrentCode), "One active scope must retain the shared execution generation.");
        return (code, completed, failed);
    }
}
