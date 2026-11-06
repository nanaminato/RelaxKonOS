using System.Diagnostics;

internal static class DockerProcessRecoveryChecks
{
    private const string Actor = "crash-actor";
    private const string Key = "original-deploy";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly DockerStackDefinitionDto Definition = new("mb05-crash",
        "services:\n  witness:\n    image: alpine:3.22\n");
    private static readonly DockerStackDeployRequest Request = new(Definition,
        DockerStackValidation.DefinitionVersion(Definition.Name, Definition.ComposeYaml));

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("relaxkonos-docker-crash-");
        try
        {
            var root = directory.FullName;
            Guid operationId;
            using (var original = StartWorker(root, "execute"))
            {
                try
                {
                    var ready = await original.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                    TestAssert.Assert(Guid.TryParse(ready, out operationId) && !original.HasExited,
                        $"The original worker did not report an in-flight operation: {ready}");
                    var durable = DockerChecks.NewStackStore(root, 4).Get(operationId)?.Operation;
                    TestAssert.Assert(durable?.State == DockerStackOperationState.Running
                        && File.ReadAllLines(Witness(root)).SequenceEqual(["deploy"]),
                        "The operation must be durable and its external effect observed before the crash.");
                    original.Kill();
                    await original.WaitForExitAsync().WaitAsync(Budget);
                    TestAssert.Assert(original.ExitCode != 0, "The original worker must exit abnormally.");
                }
                finally
                {
                    await StopWorkerAsync(original);
                }
            }

            using var recovery = StartWorker(root, "recover");
            try
            {
                var output = await recovery.StandardOutput.ReadToEndAsync().WaitAsync(Budget);
                await recovery.WaitForExitAsync().WaitAsync(Budget);
                TestAssert.Assert(recovery.ExitCode == 0 && output.Trim() == operationId.ToString(),
                    $"The recovery process did not return the original operation: {output}");
            }
            finally
            {
                await StopWorkerAsync(recovery);
            }

            var recovered = DockerChecks.NewStackStore(root, 4).Get(operationId)?.Operation;
            TestAssert.Assert(recovered is { State: DockerStackOperationState.Interrupted,
                    ProblemCode: DockerStackProblem.Interrupted }
                && recovered.Services.Single().Service == "witness"
                && File.ReadAllLines(Witness(root)).SequenceEqual(["deploy"]),
                "A fresh reader must see the original terminal receipt and exactly one external effect.");
            Console.WriteLine($"Docker cross-process recovery passed: operation={operationId}, state={recovered!.State}, effects=1.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static async Task RunWorkerAsync(string root, string phase)
    {
        if (phase is not ("execute" or "recover")) throw new ArgumentException("Unknown Docker recovery phase.");
        var leases = new TestResidentBackgroundLeases();
        var effect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compose = new FakeComposeService
        {
            PreviewServices = ["witness"],
            BlockOnDeploy = phase == "execute",
            Observed = [new("witness", "fixture", "alpine:3.22", "running", "observed")],
            OnDeploy = () =>
            {
                TestAssert.Assert(Volatile.Read(ref leases.Active) == 1, "The mutation must run under its work lease.");
                File.AppendAllText(Witness(root), "deploy" + Environment.NewLine);
                effect.TrySetResult();
            }
        };
        using var coordinator = DockerChecks.NewStackCoordinator(root, compose, new TestApplicationLifetime(), 4, leases: leases);
        await coordinator.StartAsync(default);
        var restored = coordinator.GetByRequest(Actor, Key);
        var receipt = await coordinator.DeployAsync(Request, Actor, Key);
        if (phase == "execute")
        {
            await effect.Task.WaitAsync(Budget);
            Console.WriteLine(receipt.OperationId);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        TestAssert.Assert(receipt is { State: DockerStackOperationState.Interrupted,
                ProblemCode: DockerStackProblem.Interrupted } && compose.ListServicesCalls == 1
            && compose.DeployCalls == 0 && restored is not null
            && JsonSerializer.Serialize(receipt) == JsonSerializer.Serialize(restored), "Startup must observe once and replay only the original receipt.");
        await coordinator.StopAsync(default);
        Console.WriteLine(receipt.OperationId);
    }

    private static string Witness(string root) => Path.Combine(root, "compose-effects.log");

    private static Process StartWorker(string root, string phase)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--docker-recovery-worker");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(phase);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start Docker recovery worker.");
    }

    private static async Task StopWorkerAsync(Process worker)
    {
        if (!worker.HasExited) worker.Kill();
        await worker.WaitForExitAsync().WaitAsync(Budget);
    }
}
