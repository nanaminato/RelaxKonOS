using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

/// <summary>UI-independent service. No automatic write retry, cached authorization, or connection retargeting.</summary>
public sealed class HostTimeService(HttpClient http, IAuthSession session) : HostSettingsService(http, session), IHostTimeService
{
    public Task<SettingsCatalogSnapshot> CatalogAsync(HostSettingsConnection connection, CancellationToken ct = default)
        => SendAsync<SettingsCatalogSnapshot>(connection, HttpMethod.Get, SettingsApiRoutes.Catalog, null, ct);
    public Task<HostTimeSnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default)
        => SendAsync<HostTimeSnapshot>(connection, HttpMethod.Get, SettingsApiRoutes.Time, null, ct);
    public Task<SettingsPlan> PreviewAsync(HostSettingsConnection connection, TimeZonePreviewRequest request, CancellationToken ct = default)
        => SendAsync<SettingsPlan>(connection, HttpMethod.Post, SettingsApiRoutes.TimePreview, request, ct);
    public Task<SettingsOperation> ApplyAsync(HostSettingsConnection connection, Guid planId, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Post, SettingsApiRoutes.TimeApply, new SettingsApplyRequest(planId), ct);
    public Task<SettingsOperation> GetOperationAsync(HostSettingsConnection connection, Guid id, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Get, SettingsApiRoutes.Operation.Replace("{id}", id.ToString("D")), null, ct);
    public Task<SettingsOperation> RollbackAsync(HostSettingsConnection connection, Guid id, string revision, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Post, SettingsApiRoutes.Rollback.Replace("{id}", id.ToString("D")), new SettingsRollbackRequest(revision), ct);
    public Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null,
        string? administratorUsername = null, CancellationToken ct = default)
        => SendAsync<HostElevationResult>(connection, HttpMethod.Post, PrivilegedApiRoutes.Elevation,
            new HostElevationRequest(HostElevationCapability.HostTimeChange, "host/time", password, administratorUsername), ct);

}
