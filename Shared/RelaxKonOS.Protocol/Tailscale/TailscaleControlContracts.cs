using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Tailscale;

[JsonConverter(typeof(JsonStringEnumConverter<TailscaleAction>))]
public enum TailscaleAction { Connect, Disconnect, Configure }

[JsonConverter(typeof(JsonStringEnumConverter<TailscaleOperationState>))]
public enum TailscaleOperationState { Prepared, Applying, Applied, Rejected, Unknown }

public sealed record TailscaleConfiguration(bool AcceptDns, bool AcceptRoutes);

/// <summary>Revision covers daemon identity and native preferences, not the changing peer list.</summary>
public sealed record TailscaleControlSnapshot(string Revision, bool Connected, bool Authenticated,
    TailscaleConfiguration Configuration, bool CanControl, string? ProblemCode = null);

public sealed record TailscalePrepareRequest(Guid OperationId, string ExpectedRevision, TailscaleAction Action,
    TailscaleConfiguration? Configuration = null);

public sealed record TailscaleOperation(Guid OperationId, TailscaleAction Action, TailscaleOperationState State,
    string ExpectedRevision, DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt,
    TailscaleConfiguration? Configuration = null, string? ObservedRevision = null, string? ProblemCode = null, TailscaleRecoveryClosure? Closure = null);

/// <summary>Completed is only set after the Helper observes this operation's own native result.</summary>
public sealed record TailscaleControlResult(Guid OperationId, bool Completed,
    TailscaleControlSnapshot? Snapshot = null, string? ProblemCode = null);

public sealed record TailscaleExecuteRequest(bool Confirmed);

/// <summary>Acknowledges the observed host state without asserting that the original change succeeded.</summary>
public sealed record TailscaleCloseRequest(Guid OperationId, string ExpectedRevision, bool Confirmed);

/// <summary>Actor is derived by Server from the authenticated principal, never accepted from the HTTP body.</summary>
public sealed record TailscaleRecoveryCommand(TailscaleCloseRequest Request, string Actor);

public sealed record TailscaleRecoveryClosure(Guid OperationId, TailscaleControlSnapshot Observation,
    DateTimeOffset ClosedAt, string Actor);

public sealed record TailscaleRecoverySnapshot(Guid OperationId, TailscaleControlSnapshot? Observation,
    bool CanClose, TailscaleRecoveryClosure? Closure = null, string? ProblemCode = null, DateTimeOffset? ExpiresAt = null);
