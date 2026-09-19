using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>
/// Container resource ceiling applied at creation time. A null member means the template default is
/// used; it is never interpreted as "unlimited" by the server.
/// </summary>
public sealed record ApplicationResourceLimitsDto(
    [property: JsonPropertyName("cpuCores")] double? CpuCores = null,
    [property: JsonPropertyName("memoryBytes")] long? MemoryBytes = null,
    [property: JsonPropertyName("pidsLimit")] int? PidsLimit = null);

/// <summary>
/// A RelaxKonOS-managed named volume. Arbitrary host directory mounts are deliberately not
/// expressible in the first stage.
/// </summary>
public sealed record ApplicationVolumeDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("containerPath")] string ContainerPath,
    [property: JsonPropertyName("readOnly")] bool ReadOnly = false);

/// <summary>
/// One container environment entry. On every response <see cref="Value"/> is null for a secret
/// entry; only <see cref="SecretVersion"/> is reported, and secret bodies never enter a revision
/// snapshot, the operation ledger, or an audit record.
/// </summary>
public sealed record ApplicationConfigEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("isSecret")] bool IsSecret = false,
    [property: JsonPropertyName("secretVersion")] int? SecretVersion = null);

/// <summary>A published container port. Host proxy traffic reaches the container through the loopback
/// address, because a container's own localhost is never the host's localhost.</summary>
public sealed record ApplicationEndpointDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("containerPort")] int ContainerPort,
    [property: JsonPropertyName("hostPort")] int? HostPort,
    [property: JsonPropertyName("bindAddress")] string BindAddress,
    [property: JsonPropertyName("protocol")] string Protocol = "tcp");

/// <summary>Immutable per-template capability description used by the deployment wizard.</summary>
public sealed record ApplicationDeploymentTemplateDto(
    [property: JsonPropertyName("sourceKind")] ApplicationSourceKind SourceKind,
    [property: JsonPropertyName("templateVersion")] string TemplateVersion,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("defaultBaseImage")] string? DefaultBaseImage,
    [property: JsonPropertyName("supportedPlatforms")] IReadOnlyList<string> SupportedPlatforms,
    [property: JsonPropertyName("requiresArchive")] bool RequiresArchive,
    [property: JsonPropertyName("requiresImageReference")] bool RequiresImageReference,
    [property: JsonPropertyName("supportsSelfContained")] bool SupportsSelfContained,
    [property: JsonPropertyName("defaultContainerPort")] int DefaultContainerPort);

/// <summary>
/// A deployed application. The definition is the operator's intent; runtime facts such as
/// <see cref="ActualState"/> are reconciled against real Docker resources.
/// </summary>
public sealed record ApplicationDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ownerReference")] string OwnerReference,
    [property: JsonPropertyName("sourceKind")] ApplicationSourceKind SourceKind,
    [property: JsonPropertyName("workloadKind")] ApplicationWorkloadKind WorkloadKind,
    [property: JsonPropertyName("desiredState")] ApplicationDesiredState DesiredState,
    [property: JsonPropertyName("actualState")] ApplicationActualState ActualState,
    [property: JsonPropertyName("readinessLevel")] ApplicationReadinessLevel ReadinessLevel,
    [property: JsonPropertyName("healthCheckPath")] string? HealthCheckPath,
    [property: JsonPropertyName("containerPort")] int ContainerPort,
    [property: JsonPropertyName("hostPort")] int? HostPort,
    [property: JsonPropertyName("bindAddress")] string BindAddress,
    [property: JsonPropertyName("endpoint")] ApplicationEndpointDto? Endpoint,
    [property: JsonPropertyName("limits")] ApplicationResourceLimitsDto Limits,
    [property: JsonPropertyName("volumes")] IReadOnlyList<ApplicationVolumeDto> Volumes,
    [property: JsonPropertyName("configuration")] IReadOnlyList<ApplicationConfigEntryDto> Configuration,
    [property: JsonPropertyName("currentRevisionId")] Guid? CurrentRevisionId,
    [property: JsonPropertyName("currentRevisionNumber")] int? CurrentRevisionNumber,
    [property: JsonPropertyName("containerName")] string? ContainerName,
    [property: JsonPropertyName("containerId")] string? ContainerId,
    [property: JsonPropertyName("siteId")] string? SiteId,
    [property: JsonPropertyName("domain")] string? Domain,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("lastDeployedAt")] DateTimeOffset? LastDeployedAt = null,
    [property: JsonPropertyName("driftProblemCode")] string? DriftProblemCode = null);

/// <summary>
/// An immutable published version. Tags are input and display only: the published revision binds the
/// observed image identity, and a local build records an image ID rather than inventing a digest.
/// </summary>
public sealed record ApplicationRevisionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("sourceKind")] ApplicationSourceKind SourceKind,
    [property: JsonPropertyName("templateVersion")] string TemplateVersion,
    [property: JsonPropertyName("inputReference")] string InputReference,
    [property: JsonPropertyName("imageReference")] string ImageReference,
    [property: JsonPropertyName("imageId")] string? ImageId,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("baseImage")] string? BaseImage,
    [property: JsonPropertyName("workloadKind")] ApplicationWorkloadKind WorkloadKind,
    [property: JsonPropertyName("readinessLevel")] ApplicationReadinessLevel ReadinessLevel,
    [property: JsonPropertyName("healthCheckPath")] string? HealthCheckPath,
    [property: JsonPropertyName("entryPoint")] string EntryPoint,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments,
    [property: JsonPropertyName("containerPort")] int ContainerPort,
    [property: JsonPropertyName("hostPort")] int? HostPort,
    [property: JsonPropertyName("bindAddress")] string BindAddress,
    [property: JsonPropertyName("limits")] ApplicationResourceLimitsDto Limits,
    [property: JsonPropertyName("volumes")] IReadOnlyList<ApplicationVolumeDto> Volumes,
    [property: JsonPropertyName("configuration")] IReadOnlyList<ApplicationConfigEntryDto> Configuration,
    [property: JsonPropertyName("siteId")] string? SiteId,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent,
    [property: JsonPropertyName("createdByReference")] string CreatedByReference,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("problemCode")] string? ProblemCode = null);

/// <summary>An archive staged for a deployment. Only the reference id travels through the protocol.</summary>
public sealed record DeploymentStagedFileDto(
    [property: JsonPropertyName("referenceId")] string ReferenceId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>Bounded application log output. Lines are sanitized and length-limited by the server.</summary>
public sealed record DeploymentLogDto(
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("containerId")] string? ContainerId,
    [property: JsonPropertyName("revisionId")] Guid? RevisionId,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines,
    [property: JsonPropertyName("truncated")] bool Truncated);
