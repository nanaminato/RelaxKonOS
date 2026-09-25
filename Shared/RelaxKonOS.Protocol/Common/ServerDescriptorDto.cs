using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>
/// Stable, coarse-grained description of the RelaxKonOS Server host supplied after authentication.
/// This describes the server process host, never the connecting client or the authenticated user.
/// </summary>
public sealed record ServerDescriptorDto(
    [property: JsonPropertyName("platform")] HostPlatformKind Platform,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("host")] ServerCapabilitiesDto? Host = null);

/// <summary>Stable server feature identifiers used by application package requirements.</summary>
public static class ServerCapabilities
{
    public const string Files = "server.files";
    public const string Metrics = "server.metrics";
    public const string Processes = "server.processes";
    public const string Terminal = "server.terminal";
    public const string PosixPermissions = "server.posix.permissions";
    public const string Firewall = "server.firewall";
    public const string Git = "server.git";
    public const string Guardian = "server.guardian";
    public const string Docker = "server.docker";
    public const string ApplicationDeployments = "server.application-deployments";
    public const string FileServices = "server.file-services";
    public const string WebServer = "server.web-server";
    public const string Certificates = "server.certificates";
    public const string Tunnels = "server.tunnels";
    public const string Proxy = "server.proxy";
}
