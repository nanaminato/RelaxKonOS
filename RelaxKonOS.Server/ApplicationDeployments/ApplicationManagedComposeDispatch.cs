using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Bridges the application and Docker ledgers at the point where a Compose deployment can be accepted.</summary>
internal sealed class ApplicationManagedComposeDispatch(ApplicationDeploymentOperationStore operations,
    DockerStackOperationCoordinator? docker = null)
{
    internal const string OutcomeUnknown = "application-deployment.managed_compose_outcome_unknown";
    internal bool Available => docker is not null;

    internal async Task<DockerStackOperationDto> DispatchAsync(Guid operationId, Guid revisionId, ManagedComposePlan plan,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> environment, string credentialRevision,
        CancellationToken cancellationToken)
    {
        if (docker is null)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NotSupported, 503);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credentialRevision))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 500);
        var definition = plan.RenderDeployment(out _, environment);
        var intent = new ManagedComposeIntent(DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml),
            DockerStackValidation.Reference(credentialRevision));
        if (!operations.PrepareComposeDispatch(operationId, plan.InstanceId, revisionId, intent))
            return ObserveOriginal(operations.Get(operationId)!)
                ?? throw new ApplicationDeploymentException(OutcomeUnknown);

        var accepted = await docker.DeployManagedAsync(plan, environment, operationId, credentialRevision, cancellationToken);
        operations.BindComposeReceipt(operationId, accepted.OperationId);
        return accepted;
    }

    internal DockerStackOperationDto? ObserveOriginal(DeploymentEntry entry)
    {
        if (entry.Compose is not { } intent || docker is null) return null;
        var operation = entry.Operation;
        var receipt = docker.GetManagedByRequest(operation.ApplicationId, operation.OperationId, intent.PlanReference, intent.CredentialReference);
        if (receipt is null || receipt.Kind != DockerStackOperationKind.Deploy
            || receipt.ProjectName != DockerStackValidation.ApplicationComposeProjectPrefix + operation.ApplicationId.ToString("N")
            || intent.ReceiptId is { } savedId && receipt.OperationId != savedId)
            return null;
        operations.BindComposeReceipt(operation.OperationId, receipt.OperationId);
        return receipt;
    }

    internal DockerStackOperationDto? ObserveOriginal(Guid operationId) =>
        operations.Get(operationId) is { } entry ? ObserveOriginal(entry) : null;
}
