namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Owns actual result execution after the service method has returned shared DTOs or bytes.</summary>
internal sealed class SystemPerformanceResult(IResult result) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var request = context.RequestAborted;
        request.ThrowIfCancellationRequested();
        using var work = await context.RequestServices.GetRequiredService<ISystemPerformanceWorkLeaseSource>().AcquireAsync(request);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(request, work.Stopping);
        stopping.Token.ThrowIfCancellationRequested();
        // Result implementations consume RequestAborted while serializing/writing; preserve the caller's token afterwards.
        context.RequestAborted = stopping.Token;
        try
        {
            await result.ExecuteAsync(context);
            stopping.Token.ThrowIfCancellationRequested();
        }
        finally { context.RequestAborted = request; }
    }
}
