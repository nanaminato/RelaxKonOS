using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Observability;

/// <summary>
/// The only correlation metadata permitted to cross a process boundary.  It deliberately
/// contains no credential, request, user name, address, or capability information.
/// </summary>
public sealed record CorrelationContext(
    [property: JsonPropertyName("correlationId")] Guid CorrelationId,
    [property: JsonPropertyName("operationId")] Guid? OperationId = null,
    [property: JsonPropertyName("action")] string? Action = null)
{
    public static CorrelationContext Create(Guid? operationId = null, string? action = null)
        => new(Guid.NewGuid(), operationId, action);

    public bool IsValid()
        => CorrelationId != Guid.Empty
           && (OperationId is null || OperationId != Guid.Empty)
           && (Action is null || ObservabilityEventCatalog.IsKnownAction(Action));
}
