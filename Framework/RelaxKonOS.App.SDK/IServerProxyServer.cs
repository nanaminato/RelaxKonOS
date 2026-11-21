using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.AppSDK;

public interface IServerProxyServer
{
    Task<ProxyServerSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<ProxyServerStatus> StatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyServerRuntimeRelease>> ReleasesAsync(CancellationToken cancellationToken);
    Task<ProxyServerValidation> ValidateAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProxyServerExportFormatOption>> ExportFormatsAsync(Guid credentialId, CancellationToken cancellationToken);
    Task<ProxyServerExport> ExportAsync(Guid credentialId, string format, CancellationToken cancellationToken);
    Task<ProxyServerSubscriptionIssued> IssueSubscriptionAsync(IssueProxyServerSubscription request, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> SaveConfigurationAsync(SaveProxyServerConfiguration request, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> SavePrincipalAsync(SaveProxyServerPrincipal request, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> DeletePrincipalAsync(Guid id, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> SaveCredentialAsync(ProxyServerCredentialRequest request, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> RotateCredentialAsync(Guid id, ProxyServerCredentialRotation request, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> DeleteCredentialAsync(Guid id, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> ApplyAsync(string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> RollbackAsync(string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> RunLifecycleAsync(ProxyServerAction action, string key, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> RevokeSubscriptionAsync(Guid id, string key, CancellationToken cancellationToken);
    Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken cancellationToken);
}
