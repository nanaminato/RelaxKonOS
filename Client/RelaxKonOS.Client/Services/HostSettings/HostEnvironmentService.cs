using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

/// <summary>Independent environment API. Revealed values are returned only to the explicit caller and never cached.</summary>
public sealed class HostEnvironmentService(HttpClient http, IAuthSession session)
    : HostSettingsService(http, session), IHostEnvironmentService
{
    public Task<SettingsTarget> ResolveTargetAsync(HostSettingsConnection connection, SettingsScope scope, CancellationToken ct = default)
        => SendAsync<SettingsTarget>(connection, HttpMethod.Get, SettingsApiRoutes.EnvironmentTarget + "?scope=" + ScopeName(scope), null, ct);

    public Task<HostEnvironmentSnapshot> ReadAsync(HostSettingsConnection connection, SettingsScope scope, bool reveal = false, CancellationToken ct = default)
        => SendAsync<HostEnvironmentSnapshot>(connection, HttpMethod.Get,
            SettingsApiRoutes.Environment + "?scope=" + ScopeName(scope) + "&reveal=" + (reveal ? "true" : "false"), null, ct);

    public Task<SettingsPlan> PreviewAsync(HostSettingsConnection connection, EnvironmentPreviewRequest request, CancellationToken ct = default)
    {
        _ = ScopeName(request.Scope);
        return SendAsync<SettingsPlan>(connection, HttpMethod.Post, SettingsApiRoutes.EnvironmentPreview, request, ct);
    }

    public Task<SettingsOperation> ApplyAsync(HostSettingsConnection connection, Guid planId, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Post, SettingsApiRoutes.EnvironmentApply, new SettingsApplyRequest(planId), ct);

    public Task<SettingsOperation> GetOperationAsync(HostSettingsConnection connection, Guid id, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Get, SettingsApiRoutes.Operation.Replace("{id}", id.ToString("D")), null, ct);

    public Task<SettingsOperation> RollbackAsync(HostSettingsConnection connection, Guid id, string revision, CancellationToken ct = default)
        => SendAsync<SettingsOperation>(connection, HttpMethod.Post, SettingsApiRoutes.Rollback.Replace("{id}", id.ToString("D")), new SettingsRollbackRequest(revision), ct);

    public async Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, SettingsScope scope,
        HostElevationCapability capability, string? password = null, string? administratorUsername = null, CancellationToken ct = default)
    {
        if (capability is not (HostElevationCapability.HostEnvironmentRead or HostElevationCapability.HostEnvironmentReveal or HostElevationCapability.HostEnvironmentChange))
            throw new ArgumentOutOfRangeException(nameof(capability));
        // Resolve the authenticated account on the server; never derive a remote UID/SID from the client OS.
        var target = await ResolveTargetAsync(connection, scope, ct);
        if (target.Scope != scope) throw new InvalidOperationException("settings.environment.target_mismatch");
        return await SendAsync<HostElevationResult>(connection, HttpMethod.Post, PrivilegedApiRoutes.Elevation,
            new HostElevationRequest(capability, target.ResourceId, password, administratorUsername), ct);
    }

    private static string ScopeName(SettingsScope scope) => scope switch
    {
        SettingsScope.HostUser => "hostUser",
        SettingsScope.HostMachine => "hostMachine",
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };
}
