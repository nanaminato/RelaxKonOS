using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>
/// Stable, coarse-grained description of the RelaxKonOS Server host supplied after authentication.
/// This describes the server process host, never the connecting client or the authenticated user.
/// </summary>
public sealed record ServerDescriptorDto(
    [property: JsonPropertyName("platform")] PlatformKind Platform,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

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
}
