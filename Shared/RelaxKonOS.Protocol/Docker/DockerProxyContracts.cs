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
    /// <summary>
    /// Build-time egress. Passed as a value-less build argument on the Server's own
    /// <c>docker build</c> command, with the value carried by that child process environment, so
    /// nothing is written to the host and no credential reaches a command line.
    /// </summary>
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
/// Saved proxy preference. Proxy URLs may embed credentials. Every value in this record is the
/// operator's own input and is returned verbatim, because a masked echo would make the form
/// unusable: saving it back would replace the real credential with the mask. Credentials are
/// therefore kept out of logs, audits, problem details, and layer diagnostics instead of being
/// hidden from the authorized operator who typed them. The values are protected at rest.
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
/// What Docker Desktop itself has stored in its own settings file. Docker Desktop ignores
/// <c>daemon.json</c> for proxies and always points its daemon at an internal relay
/// (<c>http.docker.internal:3128</c>), so the daemon's reported proxy is the relay address rather
/// than the upstream the operator chose. This record is the only way to show that upstream, and it
/// is also what makes "is our setting actually in effect" answerable on that platform.
/// </summary>
/// <param name="Mode">Docker Desktop's proxy mode, e.g. <c>manual</c> or <c>system</c>; empty when it is not set.</param>
/// <param name="SettingsPath">The file the values were read from, so the operator can inspect it.</param>
public sealed record DockerDesktopProxyDto(
    string Mode,
    string HttpProxy,
    string HttpsProxy,
    string NoProxy,
    string SettingsPath)
{
    /// <summary>True when Docker Desktop routes image pulls through an upstream we can compare against.</summary>
    public bool IsManual => string.Equals(Mode, "manual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Full proxy picture: the saved preference, the per-layer outcome, and the values the Docker
/// daemon actually reports. <c>Effective*</c> is read from the daemon, not from the saved file, so
/// an out-of-band host change is visible instead of being masked by the preference.
/// <see cref="DesktopProxy"/> carries the Docker Desktop upstream on hosts where that application
/// owns the daemon, and is null everywhere else.
/// </summary>
public sealed record DockerProxyStatusDto(
    DockerProxySettingsDto Settings,
    IReadOnlyList<DockerProxyLayerDto> Layers,
    string EffectiveHttpProxy,
    string EffectiveHttpsProxy,
    string EffectiveNoProxy,
    string ManagedProxyEndpoint,
    bool ManagedProxyAvailable,
    string Platform,
    DockerDesktopProxyDto? DesktopProxy = null);

public static class DockerProxyApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Proxy = $"/{V1}/docker/proxy";
}
