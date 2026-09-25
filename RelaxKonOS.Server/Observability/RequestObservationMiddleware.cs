using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

public sealed class RequestObservationMiddleware(RequestDelegate next)
{
    public const string CorrelationHeader = "X-RelaxKonOS-Correlation-Id";
    private static readonly ActivitySource ActivitySource = new("RelaxKonOS.Server");

    public async Task InvokeAsync(HttpContext context, ICorrelationContextAccessor accessor, IEventLogger events,
        IObservabilitySanitizer sanitizer, ObservabilityOptions options)
    {
        var correlation = ReadOrCreate(context.Request.Headers[CorrelationHeader]);
        context.Response.Headers[CorrelationHeader] = correlation.CorrelationId.ToString("D");
        using var activity = ActivitySource.StartActivity("http.request", ActivityKind.Server);
        using var current = accessor.Push(correlation);
        using var scope = context.RequestServices.GetRequiredService<ILogger<RequestObservationMiddleware>>().BeginScope(new Dictionary<string, object?>
        {
            ["correlationId"] = correlation.CorrelationId, ["traceId"] = activity?.TraceId.ToString(), ["spanId"] = activity?.SpanId.ToString()
        });
        var timer = Stopwatch.StartNew();
        events.Write(new(ObservabilityEventCatalog.RequestStarted, ObservabilitySeverity.Debug, ObservabilityOutcome.Started, "server", "Request processing started."));
        try { await next(context); }
        catch (Exception exception)
        {
            events.Write(new(ObservabilityEventCatalog.RequestUnhandledException, ObservabilitySeverity.Error, ObservabilityOutcome.Failed,
                "server", "Unhandled request exception.", "request.unhandled_exception", DurationMs: timer.ElapsedMilliseconds,
                Exception: sanitizer.SanitizeException(exception)));
            throw;
        }
        finally
        {
            var status = context.Response.StatusCode;
            var important = status >= 500 || timer.ElapsedMilliseconds >= options.SlowRequestThresholdMs || status == StatusCodes.Status401Unauthorized || status == StatusCodes.Status403Forbidden;
            if (important || Random.Shared.NextDouble() < options.SuccessfulRequestSampleRate)
            {
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                var severity = status >= 500 ? ObservabilitySeverity.Error : status >= 400 ? ObservabilitySeverity.Warning : ObservabilitySeverity.Information;
                var outcome = status >= 400 ? ObservabilityOutcome.Failed : ObservabilityOutcome.Succeeded;
                events.Write(new(ObservabilityEventCatalog.RequestCompleted, severity, outcome, "server", "Request processing completed.",
                    status >= 400 ? $"http.{status}" : null, ResourceType: "route", ResourceReference: route, DurationMs: timer.ElapsedMilliseconds));
            }
        }
    }

    private static CorrelationContext ReadOrCreate(string? candidate)
        => Guid.TryParse(candidate, out var id) && id != Guid.Empty ? new CorrelationContext(id) : CorrelationContext.Create();
}
