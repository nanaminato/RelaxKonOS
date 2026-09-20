using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Domain;

/// <summary>
/// Host-global Docker proxy preference. The Docker daemon is a machine resource rather than a
/// tenant, so this is a single record whose last authorized writer owns it; it deliberately does
/// not follow the per-user image mirror model. Proxy URLs may embed credentials, so the storage
/// layer protects them at rest and no call site other than the Docker proxy service may read them.
/// </summary>
public sealed class DockerProxySetting
{
    /// <summary>The table holds exactly one row, enforced by a check constraint.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool Enabled { get; set; }
    public DockerProxySource Source { get; set; } = DockerProxySource.Custom;
    public string HttpProxy { get; set; } = string.Empty;
    public string HttpsProxy { get; set; } = string.Empty;
    public string NoProxy { get; set; } = string.Empty;
    /// <summary>Install the proxy on the daemon, which governs image pulls.</summary>
    public bool ApplyToEngine { get; set; }
    /// <summary>Install the proxy on the Server's own docker child processes, which governs builds.</summary>
    public bool ApplyToBuild { get; set; }
    /// <summary>
    /// True when the last engine-layer write reached the host successfully. It distinguishes "the
    /// host is configured but its daemon has not restarted" from "the write never happened", which
    /// the daemon's own read-back alone cannot tell apart.
    /// </summary>
    public bool EngineApplied { get; set; }
    /// <summary>Stable problem code from the last engine-layer apply; empty when it succeeded.</summary>
    public string EngineProblemCode { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Actor user id. It is recorded for auditability and is not a tenant boundary.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}
