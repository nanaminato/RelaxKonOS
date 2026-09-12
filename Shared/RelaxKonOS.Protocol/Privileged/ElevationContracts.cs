using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Capabilities which may receive a short-lived host-administrator grant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostElevationCapability>))]
public enum HostElevationCapability
{
    FileRead,
    FileWrite,
    FileCreateDirectory,
    FileDelete,
    FileRename,
    FileMove,
    FileCopy,
    FileUpload,
    NativeServiceAction,
    NginxInstall,
    NginxLifecycle,
    NginxConfigurationWrite,
    ProxyServiceAction,
    FirewallChange,
    GitPackageInstall,
    SmbManage,
    SmbInstall,
    FrpInstall,
    MihomoInstall,
    DockerInstall,
    HostTimeChange,
    HostEnvironmentRead,
    HostEnvironmentChange,
    HostEnvironmentReveal,
}

/// <summary>Authenticated request for one non-file host capability and exact managed resource.</summary>
public sealed record HostElevationRequest(
    [property: JsonPropertyName("capability")] HostElevationCapability Capability,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("password")] string? Password = null,
    [property: JsonPropertyName("administratorUsername")] string? AdministratorUsername = null,
    [property: JsonPropertyName("includeDescendants")] bool IncludeDescendants = false);

public sealed record HostElevationResult(
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null);

public static class PrivilegedApiRoutes
{
    public const string Elevation = $"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/privileged/elevation";
}
