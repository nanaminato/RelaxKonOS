using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>
/// Template-specific build input. Unknown members are rejected: none of these contracts can carry
/// shell text, a Dockerfile, a host path, or an arbitrary build argument.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeploymentSourceInputDto(
    [property: JsonPropertyName("imageReference")] string? ImageReference = null,
    [property: JsonPropertyName("baseImage")] string? BaseImage = null,
    [property: JsonPropertyName("archiveReferenceId")] string? ArchiveReferenceId = null,
    [property: JsonPropertyName("runtimeVersion")] string? RuntimeVersion = null,
    [property: JsonPropertyName("programEntry")] string? ProgramEntry = null,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string>? Arguments = null,
    [property: JsonPropertyName("selfContained")] bool SelfContained = false);

/// <summary>
/// Creates the application record only. The source archive or image deliberately stays out of the
/// definition, because a published revision is what binds a source; the first deployment is a
/// separate, idempotent operation against the created record.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateApplicationRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sourceKind")] ApplicationSourceKind SourceKind,
    [property: JsonPropertyName("workloadKind")] ApplicationWorkloadKind WorkloadKind = ApplicationWorkloadKind.Web,
    [property: JsonPropertyName("readinessLevel")] ApplicationReadinessLevel ReadinessLevel = ApplicationReadinessLevel.Http,
    [property: JsonPropertyName("healthCheckPath")] string? HealthCheckPath = "/",
    [property: JsonPropertyName("containerPort")] int ContainerPort = 8080,
    [property: JsonPropertyName("hostPort")] int? HostPort = null,
    [property: JsonPropertyName("bindAddress")] string BindAddress = "127.0.0.1",
    [property: JsonPropertyName("limits")] ApplicationResourceLimitsDto? Limits = null,
    [property: JsonPropertyName("volumes")] IReadOnlyList<ApplicationVolumeDto>? Volumes = null,
    [property: JsonPropertyName("configuration")] IReadOnlyList<ApplicationConfigEntryDto>? Configuration = null,
    [property: JsonPropertyName("siteId")] string? SiteId = null);

/// <summary>
/// Replaces the stored application definition. Runtime fields are applied by the next deployment,
/// because changing ports, limits, or mounts requires replacing the container instance.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateApplicationRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("workloadKind")] ApplicationWorkloadKind WorkloadKind,
    [property: JsonPropertyName("readinessLevel")] ApplicationReadinessLevel ReadinessLevel,
    [property: JsonPropertyName("healthCheckPath")] string? HealthCheckPath = null,
    [property: JsonPropertyName("containerPort")] int ContainerPort = 8080,
    [property: JsonPropertyName("hostPort")] int? HostPort = null,
    [property: JsonPropertyName("bindAddress")] string BindAddress = "127.0.0.1",
    [property: JsonPropertyName("limits")] ApplicationResourceLimitsDto? Limits = null,
    [property: JsonPropertyName("volumes")] IReadOnlyList<ApplicationVolumeDto>? Volumes = null,
    [property: JsonPropertyName("configuration")] IReadOnlyList<ApplicationConfigEntryDto>? Configuration = null,
    [property: JsonPropertyName("siteId")] string? SiteId = null);

/// <summary>Queues a deployment that publishes a new immutable revision from the supplied source.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeployApplicationRequest(
    [property: JsonPropertyName("source")] DeploymentSourceInputDto Source,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>Queues a deployment that reuses an existing revision's image and configuration references.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RollbackApplicationRequest(
    [property: JsonPropertyName("revisionId")] Guid RevisionId,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>Shared start/stop/restart input. A forced stop is the only destructive variant.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplicationLifecycleRequest(
    [property: JsonPropertyName("force")] bool Force = false,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>
/// Removes a deployed application. Managed volumes are retained unless
/// <paramref name="DeleteVolumes"/> is explicitly confirmed, because they hold application data.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteApplicationRequest(
    [property: JsonPropertyName("deleteVolumes")] bool DeleteVolumes = false,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>Registers an existing server-side file. The path never enters a deployment request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDeploymentFileReferenceRequest(
    [property: JsonPropertyName("path")] string Path);
