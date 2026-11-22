namespace RelaxKonOS.Server.Docker;

public sealed class DockerImageMirrorExecution : IDockerImageMirrorExecution
{
    public string Resolve(string endpoint, string imageReference) =>
        TryGetDockerHubPath(imageReference, out var path) ? $"{endpoint}/{path}" : imageReference;

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
