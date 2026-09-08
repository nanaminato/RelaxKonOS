namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>Typed network boundary: no arbitrary routes, DNS servers, interfaces or commands cross it.</summary>
public interface IProxyNetworkSafetyPlatform
{
    Task<ProxyManagementRouteSnapshot?> CaptureManagementRouteAsync(CancellationToken cancellationToken);
    Task<bool> ApplyTunAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken);
    Task<bool> VerifyManagementRouteAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Protected host state only; none of these values are public API DTO fields.</summary>
public sealed record ProxyManagementRouteSnapshot(
    string SnapshotId,
    DateTimeOffset CapturedAt,
    bool ManagementPathSafe,
    string EgressInterface,
    string DefaultGateway,
    IReadOnlyList<string> SystemBypass);

/// <summary>Conservative default until the platform-specific route/DNS implementation is validated.</summary>
public sealed class UnavailableProxyNetworkSafetyPlatform : IProxyNetworkSafetyPlatform
{
    public Task<ProxyManagementRouteSnapshot?> CaptureManagementRouteAsync(CancellationToken cancellationToken) => Task.FromResult<ProxyManagementRouteSnapshot?>(null);
    public Task<bool> ApplyTunAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> VerifyManagementRouteAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RestoreAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
}
