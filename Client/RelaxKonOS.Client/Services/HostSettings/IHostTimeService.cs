using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

/// <summary>A frozen connection identity: callers cannot carry a plan into another login or host.</summary>
public sealed record HostSettingsConnection(string ServerUrl, Guid SessionId, Guid UserId);

public interface IHostTimeService
{
    HostSettingsConnection CaptureConnection();
    bool IsCurrent(HostSettingsConnection connection);
    Task<SettingsCatalogSnapshot> CatalogAsync(HostSettingsConnection connection, CancellationToken ct = default);
    Task<HostTimeSnapshot> ReadAsync(HostSettingsConnection connection, CancellationToken ct = default);
    Task<SettingsPlan> PreviewAsync(HostSettingsConnection connection, TimeZonePreviewRequest request, CancellationToken ct = default);
    Task<SettingsOperation> ApplyAsync(HostSettingsConnection connection, Guid planId, CancellationToken ct = default);
    Task<SettingsOperation> GetOperationAsync(HostSettingsConnection connection, Guid id, CancellationToken ct = default);
    Task<SettingsOperation> RollbackAsync(HostSettingsConnection connection, Guid id, string revision, CancellationToken ct = default);
    Task<HostElevationResult> AuthorizeAsync(HostSettingsConnection connection, string? password = null,
        string? administratorUsername = null, CancellationToken ct = default);
}
