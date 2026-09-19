using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

public interface IHostIdentityService
{
    HostSettingsConnection CaptureConnection();
    bool IsCurrent(HostSettingsConnection connection);
    Task<HostIdentitySnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default);
    Task<SettingsPlan> PreviewAsync(HostSettingsConnection connection, HostnamePreviewRequest request, CancellationToken ct = default);
    Task<SettingsOperation> ApplyAsync(HostSettingsConnection connection, Guid planId, CancellationToken ct = default);
    Task<SettingsOperation> GetOperationAsync(HostSettingsConnection connection, Guid id, CancellationToken ct = default);
    Task<SettingsOperation> RollbackAsync(HostSettingsConnection connection, Guid id, string revision, CancellationToken ct = default);
    Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null,
        string? administratorUsername = null, CancellationToken ct = default);
}
