using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Endpoints;

internal static class ResidentScopedResponse
{
    internal static async Task ExecuteAsync(HttpContext context, IResidentModuleWorkLeaseSource leases, string moduleId,
        Func<IServiceProvider, CancellationToken, Task<IResult>> execute, Func<Exception, bool>? cleanupUnconfirmed = null)
    {
        var request = context.RequestAborted;
        var lease = await leases.AcquireAsync(moduleId, request);
        var recoveryRequired = false;
        try
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(request, lease.Stopping);
            stopping.Token.ThrowIfCancellationRequested();
            var scope = context.RequestServices.CreateAsyncScope();
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
