using System.Text.Json;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

/// <summary>Small local JSON sink for the closed event contract; failure is intentionally best effort.</summary>
public interface IRuntimeLogSink { void Write(ObservabilityEvent entry, CorrelationContext context); }

public sealed class JsonRuntimeLogSink(ObservabilityOptions options, IObservabilitySanitizer sanitizer) : IRuntimeLogSink
{
    private readonly object _gate = new();
    public void Write(ObservabilityEvent entry, CorrelationContext context)
    {
        if (string.IsNullOrWhiteSpace(options.LogDirectory)) return;
        try
        {
            var directory = options.LogDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"runtime-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            lock (_gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= options.MaximumLogFileBytes)
                    path = Path.Combine(directory, $"runtime-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
                var payload = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow, severity = entry.Severity.ToString(), eventId = entry.Definition.Id,
                    eventName = entry.Definition.Name, component = entry.Component, correlationId = context.CorrelationId,
                    operationId = entry.OperationId ?? context.OperationId, action = entry.Action ?? context.Action,
                    outcome = entry.Outcome.ToString().ToLowerInvariant(), problemCode = entry.ProblemCode,
                    resourceType = entry.ResourceType, resourceReference = entry.ResourceReference is null ? null : sanitizer.ToReference(entry.ResourceReference),
                    durationMs = entry.DurationMs, message = sanitizer.SanitizeSummary(entry.Message), exceptionType = entry.Exception?.Type
                });
                File.AppendAllText(path, payload + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
