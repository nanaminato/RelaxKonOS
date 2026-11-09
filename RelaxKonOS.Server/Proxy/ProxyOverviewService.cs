using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

internal sealed class ProxyOverviewService(Func<CancellationToken, Task<MihomoRuntimeObservation>> observeRuntime)
{
    internal static void Register(IServiceCollection services)
    {
        services.AddScoped(static _ => ProxyConnectionExecutionGate.Shared.AcquireOverview());
        services.AddSingleton(new ProxyReadSamplerRegistration(typeof(ProxyReadSampler<ProxyOverviewDto>), static (scopes, leases, runtime) =>
        {
            var overview = new ProxyOverviewService(ct => ProxyReadSampler<MihomoRuntimeObservation>.Resolve(runtime).ReadAsync(ct));
            return new ProxyReadSampler<ProxyOverviewDto>(scopes, leases, (scope, ct) => overview.GetAsync(
                // Resolve the code lease first so scope disposal releases it after the read dependencies.
                scope.GetRequiredService<ProxyConnectionExecutionGate.OverviewCode>(),
                scope.GetRequiredService<IMihomoControllerClient>(), scope.GetRequiredService<IProxyPlatformService>(),
                scope.GetRequiredService<IProxyProfileRepository>().GetActiveAsync, scope.GetRequiredService<IProxyRecoveryService>(), ct));
        }));
    }

    internal async Task<ProxyOverviewDto> GetAsync(ProxyConnectionExecutionGate.OverviewCode code,
        IMihomoOverviewReader controller, IProxyPlatformService platform,
        Func<CancellationToken, Task<ProxyProfileDto?>> getActiveProfile, IProxyRecoveryService recovery, CancellationToken ct)
    {
        ProxyProfileDto? active = null;
        ProxyRecoveryStatusDto? recoveryState = null;
        MihomoRuntimeObservation? sample = null;
        ControllerResult<bool>? reachable = null;
        ControllerResult<int>? count = null;
        var readout = await code.ReadAsync(new(ReadActiveProfileAsync, ReadRecoveryAsync, ReadRuntimeAsync, ReadReachableAsync,
            ReadTunAsync, CountConnectionsAsync), ct);
        var health = MihomoEngine.ProjectHealth(reachable!, recoveryState!, readout.TunEnabled);
        if (count is { Succeeded: false }) health = health with { State = ProxyHealthState.Degraded, ProblemCode = count.ProblemCode };
        return new ProxyOverviewDto(MihomoEngine.Id, MihomoEngine.Capabilities, await platform.GetCapabilitiesAsync(ct),
            sample!.Runtime, health, health.TunState == ProxyTunState.Enabled ? ProxyOperatingMode.Tun : ProxyOperatingMode.ListenerOnly,
            active, readout.ActiveConnections, recoveryState!, RuntimeInformation.OSDescription);

        async Task ReadActiveProfileAsync(CancellationToken token) => active = await getActiveProfile(token);
        async Task<bool> ReadRecoveryAsync(CancellationToken token) => (recoveryState = await recovery.GetStatusAsync(token)).RecoveryRequired;
        async Task<bool?> ReadRuntimeAsync(CancellationToken token) => (reachable = (sample = await observeRuntime(token)).Controller)?.Succeeded;
        async Task<bool> ReadReachableAsync(CancellationToken token) => (reachable = await controller.IsReachableAsync(token)).Succeeded;
        async Task<bool> ReadTunAsync(CancellationToken token) => await controller.GetTunEnabledAsync(token) is { Succeeded: true, Value: true };
        async Task<int> CountConnectionsAsync(CancellationToken token) =>
            (count = await controller.GetConnectionCountAsync(token)).Succeeded ? count.Value : 0;
    }
}
