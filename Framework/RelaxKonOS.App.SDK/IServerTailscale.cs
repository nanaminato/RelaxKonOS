using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.AppSDK;

public interface IServerTailscale
{
    event EventHandler? SessionChanged;
    HostSettingsConnection CaptureConnection();
    bool IsCurrent(HostSettingsConnection connection);
    Task<TailscaleStatusDto> GetStatusAsync(HostSettingsConnection connection, CancellationToken ct);
    Task<TailscaleControlSnapshot> GetControlAsync(HostSettingsConnection connection, CancellationToken ct);
    Task<TailscaleOperation> PrepareAsync(HostSettingsConnection connection, TailscalePrepareRequest request, CancellationToken ct);
    Task<TailscaleOperation> ExecuteAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct);
    Task<TailscaleOperation> GetOperationAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct);
    Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct);
    Task<TailscaleOperation> CloseAsync(HostSettingsConnection connection, TailscaleCloseRequest request, CancellationToken ct);
}

public interface IExternalTailscale : IServerTailscale
{
    ITailscaleOperationReferences OperationReferences { get; }
    ITailscaleOperationReferences LoginReferences { get; }
    Task<bool> RequestPeerAccessAsync(HostSettingsConnection connection, CancellationToken cancellationToken);
    Task<TailscaleControlSnapshot> ReadAuthorizedControlAsync(HostSettingsConnection connection, CancellationToken cancellationToken);
}
