using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

/// <summary>Creates one child activity/scope per SignalR invocation without logging hub arguments.</summary>
public sealed class ObservationHubFilter(ICorrelationContextAccessor accessor, IEventLogger events) : IHubFilter
{
    private static readonly ActivitySource ActivitySource = new("RelaxKonOS.Server.SignalR");
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var parent = accessor.Current ?? ReadOrCreate(invocationContext.Context.GetHttpContext());
        using var activity = ActivitySource.StartActivity("signalr.invocation", ActivityKind.Internal);
        using var scope = accessor.Push(parent);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await next(invocationContext);
            events.Write(new(ObservabilityEventCatalog.RequestCompleted, ObservabilitySeverity.Debug, ObservabilityOutcome.Succeeded,
                "server", "SignalR invocation completed.", DurationMs: timer.ElapsedMilliseconds));
            return result;
        }
        catch
        {
            events.Write(new(ObservabilityEventCatalog.RequestUnhandledException, ObservabilitySeverity.Error, ObservabilityOutcome.Failed,
                "server", "SignalR invocation failed.", "signalr.invocation_failed", DurationMs: timer.ElapsedMilliseconds));
            throw;
        }
    }
    private static CorrelationContext ReadOrCreate(HttpContext? context)
        => context is not null && Guid.TryParse(context.Request.Headers[RequestObservationMiddleware.CorrelationHeader], out var id) && id != Guid.Empty
            ? new CorrelationContext(id) : CorrelationContext.Create();
}
