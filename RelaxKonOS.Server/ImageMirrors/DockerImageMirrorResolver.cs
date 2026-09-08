using RelaxKonOS.Protocol.ImageMirrors;
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
    public string Resolve(Guid userId, string imageReference)
    {
        var selected = mirrors.GetSelected(userId, ImageMirrorTarget.Docker);
        return selected is not null && TryGetDockerHubPath(imageReference, out var path)
            ? $"{selected.Endpoint}/{path}"
            : imageReference;
    }

    private static bool TryGetDockerHubPath(string imageReference, out string path)
    {
        path = string.Empty;
        var slash = imageReference.IndexOf('/');
        if (slash < 0)
        {
            path = $"library/{imageReference}";
            return true;
        }

        var firstSegment = imageReference[..slash];
        var remainder = imageReference[(slash + 1)..];
        if (firstSegment.Equals("docker.io", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase))
        {
            path = remainder.Contains('/') ? remainder : $"library/{remainder}";
            return true;
        }

        // Docker treats a first path component containing '.' or ':' (and localhost) as an
        // explicit registry. Leave those references intact rather than redirecting them.
        if (firstSegment.Contains('.') || firstSegment.Contains(':')
            || firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        path = imageReference;
        return true;
    }
}
