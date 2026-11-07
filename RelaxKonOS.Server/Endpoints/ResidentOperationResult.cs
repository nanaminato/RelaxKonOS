using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Endpoints;

internal sealed class ResidentOperationResult(string moduleId, IResult result) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var request = context.RequestAborted;
        using var lease = await context.RequestServices.GetRequiredService<IResidentModuleWorkLeaseSource>()
            .AcquireAsync(moduleId, request);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(request, lease.Stopping);
        stopping.Token.ThrowIfCancellationRequested();
        context.RequestAborted = stopping.Token;
        try { await result.ExecuteAsync(context); }
        finally { context.RequestAborted = request; }
    }
}
