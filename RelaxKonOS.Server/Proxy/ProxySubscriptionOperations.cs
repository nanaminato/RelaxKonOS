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
        return operations.EnqueueAsync(key, kind, (_, token) => ExecuteAsync(operation, subscriptionId, actor, kind, token), ct);
    }

    private async Task<string?> ExecuteAsync(ProxySubscriptionOperation operation, Guid id, string actor, string kind, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        Exception? executionFailure = null;
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<IProxySubscriptionService>();
            var audit = scope.ServiceProvider.GetRequiredService<ProxyAuditStore>();
            string? problem;
            try
            {
                problem = operation switch
                {
                    ProxySubscriptionOperation.RefreshAll => await service.RefreshAllAsync(ct),
                    ProxySubscriptionOperation.Refresh => await service.RefreshAsync(id, ct),
                    ProxySubscriptionOperation.Activate => await service.ActivateAsync(id, ct),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
            }
            catch
            {
                await audit.RecordAsync(actor, kind, "failed", ProxyProblemCodes.OperationInterrupted, ct);
                throw;
            }
            await audit.RecordAsync(actor, kind, string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return problem;
        }
        catch (Exception error) { executionFailure = error; throw; }
        finally
        {
            try { await scope.DisposeAsync(); }
            catch (Exception cleanup)
            {
                // Retain the failed scope; disposal may have only partially released its resources.
                throw new ProxySubscriptionCleanupException(scope,
                    executionFailure is null ? cleanup : new AggregateException(executionFailure, cleanup));
            }
        }
    }
}

internal sealed class ProxySubscriptionCleanupException(IAsyncDisposable resource, Exception inner)
    : Exception("Subscription scope cleanup requires recovery.", inner)
{
    internal IAsyncDisposable Resource { get; } = resource;
}
