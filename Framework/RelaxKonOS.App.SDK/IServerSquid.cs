using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.AppSDK;

public interface IServerSquid
{
    bool CanManage { get; }
    Task<SquidAccount[]> ReadAccountsAsync(CancellationToken cancellationToken);
    Task<SquidConfigurationSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<SquidConfigurationPreview> PreviewAsync(SquidConfiguration configuration, CancellationToken cancellationToken);
    Task<SquidNativeValidation> ValidateAsync(SquidConfiguration configuration, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> ApplyAsync(ApplySquidConfigurationRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> ChangeAccountAsync(SquidAccountMutation request, string idempotencyKey, CancellationToken cancellationToken);
    Task<ProxyOperationAcceptedDto> RunLifecycleAsync(SquidLifecycleRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken cancellationToken);
}
