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
            ProxySubscriptionOperation.RefreshAll => ProxyOperationStore.RefreshBatchKind,
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
                ProxySubscriptionOperation.RefreshAll => RefreshBatchAsync(service, operationId, token),
                ProxySubscriptionOperation.Refresh => service.RefreshAsync(id, operationId, token),
                ProxySubscriptionOperation.Activate => service.ActivateAsync(id, token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }, ct);

    private async Task<string?> RefreshBatchAsync(IProxySubscriptionService service, Guid operationId, CancellationToken ct)
    {
        var subscriptions = await service.ListAsync(ct);
        var plan = await operations.PlanRefreshBatchAsync(operationId, subscriptions.Select(item => item.Id), ct);
        foreach (var step in plan)
        {
            await operations.StartRefreshStepAsync(operationId, step.OperationId);
            var problem = await service.RefreshAsync(step.SubscriptionId, step.OperationId, ct);
            if (problem == ProxyProblemCodes.RecoveryRequired) return problem;
            await operations.FinishRefreshStepAsync(operationId, step.OperationId, problem);
            if (!string.IsNullOrEmpty(problem)) return problem;
        }
        return null;
    }
}
