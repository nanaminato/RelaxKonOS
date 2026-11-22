namespace RelaxKonOS.Server.Docker;

public interface IDockerImageMirrorExecution
{
    string Resolve(string endpoint, string imageReference);
}
