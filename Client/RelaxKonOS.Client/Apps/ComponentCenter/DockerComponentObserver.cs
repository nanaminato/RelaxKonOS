using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Engine API availability is not evidence of package ownership or host installation absence.</summary>
public sealed class DockerComponentObserver(IRemoteDockerClient client) : IComponentHostObserver
{
    public bool CanObserve(string componentId) => componentId == "docker";

    public async Task<ComponentHostObservation?> ObserveAsync(string componentId, string? hostOs, CancellationToken cancellationToken)
    {
        if (!CanObserve(componentId)) return null;
        return FromStatus(await client.GetStatusAsync(cancellationToken), hostOs);
    }

    public static ComponentHostObservation FromStatus(DockerStatusDto status, string? hostOs) => new("docker",
        LocalizedStatus.Key(hostOs is "linux" or "windows"
            ? "component_center.platform." + hostOs : "component_center.platform.unknown"),
        LocalizedStatus.Key("component_center.docker.ownership_unknown"),
        LocalizedStatus.Literal(status.IsAvailable ? status.ServerVersion ?? "—" : "—"),
        LocalizedStatus.Key(status.IsAvailable ? "component_center.docker.available" : "component_center.docker.unavailable"),
        LocalizedStatus.Key("component_center.docker.note"), status.IsAvailable);
}
