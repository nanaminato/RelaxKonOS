using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Docker;

/// <summary>Routes for the server-side local Docker integration.</summary>
public static class DockerApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Status = $"/{V1}/docker/status";
    /// <summary>Host-wide engine lifecycle action; <c>{action}</c> is a DockerEngineAction segment.</summary>
    public const string EngineAction = $"/{V1}/docker/engine/{{action}}";
    public const string Containers = $"/{V1}/docker/containers";
    public const string ContainerById = $"/{V1}/docker/containers/{{id}}";
    public const string ContainerAction = $"/{V1}/docker/containers/{{id}}/{{action}}";
    public const string Images = $"/{V1}/docker/images";
    public const string ImagePull = $"/{V1}/docker/images/pull";
    public const string ImageDelete = $"/{V1}/docker/images/{{id}}";
    public const string Networks = $"/{V1}/docker/networks";
    public const string Volumes = $"/{V1}/docker/volumes";
    public const string NetworkById = $"/{V1}/docker/networks/{{id}}";
    public const string VolumeByName = $"/{V1}/docker/volumes/{{name}}";
    public const string ContainerLogs = $"/{V1}/docker/containers/{{id}}/logs";
    public const string ContainerStats = $"/{V1}/docker/containers/{{id}}/stats";
    public const string ImageBuild = $"/{V1}/docker/images/build";
    public const string ImageExport = $"/{V1}/docker/images/{{id}}/export";
    public const string ImageImport = $"/{V1}/docker/images/import";
    /// <summary>Parses a definition and answers with the services it would run. Nothing is applied.</summary>
    public const string StackPreview = $"/{V1}/docker/stacks/preview";
    public const string Stacks = $"/{V1}/docker/stacks";
    /// <summary>Submits a deployment. Answers <c>202 Accepted</c> with a durable stack operation.</summary>
    public const string StackDeploy = $"/{V1}/docker/stacks/deploy";
    /// <summary>Ordered history of one project's operations, newest first.</summary>
    public const string StackOperations = $"/{V1}/docker/stacks/{{name}}/operations";
    /// <summary>The single active operation of one project, or <c>404</c> when it has none.</summary>
    public const string StackActiveOperation = $"/{V1}/docker/stacks/{{name}}/operations/active";
    public const string StackOperationById = $"/{V1}/docker/stack-operations/{{operationId}}";
    public const string StackOperationDiagnostics = $"/{V1}/docker/stack-operations/{{operationId}}/diagnostics";
    public const string StackOperationCancel = $"/{V1}/docker/stack-operations/{{operationId}}/cancel";
    public const string StackServices = $"/{V1}/docker/stacks/{{name}}/services";
    public const string StackDefinition = $"/{V1}/docker/stacks/{{name}}/definition";
    /// <summary>Whole-project lifecycle action. Answers <c>202 Accepted</c> with a durable stack operation.</summary>
    public const string StackAction = $"/{V1}/docker/stacks/{{name}}/{{action}}";

    /// <summary>Builds the canonical read route of one stack operation.</summary>
    public static string StackOperation(Guid operationId) => $"/{V1}/docker/stack-operations/{operationId:D}";

    public static string StackOperationDiagnosticsRoute(Guid operationId) => $"/{V1}/docker/stack-operations/{operationId:D}/diagnostics";

    public static string StackOperationCancelRoute(Guid operationId) => $"/{V1}/docker/stack-operations/{operationId:D}/cancel";
}
