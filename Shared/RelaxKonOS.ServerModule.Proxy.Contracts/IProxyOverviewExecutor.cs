namespace RelaxKonOS.Server.Proxy;

public interface IProxyOverviewExecutor
{
    Task<ProxyOverviewReadout> ReadAsync(ProxyOverviewReads reads, CancellationToken ct);
}

public sealed record ProxyOverviewReads(
    Func<CancellationToken, Task> ReadActiveProfileAsync,
    Func<CancellationToken, Task<bool>> ReadRecoveryRequiredAsync,
    Func<CancellationToken, Task<bool?>> ObserveRuntimeAsync,
    Func<CancellationToken, Task<bool>> IsReachableAsync,
    Func<CancellationToken, Task<bool>> ReadTunEnabledAsync,
    Func<CancellationToken, Task<int>> CountConnectionsAsync);

public readonly record struct ProxyOverviewReadout(bool TunEnabled, int ActiveConnections);
