using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Host-measured facts for a component the center does not install itself. It never authorizes an
/// action: the fields describe what another Host surface already reports, and
/// <see cref="Found"/> only says whether anything was observed at all.
/// </summary>
public sealed record ComponentHostObservation(string ComponentId, LocalizedStatus Platform, LocalizedStatus Management,
    LocalizedStatus Version, LocalizedStatus State, LocalizedStatus Note, bool Found);

/// <summary>Read-only observation of a component whose lifecycle belongs to another application.</summary>
public interface IComponentHostObserver
{
    bool CanObserve(string componentId);
    /// <summary><paramref name="hostOs"/> is the platform the Host reported for itself, when known.</summary>
    Task<ComponentHostObservation?> ObserveAsync(string componentId, string? hostOs, CancellationToken cancellationToken);
}

/// <summary>
/// Nginx is not a ledgered managed runtime: the Web Server application already owns host Nginx
/// instances, and how one is installed depends on the platform (distribution package with a system
/// service on Linux, a RelaxKonOS-managed archive with its own service on Windows). The observer
/// therefore reports that domain's own facts instead of inventing a component ledger.
/// </summary>
public sealed class WebServerComponentObserver(IRemoteWebServerClient client) : IComponentHostObserver
{
    public bool CanObserve(string componentId) => componentId == "nginx";

    public async Task<ComponentHostObservation?> ObserveAsync(string componentId, string? hostOs, CancellationToken cancellationToken)
    {
        if (!CanObserve(componentId)) return null;
        var platform = PlatformKey(hostOs);
        var instances = await client.ListAsync(cancellationToken);
        if (instances.Count > 0)
        {
            var instance = instances[0];
            var status = await client.GetStatusAsync(instance.Id, cancellationToken);
            return new(componentId, platform,
                LocalizedStatus.Key("component_center.management." + instance.ManagementMode, instance.ManagementMode.ToString()),
                LocalizedStatus.Literal(instance.Version ?? "—"),
                LocalizedStatus.Key("component_center.web_server_state." + (status?.RuntimeState ?? WebServerRuntimeState.Unknown)),
                LocalizedStatus.Key("component_center.observation.managed_note"),
                Found: true);
        }
        var candidates = await client.ListIntegrationCandidatesAsync(cancellationToken);
        if (candidates.Count > 0)
            return new(componentId, platform, LocalizedStatus.Key("component_center.management.Candidate"),
                LocalizedStatus.Literal(candidates[0].Version ?? "—"), LocalizedStatus.Key("component_center.web_server_state.Unknown"),
                LocalizedStatus.Key("component_center.observation.candidate_note"), Found: true);
        return new(componentId, platform, LocalizedStatus.Literal("—"), LocalizedStatus.Literal("—"),
            LocalizedStatus.Key("component_center.web_server_state.Unknown"),
            LocalizedStatus.Key("component_center.observation.none"), Found: false);
    }

    private static LocalizedStatus PlatformKey(string? hostOs) => hostOs switch
    {
        "linux" => LocalizedStatus.Key("component_center.platform.linux", "Linux"),
        "windows" => LocalizedStatus.Key("component_center.platform.windows", "Windows"),
        _ => LocalizedStatus.Key("component_center.platform.unknown")
    };
}
