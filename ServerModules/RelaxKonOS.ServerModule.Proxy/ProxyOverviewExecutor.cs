namespace RelaxKonOS.Server.Proxy;

public sealed class ProxyOverviewExecutor : IProxyOverviewExecutor
{
    public async Task<ProxyOverviewReadout> ReadAsync(ProxyOverviewReads reads, CancellationToken ct)
    {
        await reads.ReadActiveProfileAsync(ct);
        var recoveryRequired = await reads.ReadRecoveryRequiredAsync(ct);
        var reachable = await reads.ObserveRuntimeAsync(ct) ?? await reads.IsReachableAsync(ct);
        var tunEnabled = reachable && !recoveryRequired && await reads.ReadTunEnabledAsync(ct);
        var connections = reachable ? await reads.CountConnectionsAsync(ct) : 0;
        return new(tunEnabled, connections);
    }
}
