using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

public interface IHostNetworkService
{
    HostSettingsConnection CaptureConnection();
    bool IsCurrent(HostSettingsConnection connection);
    Task<HostNetworkSnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default);
    Task<HostNetworkApplyResult> ApplyAsync(HostSettingsConnection connection, HostNetworkApplyRequest request, CancellationToken ct = default);
    Task<HostNetworkConfirmed> ConfirmAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct = default);
    Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null, string? administratorUsername = null, CancellationToken ct = default);
}

public sealed class HostNetworkService(HttpClient http, IAuthSession session) : HostSettingsService(http, session), IHostNetworkService
{
    public Task<HostNetworkSnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default)
        => SendAsync<HostNetworkSnapshot>(connection, HttpMethod.Get, SettingsApiRoutes.Network, null, ct);
    public Task<HostNetworkApplyResult> ApplyAsync(HostSettingsConnection connection, HostNetworkApplyRequest request, CancellationToken ct = default)
        => SendAsync<HostNetworkApplyResult>(connection, HttpMethod.Post, SettingsApiRoutes.NetworkApply, request, ct);
    public Task<HostNetworkConfirmed> ConfirmAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct = default)
        => SendAsync<HostNetworkConfirmed>(connection, HttpMethod.Post, SettingsApiRoutes.NetworkConfirm, new HostNetworkConfirmRequest(operationId), ct);
    public Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null, string? administratorUsername = null, CancellationToken ct = default)
        => SendAsync<HostElevationResult>(connection, HttpMethod.Post, PrivilegedApiRoutes.Elevation,
            new HostElevationRequest(HostElevationCapability.HostNetworkChange, "host/network", password, administratorUsername), ct);
}
