using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public interface IDockerProxyProjection
{
    DockerProxyLayerDto BuildLayer(DockerProxyResolution resolution);
    DockerProxyLayerDto EngineLayer(DockerProxyResolution resolution, bool applied, string problemCode, bool supported,
        DockerEngineProxyState? engineState, DockerDesktopProxyDto? desktop);
}
