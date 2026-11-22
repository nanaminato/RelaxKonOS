using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public enum DockerProxyEngineChange
{
    InvalidResolution,
    Retire,
    Unsupported,
    ConfirmationRequired,
    Install,
}

public interface IDockerProxyProjection
{
    DockerProxyEngineChange DecideEngineChange(DockerProxyResolution resolution, bool isSupported, bool confirmed);
    DockerProxyLayerDto BuildLayer(DockerProxyResolution resolution);
    DockerProxyLayerDto EngineLayer(DockerProxyResolution resolution, bool applied, string problemCode, bool supported,
        DockerEngineProxyState? engineState, DockerDesktopProxyDto? desktop);
}
