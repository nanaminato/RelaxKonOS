using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Services.HostSettings;

/// <summary>A frozen connection identity: callers cannot carry a plan into another login or host.
/// <paramref name="ServiceId"/> is the stable login identity; the transport address is read from the
/// session at call time so a verified tunnel rebind does not invalidate the connection.</summary>
public sealed record HostSettingsConnection(string ServiceId, Guid SessionId, Guid UserId);

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
