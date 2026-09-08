using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>First engine implementation. It exposes only neutral contracts and cannot be reached by a RelaxKonOS RelaxKonOS.Client.</summary>
public sealed class MihomoEngine(IMihomoControllerClient controller, IMihomoConfigurationValidator validator, IProxyPlatformPaths paths, IProxyDiagnosticLogStore? diagnostics = null) : IProxyEngine
{
    public const string Id = "mihomo";
    public string EngineId => Id;

    public Task<ProxyEngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProxyEngineCapabilities(true, true, true, true, true, true));

    public async Task<ProxyHealthDto> GetHealthAsync(CancellationToken cancellationToken)
    {
        var reachable = await controller.IsReachableAsync(cancellationToken);
        return reachable.Succeeded
            ? new(ProxyRuntimeState.Running, ProxyTunState.Disabled, ProxyHealthState.Healthy, true, true, true)
            : new(ProxyRuntimeState.Degraded, ProxyTunState.Disabled, ProxyHealthState.Degraded, false, false, false, reachable.ProblemCode);
    }

    public Task<string?> ValidateConfigurationAsync(string configurationPath, CancellationToken cancellationToken) => validator.ValidateAsync(configurationPath, cancellationToken);
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => controller.ReloadAsync(cancellationToken);
    public async Task<IReadOnlyList<ProxyGroupDto>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var result = await controller.GetGroupsAsync(cancellationToken);
        if (!result.Succeeded) return [];
        var configuredOrder = await MihomoProxyGroupOrder.ReadAsync(paths, cancellationToken);
        return result.Value!
            .OrderBy(group => configuredOrder.TryGetValue(group.Name, out var index) ? index : int.MaxValue)
            .ToArray();
    }
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => controller.SelectGroupAsync(groupName, proxyName, cancellationToken);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => controller.GetRoutingModeAsync(cancellationToken);
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => controller.SetRoutingModeAsync(mode, cancellationToken);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) =>
        controller.TestProxyDelayAsync(proxyName, url, timeoutMilliseconds, cancellationToken);
    public async Task<IReadOnlyList<ProxyConnectionDto>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var result = await controller.GetConnectionsAsync(cancellationToken);
        return result.Succeeded ? result.Value! : [];
    }
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => controller.GetTrafficAsync(cancellationToken);
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => controller.CloseConnectionAsync(connectionId, cancellationToken);
    public async Task<IReadOnlyList<ProxyLogEntryDto>> GetLogsAsync(int limit, CancellationToken cancellationToken)
    {
        var result = await controller.GetLogsAsync(limit, cancellationToken);
        var controllerLogs = result.Succeeded ? result.Value! : [];
        IReadOnlyList<ProxyLogEntryDto> diagnosticLogs = diagnostics is null ? [] : await diagnostics.ReadAsync(limit, cancellationToken);
        return controllerLogs.Concat(diagnosticLogs)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();
    }
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => controller.GetDnsStatusAsync(cancellationToken);
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
