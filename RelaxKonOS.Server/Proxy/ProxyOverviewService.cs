using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

internal sealed class ProxyOverviewService(Func<CancellationToken, Task<MihomoRuntimeObservation>> observeRuntime)
{
    internal static void Register(IServiceCollection services) =>
        services.AddSingleton(new ProxyReadSamplerRegistration(typeof(ProxyReadSampler<ProxyOverviewDto>), static (scopes, leases, runtime) =>
        {
            var overview = new ProxyOverviewService(ct => ProxyReadSampler<MihomoRuntimeObservation>.Resolve(runtime).ReadAsync(ct));
            return new ProxyReadSampler<ProxyOverviewDto>(scopes, leases, (scope, ct) => overview.GetAsync(
                scope.GetRequiredService<IMihomoControllerClient>(), scope.GetRequiredService<IProxyPlatformService>(),
                scope.GetRequiredService<IProxyProfileRepository>().GetActiveAsync, scope.GetRequiredService<IProxyRecoveryService>(), ct));
        }));

    internal async Task<ProxyOverviewDto> GetAsync(IMihomoOverviewReader controller, IProxyPlatformService platform,
        Func<CancellationToken, Task<ProxyProfileDto?>> getActiveProfile, IProxyRecoveryService recovery, CancellationToken ct)
    {
        var active = await getActiveProfile(ct);
        var recoveryState = await recovery.GetStatusAsync(ct);
        var sample = await observeRuntime(ct);
        // An absent or unverified runtime may skip the version probe. Only overview still needs health.
        var reachable = sample.Controller ?? await controller.IsReachableAsync(ct);
        var health = await MihomoEngine.ProjectHealthAsync(controller, reachable, recoveryState, ct);
        var connectionCount = 0;
        if (health.ControllerReachable)
        {
            var count = await controller.GetConnectionCountAsync(ct);
            if (count.Succeeded) connectionCount = count.Value;
            else health = health with { State = ProxyHealthState.Degraded, ProblemCode = count.ProblemCode };
        }
        return new ProxyOverviewDto(MihomoEngine.Id, MihomoEngine.Capabilities, await platform.GetCapabilitiesAsync(ct),
            sample.Runtime, health, health.TunState == ProxyTunState.Enabled ? ProxyOperatingMode.Tun : ProxyOperatingMode.ListenerOnly,
            active, connectionCount, recoveryState, RuntimeInformation.OSDescription);
    }
}
