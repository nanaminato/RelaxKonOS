using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

internal sealed class ProxyOverviewService(
    IProxyRuntimeManager runtime,
    IProxyEngineRegistry engines,
    IProxyPlatformService platform,
    IProxyProfileRepository profiles,
    IProxyRecoveryService recovery)
{
    internal async Task<ProxyOverviewDto> GetAsync(CancellationToken ct)
    {
        var engine = engines.Find(MihomoEngine.Id)!;
        var active = await profiles.GetActiveAsync(ct);
        var recoveryState = await recovery.GetStatusAsync(ct);
        var health = await engine.GetHealthAsync(ct);
        var connectionCount = 0;
        if (health.ControllerReachable)
        {
            try { connectionCount = await engine.GetConnectionCountAsync(ct); }
            catch (ProxyObservationException error) { health = health with { State = ProxyHealthState.Degraded, ProblemCode = error.ProblemCode }; }
        }
        return new ProxyOverviewDto(MihomoEngine.Id, await engine.GetCapabilitiesAsync(ct), await platform.GetCapabilitiesAsync(ct),
            await runtime.GetAsync(MihomoEngine.Id, ct), health, health.TunState == ProxyTunState.Enabled ? ProxyOperatingMode.Tun : ProxyOperatingMode.ListenerOnly,
            active, connectionCount, recoveryState, RuntimeInformation.OSDescription);
    }
}
