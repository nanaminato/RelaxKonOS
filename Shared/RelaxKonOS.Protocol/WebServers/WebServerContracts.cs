using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.WebServers;

public enum WebServerType { Nginx }
public enum WebServerManagementMode { External, Integrated, Managed }
public enum WebServerRuntimeState { Unknown, Running, Stopped }
public enum WebServerOperationState { Queued, Running, Succeeded, Failed, Cancelled }
public enum WebServerLifecycleAction { Start, Stop, Restart, Reload, EnableAcmeHttp01 }
/// <summary>How a Windows managed installation handles a pre-existing RelaxKonOS Nginx directory.</summary>
public enum ManagedInstallExistingDirectoryAction { Reject, Reuse, Replace }

public sealed record WebServerCapabilities(
    [property: JsonPropertyName("canRead")] bool CanRead,
    [property: JsonPropertyName("canTestConfiguration")] bool CanTestConfiguration,
    [property: JsonPropertyName("canIntegrate")] bool CanIntegrate,
    [property: JsonPropertyName("canReload")] bool CanReload,
    [property: JsonPropertyName("canStart")] bool CanStart = false,
    [property: JsonPropertyName("canStop")] bool CanStop = false,
    [property: JsonPropertyName("canRestart")] bool CanRestart = false,
    [property: JsonPropertyName("canUninstall")] bool CanUninstall = false);

public sealed record WebServerDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("type")] WebServerType Type,
    [property: JsonPropertyName("managementMode")] WebServerManagementMode ManagementMode,
    [property: JsonPropertyName("executablePath")] string ExecutablePath,
    [property: JsonPropertyName("configurationPath")] string? ConfigurationPath,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt,
    [property: JsonPropertyName("capabilities")] WebServerCapabilities Capabilities);

public sealed record WebServerStatusDto(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("runtimeState")] WebServerRuntimeState RuntimeState,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record WebServerConfigTestResultDto(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

public sealed record IntegrateWebServerRequest(
    [property: JsonPropertyName("confirmed")] bool Confirmed);

/// <summary>Explicit acknowledgement for installing the provider's RelaxKonOS-owned instance.</summary>
public sealed record InstallManagedWebServerRequest(
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("packageId")] string? PackageId = null,
    [property: JsonPropertyName("existingDirectoryAction")] ManagedInstallExistingDirectoryAction ExistingDirectoryAction = ManagedInstallExistingDirectoryAction.Reject);

/// <summary>A validated local Nginx Windows ZIP staged by the server for one installation.</summary>
public sealed record WebServerInstallPackageDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileName")] string FileName);

/// <summary>Official Nginx Windows versions discovered by the server from nginx.org.</summary>
public sealed record WebServerInstallCatalogDto(
    [property: JsonPropertyName("mainlineVersion")] string? MainlineVersion,
    [property: JsonPropertyName("stableVersion")] string? StableVersion,
    [property: JsonPropertyName("versions")] IReadOnlyList<string> Versions,
    [property: JsonPropertyName("problemCode")] string ProblemCode = "");

/// <summary>Official direct-download location for a validated managed installation package.</summary>
public sealed record WebServerInstallDownloadDto(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url);

/// <summary>Explicit acknowledgement for deleting a RelaxKonOS-owned web-server installation.</summary>
public sealed record UninstallManagedWebServerRequest(
    [property: JsonPropertyName("confirmed")] bool Confirmed);

public sealed record WebServerOperationDto(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("state")] WebServerOperationState State,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("snapshotId")] string? SnapshotId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt);
