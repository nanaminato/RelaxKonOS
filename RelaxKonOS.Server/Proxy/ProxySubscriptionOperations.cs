using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy;

public enum ProxySubscriptionOperation { RefreshAll, Refresh, Activate }

/// <summary>Queues values only; accepted work owns a scope independent of the HTTP request.</summary>
public sealed class ProxySubscriptionOperations(ProxyOperationStore operations, IServiceScopeFactory scopes)
{
    public Task<ProxyOperationDto> EnqueueAsync(string key,
        ProxySubscriptionOperation operation, Guid subscriptionId, string actor, CancellationToken ct)
    {
        var kind = operation switch
        {
            ProxySubscriptionOperation.RefreshAll => "subscription.refresh_all",
            ProxySubscriptionOperation.Refresh => "subscription.refresh",
            ProxySubscriptionOperation.Activate => "subscription.activate",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return operations.EnqueueIdentifiedAsync(key, kind, (operationId, _, token) =>
            ExecuteAsync(operation, subscriptionId, operationId, actor, kind, token), ct);
    }

    private Task<string?> ExecuteAsync(ProxySubscriptionOperation operation, Guid id, Guid operationId, string actor, string kind, CancellationToken ct) =>
        ProxyOperationScope.RunAsync(scopes, actor, kind, (services, token) =>
        {
            var service = services.GetRequiredService<IProxySubscriptionService>();
            return operation switch
            {
                ProxySubscriptionOperation.RefreshAll => service.RefreshAllAsync(token),
                ProxySubscriptionOperation.Refresh => service.RefreshAsync(id, operationId, token),
                ProxySubscriptionOperation.Activate => service.ActivateAsync(id, token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }, ct);
}
