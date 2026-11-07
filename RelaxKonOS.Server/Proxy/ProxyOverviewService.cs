using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

internal sealed class ProxyOverviewService(
    MihomoRuntimeManager runtime,
    IMihomoControllerClient controller,
    IProxyPlatformService platform,
    IProxyProfileRepository profiles,
    IProxyRecoveryService recovery)
{
    internal async Task<ProxyOverviewDto> GetAsync(CancellationToken ct)
    {
        var active = await profiles.GetActiveAsync(ct);
        var recoveryState = await recovery.GetStatusAsync(ct);
        Task<ControllerResult<bool>>? observation = null;
        Task<ControllerResult<bool>> ObserveAsync(CancellationToken token) =>
            observation ??= controller.IsReachableAsync(token);

        // Managed-runtime classification samples the same controller under its existing gate.
        // When the runtime has no verified installation, health can still observe the controller.
        var runtimeState = await runtime.GetObservedAsync(ObserveAsync, ct);
        var health = await MihomoEngine.ProjectHealthAsync(controller, await ObserveAsync(ct), recoveryState, ct);
        var connectionCount = 0;
        if (health.ControllerReachable)
        {
            var count = await controller.GetConnectionCountAsync(ct);
            if (count.Succeeded) connectionCount = count.Value;
            else health = health with { State = ProxyHealthState.Degraded, ProblemCode = count.ProblemCode };
        }
        return new ProxyOverviewDto(MihomoEngine.Id, MihomoEngine.Capabilities, await platform.GetCapabilitiesAsync(ct),
            runtimeState, health, health.TunState == ProxyTunState.Enabled ? ProxyOperatingMode.Tun : ProxyOperatingMode.ListenerOnly,
            active, connectionCount, recoveryState, RuntimeInformation.OSDescription);
    }
}
