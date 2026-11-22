namespace RelaxKonOS.Server.Docker;

public sealed record DockerEngineProxyState(string HttpProxy, string HttpsProxy, string NoProxy);
