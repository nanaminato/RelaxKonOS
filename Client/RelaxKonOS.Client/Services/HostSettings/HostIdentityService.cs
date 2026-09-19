using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

/// <summary>UI-independent service. No automatic write retry, cached authorization, or connection retargeting.</summary>
public sealed class HostIdentityService(HttpClient http, IAuthSession session) : HostSettingsService(http, session), IHostIdentityService
{
    public Task<HostIdentitySnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default)
        => SendAsync<HostIdentitySnapshot>(connection, HttpMethod.Get, SettingsApiRoutes.Identity, null, ct);
    public Task<SettingsPlan> PreviewAsync(HostSettingsConnection connection, HostnamePreviewRequest request, CancellationToken ct = default)
        => SendAsync<SettingsPlan>(connection, HttpMethod.Post, SettingsApiRoutes.IdentityPreview, request, ct);
    public Task<SettingsOperation> ApplyAsync(HostSettingsConnection connection, Guid planId, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Post, SettingsApiRoutes.IdentityApply, new SettingsApplyRequest(planId), ct);
    public Task<SettingsOperation> GetOperationAsync(HostSettingsConnection connection, Guid id, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Get, SettingsApiRoutes.Operation.Replace("{id}", id.ToString("D")), null, ct);
    public Task<SettingsOperation> RollbackAsync(HostSettingsConnection connection, Guid id, string revision, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Post, SettingsApiRoutes.Rollback.Replace("{id}", id.ToString("D")), new SettingsRollbackRequest(revision), ct);
    public Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null,
        string? administratorUsername = null, CancellationToken ct = default)
        => SendAsync<HostElevationResult>(connection, HttpMethod.Post, PrivilegedApiRoutes.Elevation,
            new HostElevationRequest(HostElevationCapability.HostIdentityChange, "host/identity", password, administratorUsername), ct);
}
