using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy;

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
        var scopeFactory = scopes;
        var ledger = operations;
        return ledger.EnqueueIdentifiedAsync(key, kind, (operationId, _, holdExecution, token) =>
            ProxyOperationScope.RunAsync(scopeFactory, actor, kind, async (services, stopping) =>
            {
                var actions = services.GetRequiredService<IProxySubscriptionActions>();
                var code = ProxySubscriptionContentNormalizer.Acquire();
                holdExecution(code);
                var executor = code.Create<IProxyQueuedSubscriptionExecutor>(
                    "RelaxKonOS.Server.Proxy.ProxyQueuedSubscriptionExecutor", actions, new Batch(ledger, actions));
                return await RunAsync(executor, operation, subscriptionId, operationId, stopping);
            }, token), ct);
    }

    private static async Task<string?> RunAsync(IProxyQueuedSubscriptionExecutor executor, ProxySubscriptionOperation operation,
        Guid subscriptionId, Guid operationId, CancellationToken ct)
    {
        try { return await executor.ExecuteAsync(operation, subscriptionId, operationId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (ProxySubscriptionException error) { throw new ProxySubscriptionException(error.ProblemCode); }
        catch (Exception) { throw new InvalidOperationException("Proxy subscription execution failed."); }
    }

    private sealed class Batch(ProxyOperationStore operations, IProxySubscriptionActions actions) : IProxySubscriptionBatch
    {
        public string InvalidSubscription => ProxyProblemCodes.SubscriptionInvalid;
        public bool RequiresRecovery(string? problem) => problem == ProxyProblemCodes.RecoveryRequired;

        public async Task<IReadOnlyList<ProxySubscriptionStep>> PlanAsync(Guid operationId, CancellationToken ct)
        {
            var ids = await actions.ListIdsAsync(ct);
            var plan = await operations.PlanRefreshBatchAsync(operationId, ids, ct);
            return plan.Select(step => new ProxySubscriptionStep(step.SubscriptionId, step.OperationId)).ToArray();
        }

        public Task StartAsync(Guid operationId, Guid stepId) => operations.StartRefreshStepAsync(operationId, stepId);

        public Task FinishAsync(Guid operationId, Guid stepId, string? problem) =>
            operations.FinishRefreshStepAsync(operationId, stepId, problem);
    }
}
