using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// The effective outbound-proxy values for this host, resolved from the shared preference. Docker
/// layers and host-initiated download clients read the same resolution, so a saved setting can
/// never be applied to one consumer and silently skipped for another.
/// </summary>
public sealed record DockerProxyResolution(
    bool Enabled,
    DockerProxySource Source,
    string HttpProxy,
    string HttpsProxy,
    string NoProxy,
    bool ApplyToBuild,
    bool ApplyToEngine,
    bool ApplyToImageTags,
    bool ApplyToRuntimeDownloads,
    string ManagedProxyEndpoint,
    bool ManagedProxyAvailable,
    string ProblemCode)
{
    /// <summary>Nothing is configured and nothing may be applied.</summary>
    public static DockerProxyResolution Disabled(string problemCode = "") =>
        new(false, DockerProxySource.Custom, "", "", "", false, false, false, false, "", false, problemCode);

    /// <summary>True when the resolution is usable, so a layer may actually be installed.</summary>
    public bool IsUsable => Enabled && ProblemCode.Length == 0;
    public bool BuildLayerActive => IsUsable && ApplyToBuild;
    public bool EngineLayerRequested => IsUsable && ApplyToEngine;
    public bool ImageTagsActive => IsUsable && ApplyToImageTags;
    public bool RuntimeDownloadsActive => IsUsable && ApplyToRuntimeDownloads;
}
