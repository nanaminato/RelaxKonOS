using RelaxKonOS.Protocol.ImageMirrors;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.ImageMirrors;

/// <summary>
/// Resolves Docker Hub references through the selected user mirror. This is deliberately a
/// server-side lookup: the Docker client API never receives a mirror endpoint from the caller.
/// </summary>
public interface IDockerImageMirrorResolver
{
    string Resolve(Guid userId, string imageReference);
}

public sealed class DockerImageMirrorResolver(IImageMirrorRepository mirrors) : IDockerImageMirrorResolver
{
    internal DockerComposeExecution Execution { private get; init; } = DockerComposeExecution.Shared;

    public string Resolve(Guid userId, string imageReference)
    {
        var selected = mirrors.GetSelected(userId, ImageMirrorTarget.Docker);
        if (selected is null) return imageReference;

        using var lease = Execution.Acquire<IDockerImageMirrorExecution>(DockerComposeExecution.ImageMirrorEntryType);
        return lease.Executor.Resolve(selected.Endpoint, imageReference);
    }
}
