using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

/// <summary>Authenticated deployment facts. These are host capabilities, not app permissions.</summary>
public sealed record ServerCapabilitiesDto(
    [property: JsonPropertyName("mode")] ServerMode Mode,
    [property: JsonPropertyName("executionIdentity")] ServerExecutionIdentityDto ExecutionIdentity,
    [property: JsonPropertyName("listener")] ServerListenerDto Listener,
    [property: JsonPropertyName("authentication")] ServerAuthenticationDto Authentication,
    [property: JsonPropertyName("capabilities")] ServerHostCapabilitiesDto Capabilities,
    [property: JsonPropertyName("limitations")] IReadOnlyList<string> Limitations);

[JsonConverter(typeof(JsonStringEnumConverter<ServerMode>))]
public enum ServerMode { User, System }

public sealed record ServerExecutionIdentityDto(int Uid, string Username, string HomeDirectory);
public sealed record ServerListenerDto(string Scope);
public sealed record ServerAuthenticationDto(string Kind, string PamTransport);

/// <summary>Frozen booleans allow clients to remove unsafe entry points before they are opened.</summary>
public sealed record ServerHostCapabilitiesDto(
    bool Files,
    bool Terminal,
    bool Git,
    bool Metrics,
    bool Processes,
    bool Guardian,
    bool Docker,
    bool Firewall,
    bool FileServices,
    bool WebServer,
    bool Certificates,
    bool Tunnels,
    bool Proxy,
    bool PrivilegedOperations);

public static class ServerApiRoutes
{
    public const string Capabilities = $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/server/capabilities";
}
