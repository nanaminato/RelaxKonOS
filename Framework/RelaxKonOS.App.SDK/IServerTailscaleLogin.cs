using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.AppSDK;

public interface IServerTailscaleLogin
{
    event EventHandler? SessionChanged;
    HostSettingsConnection CaptureConnection();
    bool IsCurrent(HostSettingsConnection connection);
    Task<TailscaleLoginOperation> PrepareAsync(HostSettingsConnection connection, Guid id, CancellationToken ct);
    Task<TailscaleLoginOperation> ExecuteAsync(HostSettingsConnection connection, Guid id, CancellationToken ct);
    Task<TailscaleLoginOperation> GetAsync(HostSettingsConnection connection, Guid id, CancellationToken ct);
    Task<TailscaleLoginChallengeResponse> ChallengeAsync(HostSettingsConnection connection, Guid id, CancellationToken ct);
    Task<TailscaleLoginRecoverySnapshot> RecoveryAsync(HostSettingsConnection connection, Guid id, CancellationToken ct);
    Task<TailscaleLoginOperation> CloseAsync(HostSettingsConnection connection, TailscaleLoginCloseRequest request, CancellationToken ct);
}
