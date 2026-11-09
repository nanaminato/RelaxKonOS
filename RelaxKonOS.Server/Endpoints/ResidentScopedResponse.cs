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
    {
        var request = context.RequestAborted;
        var lease = await admit(request);
        var recoveryRequired = false;
        try
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(request, lease.Stopping);
            stopping.Token.ThrowIfCancellationRequested();
            var scope = scopeFactory is null ? context.RequestServices.CreateAsyncScope() : scopeFactory.CreateAsyncScope();
            context.RequestAborted = stopping.Token;
            try
            {
                var result = await execute(scope.ServiceProvider, stopping.Token);
                stopping.Token.ThrowIfCancellationRequested();
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
                    retainFailedScope?.Invoke(scope);
                    throw;
                }
            }
        }
        finally { if (!recoveryRequired) lease.Dispose(); }

        void RetainForRecovery()
        {
            if (recoveryRequired) return;
            recoveryRequired = true;
            lease.RequireRecovery();
        }
    }
}
