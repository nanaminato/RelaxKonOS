using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.ApplicationDeployments;

internal static class CatalogComposeCrashChecks
{
    private static readonly Guid InstanceId = Guid.Parse("ac8f5f2b-423b-44ce-8fa9-4e18a0e6fc62");
    private const string Actor = "compose-crash-fixture";
    private const string Key = "fixture-original-install-key";
    private const string CredentialVersion = "fixture-secret-version-1";
    private static readonly string RequestReference = ApplicationDeploymentOperationStore.Reference("fixture-request");

    internal static async Task RunAsync(string root, string phase)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The process crash fixture requires Linux.");
        var environment = new TestHostEnvironment(root);
        var options = new ApplicationDeploymentOptions { RootDirectory = "app" };
        var operations = new ApplicationDeploymentOperationStore(environment, options);
        var lifetime = new TestApplicationLifetime();
        var release = ApplicationCatalog.Require("fixture-web-db-plan", "1.0.0");
        var plan = release.CompileTopology(InstanceId, 18083);
        var compose = new FakeComposeService
        {
            Observed = [.. plan.Services.Select(service => new DockerStackServiceDto(
                service.Id, plan.ContainerName(service.Id), service.ImageReference, "running", "running"))],
        };
        using var docker = DockerChecks.NewStackCoordinator(Path.Combine(root, "docker"), compose, lifetime, 4);
        await docker.StartAsync(default);
        var dispatch = new ApplicationManagedComposeDispatch(operations, docker);
        var idPath = Path.Combine(root, "parent-id");
        if (phase == "child")
        {
            var initial = operations.Create(InstanceId, "compose-crash", DeploymentOperationKind.Deploy, Actor, Key,
                RequestReference, ApplicationDeploymentService.Resources(InstanceId), out _).Operation;
            operations.Update(initial.OperationId, prior => prior with
            {
                State = DeploymentOperationState.Running,
                Stage = DeploymentStage.Creating,
                Cancellable = false,
            }, "fixture-dispatch-stage");
            File.WriteAllText(idPath, initial.OperationId.ToString("D"));
            var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["web"] = new Dictionary<string, string> { ["DB_PASSWORD"] = "synthetic-crash-fixture" },
                ["db"] = new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "synthetic-crash-fixture" },
            };
            await dispatch.DispatchAsync(initial.OperationId, Guid.NewGuid(), plan, inputs, CredentialVersion, default);
            throw new InvalidOperationException("The child must be killed before writing the association.");
        }

        var parent = Guid.Parse(File.ReadAllText(idPath));
        var entry = operations.Get(parent)!;
        TestAssert.Assert(entry is { Compose.ReceiptId: null }, "The original app ledger must contain intent without association.");
        var child = docker.GetManagedByRequest(InstanceId, parent, entry.Compose!.PlanReference, entry.Compose.CredentialReference);
        if (phase == "recover")
            TestAssert.Assert(child is { State: DockerStackOperationState.Succeeded }, "A successful original Docker receipt must survive SIGKILL.");
        else
            TestAssert.Assert(child is null, "The missing-receipt fixture must have no Docker child.");
        var logs = new ApplicationDeploymentLiveLogs();
        var service = new ApplicationDeploymentService(null!, null!, null!, null!, null!, options, environment,
            logs, null!, NullLogger<ApplicationDeploymentService>.Instance);
        var coordinator = new ApplicationDeploymentCoordinator(operations, null!, service, options, logs, lifetime,
            new CatalogUpdateEventPublisher(), NullLogger<ApplicationDeploymentCoordinator>.Instance, dispatch);
        await coordinator.StartAsync(default);
        var recovered = operations.FindIdempotent(InstanceId, DeploymentOperationKind.Deploy, Actor, Key, RequestReference)!;
        TestAssert.Assert(recovered.Operation.OperationId == parent && recovered.Compose?.ReceiptId == child?.OperationId
            && recovered.Operation.RecoveryProblemCode == ApplicationManagedComposeDispatch.OutcomeUnknown && compose.ManagedDeployCalls == 0,
            "Startup must preserve the original parent, only bind the original child, and avoid redispatch.");
        try
        {
            operations.Create(InstanceId, "compose-crash", DeploymentOperationKind.Deploy, Actor, "different-key",
                RequestReference, ApplicationDeploymentService.Resources(InstanceId), out _);
            throw new InvalidOperationException("A conflicting operation was accepted.");
        }
        catch (ApplicationDeploymentException error) when (error.ProblemCode == ApplicationDeploymentProblemCodes.ResourceConflict) { }
        await docker.StopAsync(default);
        Console.WriteLine($"PASS CA-03 SIGKILL {phase} parent={parent} child={child?.OperationId.ToString() ?? "missing"} redispatch=0");
    }
}
