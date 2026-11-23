using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

// The previous contract prepared contexts but did not implement retention.
public static class ApplicationArchiveExtractor
{
    public static Task<(string? EntryPoint, string? Problem)> PrepareBuildContextAsync(Stream archive, string destination,
        int maximumEntries, long maximumExpandedBytes, int maximumPathDepth, ApplicationSourceKind kind,
        string? requestedImage, string? runtimeVersion, string? programEntry, bool selfContained,
        ApplicationWorkloadKind workloadKind, int port, string selfContainedImage, string webImage, string workerImage,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
