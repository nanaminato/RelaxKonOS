using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>First engine implementation. It exposes only neutral contracts and cannot be reached by a RelaxKonOS RelaxKonOS.Client.</summary>
public sealed class MihomoEngine(
    IMihomoControllerClient controller,
    IMihomoConfigurationValidator validator,
    IProxyPlatformPaths paths,
    IProxyRecoveryService? tunSafety = null) : IProxyEngine
{
    public const string Id = "mihomo";
    internal static readonly ProxyEngineCapabilities Capabilities = new(true, true, true, true, true, true);
    public string EngineId => Id;

    public Task<ProxyEngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Capabilities);

    public async Task<ProxyHealthDto> GetHealthAsync(CancellationToken cancellationToken)
    {
        var reachable = await controller.IsReachableAsync(cancellationToken);
        var recovery = tunSafety is null ? new ProxyRecoveryStatusDto(false, false, null) : await tunSafety.GetStatusAsync(cancellationToken);
        return await ProjectHealthAsync(controller, reachable, recovery, cancellationToken);
    }

    internal static async Task<ProxyHealthDto> ProjectHealthAsync(IMihomoOverviewReader controller,
        ControllerResult<bool> reachable, ProxyRecoveryStatusDto recovery, CancellationToken cancellationToken)
        => ProjectHealth(reachable, recovery, reachable.Succeeded && !recovery.RecoveryRequired
            && await controller.GetTunEnabledAsync(cancellationToken) is { Succeeded: true, Value: true });

    // Only an unfinished recovery marker takes precedence over live TUN observation.
    internal static ProxyHealthDto ProjectHealth(ControllerResult<bool> reachable, ProxyRecoveryStatusDto recovery, bool tunEnabled)
    {
        var tunState = recovery.RecoveryRequired ? ProxyTunState.Recovering : tunEnabled ? ProxyTunState.Enabled : ProxyTunState.Disabled;
        if (recovery.RecoveryRequired)
            return new(reachable.Succeeded ? ProxyRuntimeState.Running : ProxyRuntimeState.Degraded, tunState,
                ProxyHealthState.RecoveryRequired, reachable.Succeeded, reachable.Succeeded, false, ProxyProblemCodes.RecoveryRequired);
        return reachable.Succeeded
            ? new(ProxyRuntimeState.Running, tunState, ProxyHealthState.Healthy, true, true, true)
            : new(ProxyRuntimeState.Degraded, tunState, ProxyHealthState.Degraded, false, false, false, reachable.ProblemCode);
    }

    public Task<string?> ValidateConfigurationAsync(string configurationPath, CancellationToken cancellationToken) => validator.ValidateAsync(configurationPath, cancellationToken);
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => controller.ReloadAsync(cancellationToken);
    public Task<IReadOnlyList<ProxyGroupDto>> GetGroupsAsync(CancellationToken cancellationToken) =>
        MihomoProxyGroupOrder.ReadGroupsAsync(controller, paths, cancellationToken);
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => controller.SelectGroupAsync(groupName, proxyName, cancellationToken);
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => controller.SetRoutingModeAsync(mode, cancellationToken);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) =>
        controller.TestProxyDelayAsync(proxyName, url, timeoutMilliseconds, cancellationToken);
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => controller.CloseConnectionAsync(connectionId, cancellationToken);
}

/// <summary>Goal 3 supplies the service-owned implementation; Goal 2 deliberately has no CLI or shell fallback.</summary>
public interface IMihomoConfigurationValidator
{
    Task<string?> ValidateAsync(string configurationPath, CancellationToken cancellationToken);
}

/// <summary>Safe pre-runtime default. Goal 3 replaces it with the native-service validator.</summary>
public sealed class UnavailableMihomoConfigurationValidator : IMihomoConfigurationValidator
{
    public Task<string?> ValidateAsync(string configurationPath, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(ProxyProblemCodes.RuntimeNotInstalled);
}
