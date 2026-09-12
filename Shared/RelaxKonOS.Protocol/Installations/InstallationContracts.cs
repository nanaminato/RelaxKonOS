using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Installations;

[JsonConverter(typeof(JsonStringEnumConverter<InstallationServiceId>))]
public enum InstallationServiceId { Smb, Nginx, Frp, Mihomo, Docker, Git }
[JsonConverter(typeof(JsonStringEnumConverter<InstallationOperationKind>))]
public enum InstallationOperationKind { Install, Upgrade, Repair, Uninstall }
[JsonConverter(typeof(JsonStringEnumConverter<InstallationOperationState>))]
public enum InstallationOperationState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }
[JsonConverter(typeof(JsonStringEnumConverter<InstallationStage>))]
public enum InstallationStage
{
    Queued, Preflight, Preparing, UpdatingPackageLists, Downloading, Copying, Verifying,
    Extracting, Installing, Configuring, Activating, HealthChecking, RollingBack,
    Completed, Failed, Cancelled, Interrupted
}

/// <param name="Progress">Verified progress within the current stage, never overall progress.</param>
public sealed record InstallationOperationDto(Guid OperationId, InstallationServiceId Service,
    InstallationOperationKind Kind, InstallationOperationState State, InstallationStage Stage,
    int? Progress, string? ProblemCode, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, bool Cancellable);

/// <summary>A short-lived, actor-bound reference to an installation archive. The path used to
/// create it never becomes part of an installation request or operation record.</summary>
public sealed record InstallationFileReferenceDto(string Id, string FileName, long Length, DateTimeOffset ExpiresAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInstallationFileReferenceRequest(string Path);

public static class InstallationApiRoutes
{
    public const string Root = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/installations";
    public const string StartPattern = "/{service}/{kind}";
    public const string OperationPattern = "/{operationId:guid}";
    public const string CancelPattern = OperationPattern + "/cancel";
    public const string ActivePattern = "/active";
    public const string FileReferencePattern = "/{service}/file-reference";
    public static string Start(InstallationServiceId service, InstallationOperationKind kind) => $"{Root}/{service}/{kind}";
    public static string Operation(Guid id) => $"{Root}/{id:D}";
    public static string Cancel(Guid id) => Operation(id) + "/cancel";
    public static string Active(InstallationServiceId service) => $"{Root}/active?service={service}";
    public static string FileReference(InstallationServiceId service) => $"{Root}/{service}/file-reference";
}

// Reject unknown properties: none of these contracts can carry shell inputs or server paths.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SmbInstallationRequest(bool Confirmed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GitInstallationRequest(bool Confirmed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NginxInstallationRequest(bool Confirmed, string? Version = null, string? FileReferenceId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrpInstallationRequest(bool Confirmed, string? Version = null, bool Rollback = false, string? FileReferenceId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoInstallationRequest(bool Confirmed, string? Version = null, bool Rollback = false, string? FileReferenceId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DockerInstallationRequest(bool Confirmed);

public static class InstallationProblemCodes
{
    public const string StoreUnavailable = "installation.store_unavailable";
    public const string InvalidRequest = "installation.invalid_request";
    public const string ConfirmationRequired = "installation.confirmation_required";
    public const string IdempotencyRequired = "installation.idempotency_required";
    public const string IdempotencyConflict = "installation.idempotency_conflict";
    public const string ResourceConflict = "installation.resource_conflict";
    public const string NotCancellable = "installation.not_cancellable";
    public const string Interrupted = "installation.interrupted";
    public const string Cancelled = "installation.cancelled";
    public const string HelperProtocol = "installation.helper_protocol";
    public const string NotSupported = "installation.not_supported";
    public const string Failed = "installation.failed";
    public const string RecoveryUnknown = "installation.recovery_unknown";
    public const string HealthCheckFailed = "installation.health_check_failed";
    public const string FileReferenceUnavailable = "installation.file_reference_unavailable";
}
