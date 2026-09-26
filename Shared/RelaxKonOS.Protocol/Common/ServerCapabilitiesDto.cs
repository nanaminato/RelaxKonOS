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

/// <summary>
/// Whether this Server may execute ordinary operations (files, terminal, Git) as the authenticated
/// identity. It is a per-login fact, not a deployment fact: the same Server answers differently for
/// root than for a regular account. When <see cref="Available"/> is false the client must say so up
/// front instead of letting the first folder open fail. <see cref="Reason"/> is a stable kebab-case
/// code, never a description: text belongs to the client's localization packs.
/// </summary>
public sealed record ServerExecutionEligibilityDto(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reason")] string? Reason,
    /// <summary>Root System Mode uses closed privileged file operations without entering the
    /// ordinary user-execution worker. This is false for alias and User Mode sessions.</summary>
    [property: JsonPropertyName("privilegedFilesAvailable")] bool PrivilegedFilesAvailable);

/// <summary>
/// Stable reason codes for <see cref="ServerExecutionEligibilityDto"/>. A client localizes from these
/// names and never from the text the Server happens to send, so both sides must share them instead of
/// matching string literals on their own.
/// </summary>
public static class ServerExecutionEligibilityReasons
{
    public const string ReservedIdentity = "reserved-identity";
    public const string SystemAccount = "system-account";
    public const string UnverifiedHomeDirectory = "unverified-home-directory";
    public const string ServerAccountRequired = "server-account-required";
    public const string WindowsProfileRequired = "windows-profile-required";
    public const string UnsupportedPlatform = "unsupported-platform";
}

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
    bool PrivilegedOperations,
    /// <summary>Containerized application deployment depends on host Docker access, so it follows
    /// the same boundary instead of introducing a second privileged path.</summary>
    bool ApplicationDeployments = false);

public static class ServerApiRoutes
{
    public const string Capabilities = $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/server/capabilities";
}
