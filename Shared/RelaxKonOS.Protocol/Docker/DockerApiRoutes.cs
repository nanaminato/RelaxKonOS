using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Docker;

/// <summary>Routes for the server-side local Docker integration.</summary>
public static class DockerApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Status = $"/{V1}/docker/status";
    public const string InstallationPlan = $"/{V1}/docker/installation/plan";
    public const string InstallationExecute = $"/{V1}/docker/installation/execute";
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
    public const string StackValidate = $"/{V1}/docker/stacks/validate";
    public const string Stacks = $"/{V1}/docker/stacks";
    public const string StackDeploy = $"/{V1}/docker/stacks/deploy";
    public const string StackServices = $"/{V1}/docker/stacks/{{name}}/services";
    public const string StackDefinition = $"/{V1}/docker/stacks/{{name}}/definition";
    public const string StackAction = $"/{V1}/docker/stacks/{{name}}/{{action}}";
}
