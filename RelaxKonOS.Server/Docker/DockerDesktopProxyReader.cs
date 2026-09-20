using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Reads what Docker Desktop itself has configured. This exists because the daemon's own read-back
/// is not enough on that platform: Docker Desktop always points its daemon at its internal relay
/// (<c>http.docker.internal:3128</c>) and forwards to the operator's upstream, so the daemon can
/// report a healthy proxy while the upstream is something entirely different.
/// </summary>
public interface IDockerDesktopProxyReader
{
    /// <summary>The Docker Desktop proxy as stored on this host, or null when there is nothing to read.</summary>
    DockerDesktopProxyDto? Read();
}

/// <summary>Reads the per-user Docker Desktop settings file of this machine.</summary>
public sealed class WindowsDockerDesktopProxyReader : IDockerDesktopProxyReader
{
    public DockerDesktopProxyDto? Read() =>
        DockerDesktopProxySettings.ResolveSettingsPath() is { } path ? DockerDesktopProxySettings.TryRead(path) : null;
}

/// <summary>Hosts where Docker Desktop owns no daemon.</summary>
public sealed class UnsupportedDockerDesktopProxyReader : IDockerDesktopProxyReader
{
    public DockerDesktopProxyDto? Read() => null;
}
