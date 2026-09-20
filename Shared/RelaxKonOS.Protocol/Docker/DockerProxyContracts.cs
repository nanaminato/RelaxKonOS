using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Docker;

/// <summary>Where the effective proxy values come from.</summary>
public enum DockerProxySource
{
    /// <summary>Proxy URLs supplied by the operator.</summary>
    Custom,
    /// <summary>The local listener of the RelaxKonOS-managed proxy runtime.</summary>
    ManagedProxy,
}

/// <summary>
/// Independent places a proxy has to be installed. They use unrelated host mechanisms, so each is
/// reported separately instead of being collapsed into one "proxy enabled" boolean.
/// </summary>
public enum DockerProxyTarget
{
    /// <summary>Daemon-side egress (image pulls). Needs host configuration and normally a daemon restart.</summary>
    Engine,
    /// <summary>Build-time and CLI egress. Applied to the Server's own <c>docker</c> child processes.</summary>
    Build,
}

/// <summary>Outcome of one proxy layer.</summary>
public enum DockerProxyLayerState
{
    /// <summary>The layer is switched off in the saved settings.</summary>
    Disabled,
    /// <summary>The layer is configured and already in effect.</summary>
    Applied,
    /// <summary>The layer is written but only takes effect after the host restarts its Docker daemon.</summary>
    RestartRequired,
    /// <summary>The host platform has no supported mechanism for this layer.</summary>
    Unsupported,
    /// <summary>The layer could not be configured or verified.</summary>
    Failed,
}

/// <summary>Result for one proxy layer. <see cref="Detail"/> is bounded and never carries credentials.</summary>
public sealed record DockerProxyLayerDto(DockerProxyTarget Target, DockerProxyLayerState State, string ProblemCode, string Detail);

/// <summary>
/// Saved proxy preference. Proxy URLs may embed credentials. <see cref="HttpProxy"/> and
/// <see cref="HttpsProxy"/> are the operator's own values and are returned verbatim to the
/// authorized caller so the form round-trips without the credential being lost; every other
/// surface — the daemon's reported values, problem details, layer diagnostics, logs, and audits —
/// masks the user information instead. The values are protected at rest.
/// </summary>
public sealed record DockerProxySettingsDto(
    bool Enabled,
    DockerProxySource Source,
    string HttpProxy,
    string HttpsProxy,
    string NoProxy,
    bool ApplyToEngine,
    bool ApplyToBuild);

/// <summary>
/// Proxy preference to save. An empty HTTPS proxy means "reuse the HTTP proxy". Installing the
/// daemon layer restarts Docker and interrupts running containers, so it requires
/// <see cref="Confirmed"/>.
/// </summary>
public sealed record SaveDockerProxySettingsRequest(
    bool Enabled,
    DockerProxySource Source,
    string? HttpProxy = null,
    string? HttpsProxy = null,
    string? NoProxy = null,
    bool ApplyToEngine = true,
    bool ApplyToBuild = true,
    bool Confirmed = false);

/// <summary>
/// Full proxy picture: the saved preference, the per-layer outcome, and the values the Docker
/// daemon actually reports. <c>Effective*</c> is read from the daemon, not from the saved file, so
/// an out-of-band host change is visible instead of being masked by the preference.
/// </summary>
public sealed record DockerProxyStatusDto(
    DockerProxySettingsDto Settings,
    IReadOnlyList<DockerProxyLayerDto> Layers,
    string EffectiveHttpProxy,
    string EffectiveHttpsProxy,
    string EffectiveNoProxy,
    string ManagedProxyEndpoint,
    bool ManagedProxyAvailable,
    string Platform);

public static class DockerProxyApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Proxy = $"/{V1}/docker/proxy";
}
