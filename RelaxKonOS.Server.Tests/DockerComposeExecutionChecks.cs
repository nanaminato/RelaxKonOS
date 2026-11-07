using System.Runtime.Loader;

internal static class DockerComposeExecutionChecks
{
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
