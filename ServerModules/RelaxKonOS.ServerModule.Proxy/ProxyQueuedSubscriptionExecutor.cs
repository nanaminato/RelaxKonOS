namespace RelaxKonOS.Server.Proxy;

public sealed class ProxyQueuedSubscriptionExecutor(IProxySubscriptionActions actions, IProxySubscriptionBatch batch)
    : IProxyQueuedSubscriptionExecutor
{
    private readonly ProxySubscriptionNormalizer normalizer = new();

    public Task<string?> ExecuteAsync(ProxySubscriptionOperation operation, Guid subscriptionId, Guid operationId, CancellationToken ct) =>
        operation switch
        {
            ProxySubscriptionOperation.RefreshAll => RefreshBatchAsync(operationId, ct),
            ProxySubscriptionOperation.Refresh => RefreshAsync(subscriptionId, operationId, ct),
            ProxySubscriptionOperation.Activate => ActivateAsync(subscriptionId, operationId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private async Task<string?> RefreshAsync(Guid subscriptionId, Guid operationId, CancellationToken ct)
    {
        var target = await actions.FindAsync(subscriptionId, ct);
        if (target is null) return batch.InvalidSubscription;
        var content = await target.FetchAsync(normalizer, ct);
        var refresh = new ProxySubscriptionRefresh(subscriptionId, operationId, DateTimeOffset.UtcNow);
        if (!target.Active)
            return await actions.StoreAsync(target.ProfileId, content, refresh, ct);
        var problem = await actions.StoreAsync(target.ProfileId, content, null, ct);
        return string.IsNullOrEmpty(problem) ? await actions.PublishRefreshAsync(target.ProfileId, content, refresh, ct) : problem;
    }

    private async Task<string?> ActivateAsync(Guid subscriptionId, Guid operationId, CancellationToken ct)
    {
        var target = await actions.FindAsync(subscriptionId, ct);
        if (target is null) return batch.InvalidSubscription;
        return await actions.ActivateAsync(target.ProfileId, subscriptionId, operationId, ct);
    }

    private async Task<string?> RefreshBatchAsync(Guid operationId, CancellationToken ct)
    {
        foreach (var step in await batch.PlanAsync(operationId, ct))
        {
            await batch.StartAsync(operationId, step.OperationId);
            var problem = await RefreshAsync(step.SubscriptionId, step.OperationId, ct);
            if (batch.RequiresRecovery(problem)) return problem;
            await batch.FinishAsync(operationId, step.OperationId, problem);
            if (!string.IsNullOrEmpty(problem)) return problem;
        }
        return null;
    }
}
