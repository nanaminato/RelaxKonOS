using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy;

internal static class ProxyOperationScope
{
    internal static async Task<string?> RunAsync(IServiceScopeFactory scopes, string actor, string kind,
        Func<IServiceProvider, CancellationToken, Task<string?>> execute, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        Exception? executionFailure = null;
        try
        {
            var audit = scope.ServiceProvider.GetRequiredService<ProxyAuditStore>();
            string? problem;
            try
            {
                problem = await execute(scope.ServiceProvider, ct);
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
                throw new ProxyOperationCleanupException(scope,
                    executionFailure is null ? cleanup : new AggregateException(executionFailure, cleanup));
            }
        }
    }
}

internal sealed class ProxyOperationCleanupException(IAsyncDisposable resource, Exception inner)
    : Exception("Proxy execution scope cleanup requires recovery.", inner)
{
    internal IAsyncDisposable Resource { get; } = resource;
}
