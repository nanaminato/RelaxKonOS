namespace RelaxKonOS.Protocol.Docker;

public sealed record DockerContainerDto(string Id, string Names, string Image, string State, string Status);
/// <summary>Safe, bounded container inspection data shown in the Docker Manager details window.</summary>
public sealed record DockerContainerDetailsDto(
    string Id,
    string Name,
    string Image,
    string Created,
    string State,
    string Status,
    string Command,
    string WorkingDirectory,
    string RestartPolicy,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Mounts,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Environment,
    IReadOnlyDictionary<string, string> Labels);
public sealed record DockerImageDto(string Id, string Repository, string Tag, string Size, string CreatedSince);
public sealed record DockerNetworkDto(string Id, string Name, string Driver, string Scope);
public sealed record DockerVolumeDto(string Name, string Driver, string Mountpoint);
public sealed record DockerNetworkDetailsDto(string Id, string Name, string Driver, string Scope, IReadOnlyList<string> Containers);
public sealed record DockerVolumeDetailsDto(string Name, string Driver, string Mountpoint, IReadOnlyDictionary<string, string> Labels);

/// <summary>Structured container lifecycle request. Confirmation is required for irreversible actions.</summary>
public sealed record DockerContainerActionRequest(bool Force = false, bool Confirmed = false);
/// <summary>Docker permits renaming an existing container without recreating it.</summary>
public sealed record DockerContainerUpdateRequest(string Name);

/// <summary>
/// Stable result for a Docker operation. <see cref="LogLines"/> contains bounded, sanitized
/// command progress for operations whose output is safe to show in the Docker Manager; detailed
/// daemon diagnostics remain in the server logs.
/// </summary>
/// <param name="LogTruncated">True when the head of <see cref="LogLines"/> was dropped, so a reader
/// never mistakes a tail for the complete command output.</param>
public sealed record DockerOperationResult(bool Success, string ProblemCode, IReadOnlyList<string>? LogLines = null, bool LogTruncated = false);
public sealed record DockerImageOperationRequest(string ImageReference, bool Confirmed = false);
/// <summary>
/// Bounded runtime constraints for a created container. A null member leaves the Docker default in
/// place; it is never translated into an "unlimited" flag.
/// </summary>
public sealed record DockerContainerResourceOptions(
    double? CpuCores = null,
    long? MemoryBytes = null,
    int? PidsLimit = null,
    string? LogDriver = null,
    IReadOnlyList<string>? LogOptions = null);

/// <summary>
/// Structured container creation input. Options are kept separate from the command arguments so
/// the server can compose a safe <c>docker create</c> invocation without the client building CLI
/// strings. Labels are how a managed resource declares its owner.
/// </summary>
public sealed record DockerContainerCreateRequest(
    string Name,
    string Image,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyList<string>? Environment = null,
    IReadOnlyList<string>? Mounts = null,
    string? Network = null,
    string? RestartPolicy = null,
    IReadOnlyList<string>? Labels = null,
    DockerContainerResourceOptions? Resources = null);
public sealed record DockerNetworkCreateRequest(string Name, string Driver = "bridge", bool Confirmed = false);
/// <summary>Structured named-volume creation. Labels let higher-level domains prove ownership before
/// changing or deleting a volume.</summary>
public sealed record DockerVolumeCreateRequest(
    string Name,
    string Driver = "local",
    bool Confirmed = false,
    IReadOnlyList<string>? Labels = null);
public sealed record DockerContainerLogsDto(IReadOnlyList<string> Lines, bool Truncated);
public sealed record DockerContainerStatsDto(string ContainerId, string CpuPercent, string MemoryUsage, string NetworkIo, string BlockIo);
public sealed record DockerBuildRequest(string ContextDirectory, string ImageReference, string? Dockerfile = null);
/// <summary>Bounded base64 archive transfer. The server never accepts arbitrary host paths.</summary>
public sealed record DockerImageArchiveDto(string ImageReference, string ContentBase64);
