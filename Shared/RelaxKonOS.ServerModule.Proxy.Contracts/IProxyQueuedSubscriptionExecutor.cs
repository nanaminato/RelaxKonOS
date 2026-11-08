namespace RelaxKonOS.Server.Proxy;

public enum ProxySubscriptionOperation { RefreshAll, Refresh, Activate }

public interface IProxyQueuedSubscriptionExecutor
{
    Task<string?> ExecuteAsync(ProxySubscriptionOperation operation, Guid subscriptionId, Guid operationId,
        CancellationToken cancellationToken);
}

public sealed record ProxySubscriptionTarget(Guid ProfileId, bool Active,
    Func<IProxySubscriptionNormalizer, CancellationToken, Task<string>> FetchAsync);

public readonly record struct ProxySubscriptionRefresh(Guid SubscriptionId, Guid OperationId, DateTimeOffset RetrievedAt);
public readonly record struct ProxySubscriptionStep(Guid SubscriptionId, Guid OperationId);

public interface IProxySubscriptionActions
{
    Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct);
    Task<ProxySubscriptionTarget?> FindAsync(Guid subscriptionId, CancellationToken ct);
    Task<string?> StoreAsync(Guid profileId, string content, ProxySubscriptionRefresh? refresh, CancellationToken ct);
    Task<string?> ActivateAsync(Guid profileId, Guid subscriptionId, Guid operationId, CancellationToken ct);
    Task<string?> PublishRefreshAsync(Guid profileId, string content, ProxySubscriptionRefresh refresh, CancellationToken ct);
}

public interface IProxySubscriptionBatch
{
    Task<IReadOnlyList<ProxySubscriptionStep>> PlanAsync(Guid operationId, CancellationToken ct);
    Task StartAsync(Guid operationId, Guid stepId);
    Task FinishAsync(Guid operationId, Guid stepId, string? problem);
    bool RequiresRecovery(string? problem);
    string InvalidSubscription { get; }
}
