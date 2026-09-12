using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

public interface IHostEnvironmentService
{
    HostSettingsConnection CaptureConnection();
    bool IsCurrent(HostSettingsConnection connection);
    Task<SettingsTarget> ResolveTargetAsync(HostSettingsConnection connection, SettingsScope scope, CancellationToken ct = default);
    Task<HostEnvironmentSnapshot> ReadAsync(HostSettingsConnection connection, SettingsScope scope, bool reveal = false, CancellationToken ct = default);
    Task<SettingsPlan> PreviewAsync(HostSettingsConnection connection, EnvironmentPreviewRequest request, CancellationToken ct = default);
    Task<SettingsOperation> ApplyAsync(HostSettingsConnection connection, Guid planId, CancellationToken ct = default);
    Task<SettingsOperation> GetOperationAsync(HostSettingsConnection connection, Guid id, CancellationToken ct = default);
    Task<SettingsOperation> RollbackAsync(HostSettingsConnection connection, Guid id, string revision, CancellationToken ct = default);
    Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, SettingsScope scope,
        HostElevationCapability capability, string? password = null, string? administratorUsername = null, CancellationToken ct = default);
}
