using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

public sealed record ObservabilityEvent(ObservabilityEventCatalog.Definition Definition, ObservabilitySeverity Severity,
    ObservabilityOutcome Outcome, string Component, string Message, string? ProblemCode = null, string? Action = null,
    Guid? OperationId = null, string? ResourceType = null, string? ResourceReference = null, long? DurationMs = null,
    SanitizedException? Exception = null);

public interface IEventLogger { void Write(ObservabilityEvent entry); }

public sealed class EventLogger(ILogger<EventLogger> logger, ICorrelationContextAccessor correlation, IObservabilitySanitizer sanitizer,
    IRuntimeLogSink sink) : IEventLogger
{
    public void Write(ObservabilityEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var context = correlation.Current ?? CorrelationContext.Create(entry.OperationId, entry.Action);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventId"] = entry.Definition.Id, ["eventName"] = entry.Definition.Name, ["component"] = entry.Component,
            ["correlationId"] = context.CorrelationId, ["operationId"] = entry.OperationId ?? context.OperationId,
            ["action"] = entry.Action ?? context.Action, ["outcome"] = entry.Outcome.ToString().ToLowerInvariant(),
            ["problemCode"] = entry.ProblemCode, ["resourceType"] = entry.ResourceType,
            ["resourceReference"] = entry.ResourceReference is null ? null : sanitizer.ToReference(entry.ResourceReference),
            ["durationMs"] = entry.DurationMs, ["exceptionType"] = entry.Exception?.Type
        });
        sink.Write(entry, context);
        logger.Log(ToLogLevel(entry.Severity), new EventId(entry.Definition.Id, entry.Definition.Name), "{Message}", sanitizer.SanitizeSummary(entry.Message));
    }

    private static LogLevel ToLogLevel(ObservabilitySeverity severity) => severity switch
    {
        ObservabilitySeverity.Trace => LogLevel.Trace, ObservabilitySeverity.Debug => LogLevel.Debug,
        ObservabilitySeverity.Information => LogLevel.Information, ObservabilitySeverity.Warning => LogLevel.Warning,
        ObservabilitySeverity.Error => LogLevel.Error, _ => LogLevel.Critical
    };
}
