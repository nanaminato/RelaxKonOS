using System.Net;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public sealed class DockerProxyProjection : IDockerProxyProjection
{
    public DockerProxyLayerDto BuildLayer(DockerProxyResolution resolution)
    {
        if (!resolution.Enabled) return new(DockerProxyTarget.Build, DockerProxyLayerState.Disabled, string.Empty, string.Empty);
        if (resolution.ProblemCode.Length > 0) return new(DockerProxyTarget.Build, DockerProxyLayerState.Failed, resolution.ProblemCode, string.Empty);
        if (!resolution.ApplyToBuild) return new(DockerProxyTarget.Build, DockerProxyLayerState.Disabled, string.Empty, string.Empty);
        // The Server owns its own docker child processes, so this layer has no host-side step and
        // cannot be left pending. What it cannot do is make an unreachable address work: a build runs
        // inside a container, where the local machine's own address is the container itself. That
        // case reports Applied together with an explicit warning instead of a detail-free success,
        // because the build argument really is in place and only the address is unusable.
        var unreachable = DockerProxyAddress.IsLoopbackUrl(resolution.HttpProxy)
            || DockerProxyAddress.IsLoopbackUrl(resolution.HttpsProxy);
        return new(DockerProxyTarget.Build, DockerProxyLayerState.Applied, string.Empty,
            unreachable ? DockerProxyDetail.BuildLoopbackUnreachable : DockerProxyDetail.BuildOnly);
    }

    public DockerProxyLayerDto EngineLayer(DockerProxyResolution resolution, bool applied, string problemCode, bool supported, DockerEngineProxyState? engineState, DockerDesktopProxyDto? desktop)
    {
        if (!resolution.Enabled || !resolution.ApplyToEngine)
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.Disabled, string.Empty, string.Empty);
        if (resolution.ProblemCode.Length > 0)
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.Failed, resolution.ProblemCode, string.Empty);
        if (!supported)
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.Unsupported, DockerProxyProblem.PlatformUnsupported, string.Empty);
        if (!applied)
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.Failed,
                problemCode.Length > 0 ? problemCode : DockerProxyProblem.EngineNotApplied, string.Empty);
        if (problemCode.Length > 0)
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.Failed, problemCode, string.Empty);

        // Docker Desktop always routes the daemon through its internal relay and re-points that relay
        // at the operator's upstream without restarting the engine, so the daemon reporting a proxy
        // only proves the relay is there. The upstream Docker Desktop actually stored is the only
        // value that can confirm this layer, and it is readable whether or not the app is running.
        if (desktop is not null)
        {
            if (desktop.IsManual && MatchesDesktopUpstream(desktop, resolution))
                return new(DockerProxyTarget.Engine, DockerProxyLayerState.Applied, string.Empty, string.Empty);
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.RestartRequired, string.Empty,
                desktop.IsManual ? DockerProxyDetail.DesktopUpstreamDiffers : DockerProxyDetail.DesktopRestartPending);
        }

        if (engineState is null)
            return new(DockerProxyTarget.Engine, DockerProxyLayerState.Failed, DockerProxyProblem.DaemonUnavailable, string.Empty);

        // Docker is allowed to normalise or relay the configured value, so on the platforms without
        // an internal relay a non-empty daemon proxy is the achievable proof that the layer is live;
        // the exact value is reported alongside it so drift stays visible to the operator.
        return engineState.HttpProxy.Length > 0 || engineState.HttpsProxy.Length > 0
            ? new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.Applied, string.Empty, string.Empty)
            : new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.RestartRequired, string.Empty, DockerProxyDetail.RestartPending);
    }

    /// <summary>
    /// Compares the stored upstream against the resolved preference. Docker Desktop may keep the
    /// address with a trailing slash or a different scheme casing, so those are normalised away
    /// before the two are considered different.
    /// </summary>
    private static bool MatchesDesktopUpstream(DockerDesktopProxyDto desktop, DockerProxyResolution resolution) =>
        SameUpstream(desktop.HttpProxy, resolution.HttpProxy)
        && SameUpstream(desktop.HttpsProxy.Length > 0 ? desktop.HttpsProxy : desktop.HttpProxy, resolution.HttpsProxy);

    private static bool SameUpstream(string stored, string expected) =>
        string.Equals(NormalizeUpstream(stored), NormalizeUpstream(expected), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUpstream(string value) => value.Trim().TrimEnd('/');
}
