using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Endpoints;

internal static class ResidentScopedResponse
{
    internal static Task ExecuteAsync(HttpContext context, IResidentModuleWorkLeaseSource leases, string moduleId,
        Func<IServiceProvider, CancellationToken, Task<IResult>> execute, Func<Exception, bool>? cleanupUnconfirmed = null) =>
        ExecuteAsync(context, ct => leases.AcquireAsync(moduleId, ct), execute, cleanupUnconfirmed);

    internal static async Task ExecuteAsync(HttpContext context, Func<CancellationToken, ValueTask<IModuleWorkLease>> admit,
        Func<IServiceProvider, CancellationToken, Task<IResult>> execute, Func<Exception, bool>? cleanupUnconfirmed = null,
        IServiceScopeFactory? scopeFactory = null, Action<AsyncServiceScope>? retainFailedScope = null)
        => await ExecuteAsync(context, await admit(context.RequestAborted), (services, _, ct) => execute(services, ct),
            cleanupUnconfirmed, scopeFactory, retainFailedScope is null ? null : (_, scope) => retainFailedScope(scope));

    internal static Task ExecuteDeferredAsync(HttpContext context,
        Func<IServiceProvider, Action<IModuleWorkLease>, CancellationToken, Task<IResult>> execute,
        Action<IModuleWorkLease, AsyncServiceScope> retainFailedScope) =>
        ExecuteAsync(context, null, execute, null, null, retainFailedScope);

    private static async Task ExecuteAsync(HttpContext context, IModuleWorkLease? lease,
        Func<IServiceProvider, Action<IModuleWorkLease>, CancellationToken, Task<IResult>> execute,
        Func<Exception, bool>? cleanupUnconfirmed, IServiceScopeFactory? scopeFactory,
        Action<IModuleWorkLease, AsyncServiceScope>? retainFailedScope)
    {
        var request = context.RequestAborted;
        var recoveryRequired = false;
        try
        {
            using var stopping = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(request, lease.Stopping);
            var cancellation = stopping?.Token ?? request;
            cancellation.ThrowIfCancellationRequested();
            var scope = scopeFactory is null ? context.RequestServices.CreateAsyncScope() : scopeFactory.CreateAsyncScope();
            context.RequestAborted = cancellation;
            try
            {
                var result = await execute(scope.ServiceProvider, AcceptLease, cancellation);
                cancellation.ThrowIfCancellationRequested();
                await result.ExecuteAsync(context);
                await context.Response.CompleteAsync();
            }
            catch (Exception error) when (cleanupUnconfirmed?.Invoke(error) == true)
            {
                RetainForRecovery();
                throw;
            }
            finally
            {
                context.RequestAborted = request;
                try { await scope.DisposeAsync(); }
                catch
                {
                    RetainForRecovery();
                    if (lease is not null) retainFailedScope?.Invoke(lease, scope);
                    throw;
                }
            }
        }
        finally { if (!recoveryRequired) lease?.Dispose(); }

        void AcceptLease(IModuleWorkLease work)
        {
            if (lease is not null) throw new InvalidOperationException("The response already owns a work lease.");
            lease = work;
        }

        void RetainForRecovery()
        {
            if (recoveryRequired || lease is null) return;
            recoveryRequired = true;
            lease.RequireRecovery();
        }
    }
}
