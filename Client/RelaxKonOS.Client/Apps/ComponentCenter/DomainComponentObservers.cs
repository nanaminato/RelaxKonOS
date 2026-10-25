using RelaxKonOS.Client.Apps.Squid;
using RelaxKonOS.Client.Apps.Tunnels;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

internal static class ComponentObservationText
{
    public static LocalizedStatus Key(string value) => LocalizedStatus.Key("component_center.domain." + value);
    public static LocalizedStatus Platform(string? os) => LocalizedStatus.Key(os is "linux" or "windows"
        ? "component_center.platform." + os : "component_center.platform.unknown");
}

/// <summary>Reads published Squid status, never accounts or native configuration for display.</summary>
public sealed class SquidComponentObserver(ISquidClient client) : IComponentHostObserver
{
    public bool CanObserve(string componentId) => componentId == "squid";
    public async Task<ComponentHostObservation?> ObserveAsync(string componentId, string? hostOs, CancellationToken cancellationToken)
    {
        if (!CanObserve(componentId)) return null;
        return FromStatus((await client.ReadAsync(cancellationToken)).Status, hostOs);
    }
    public static ComponentHostObservation FromStatus(SquidStatus status, string? os)
    {
        var supported = status.Supported;
        return new("squid", ComponentObservationText.Platform(os),
            ComponentObservationText.Key(!supported ? "unknown" : !status.Installed ? "absent"
                : status.Managed ? "managed" : "external"), LocalizedStatus.Literal("—"),
            ComponentObservationText.Key(!supported ? "unsupported" : status.RecoveryRequired ? "recovery"
                : !status.Installed ? "absent" : status.Running ? "running" : "stopped"),
            ComponentObservationText.Key("squid_note"), supported && status.Installed);
    }
}

/// <summary>Client executable observation and server process observation are independent facts.</summary>
public sealed class TunnelComponentObserver(IRemoteTunnelClient client) : IComponentHostObserver
{
    public bool CanObserve(string componentId) => componentId is "frpc" or "frps";
    public async Task<ComponentHostObservation?> ObserveAsync(string componentId, string? hostOs, CancellationToken cancellationToken)
    {
        if (componentId == "frpc") return FromClient(await client.GetRuntimeAsync(cancellationToken), hostOs);
        if (componentId == "frps") return FromServer(await client.GetManagedFrpsAsync(cancellationToken), hostOs);
        return null;
    }
    public static ComponentHostObservation FromClient(TunnelRuntimeDto runtime, string? os)
    {
        var found = runtime.State is TunnelRuntimeState.Available or TunnelRuntimeState.Running or TunnelRuntimeState.Stopped;
        return new("frpc", ComponentObservationText.Platform(os),
            ComponentObservationText.Key(found ? runtime.Mode == TunnelRuntimeMode.Managed ? "managed" : "external" : "unknown"),
            LocalizedStatus.Literal(found ? runtime.Version ?? "—" : "—"),
            ComponentObservationText.Key(runtime.State switch
            {
                TunnelRuntimeState.Available => "available",
                TunnelRuntimeState.Running => "running",
                TunnelRuntimeState.Stopped => "stopped",
                _ => "unknown"
            }), ComponentObservationText.Key("frpc_note"), found);
    }
    public static ComponentHostObservation FromServer(ManagedFrpsConfigurationDto server, string? os) =>
        new("frps", ComponentObservationText.Platform(os), ComponentObservationText.Key("unknown"),
            LocalizedStatus.Literal("—"), ComponentObservationText.Key(server.State switch
            {
                ManagedFrpsState.Running => "running",
                ManagedFrpsState.Starting => "starting",
                ManagedFrpsState.Failed => "failed",
                _ => "unknown"
            }), ComponentObservationText.Key("frps_note"), server.State == ManagedFrpsState.Running);
}
