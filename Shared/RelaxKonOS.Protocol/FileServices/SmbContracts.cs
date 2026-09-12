using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.FileServices;

public sealed record FileServiceStatusDto(
    [property: JsonPropertyName("protocol")] FileServiceProtocol Protocol,
    [property: JsonPropertyName("state")] FileServiceRuntimeState State,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("serviceActive")] bool ServiceActive,
    [property: JsonPropertyName("port445Listening")] bool Port445Listening,
    [property: JsonPropertyName("healthProblemCode")] string? HealthProblemCode = null);

public sealed record FileServiceCapabilitiesDto(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("installSupported")] bool InstallSupported,
    [property: JsonPropertyName("sambaCredentialsSupported")] bool SambaCredentialsSupported,
    [property: JsonPropertyName("managedSharesSupported")] bool ManagedSharesSupported,
    [property: JsonPropertyName("windowsShareSecuritySupported")] bool WindowsShareSecuritySupported,
    [property: JsonPropertyName("problemCode")] string? ProblemCode = null);

public sealed record FileSharePermissionDto(
    [property: JsonPropertyName("principal")] string Principal,
    [property: JsonPropertyName("access")] FileShareAccess Access);

public sealed record FileShareDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("readOnly")] bool ReadOnly,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("guestAllowed")] bool GuestAllowed,
    [property: JsonPropertyName("permissions")] IReadOnlyList<FileSharePermissionDto> Permissions,
    [property: JsonPropertyName("managed")] bool Managed,
    [property: JsonPropertyName("drifted")] bool Drifted = false);

public sealed record UpsertFileShareRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("readOnly")] bool ReadOnly,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("guestAllowed")] bool GuestAllowed,
    [property: JsonPropertyName("permissions")] IReadOnlyList<FileSharePermissionDto> Permissions);

public sealed record FileServiceUserDto(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("eligible")] bool Eligible);

/// <summary>Secret is intentionally write-only: no password field exists in a response DTO.</summary>
public sealed record SetSambaPasswordRequest([property: JsonPropertyName("password")] string Password);

public sealed record FileServiceOperationResultDto(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("problemCode")] string? ProblemCode = null);

public sealed record FileServiceConnectionInfoDto(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("windowsUncPrefix")] string WindowsUncPrefix,
    [property: JsonPropertyName("smbUriPrefix")] string SmbUriPrefix);
