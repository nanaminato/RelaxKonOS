using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Tailscale;

/// <summary>
/// One first-login attempt as bound by Server authorization. The Helper persists this identity
/// before starting the native command; it is evidence, never an HTTP authorization body.
/// </summary>
public sealed record TailscaleLoginIntent(Guid OperationId, Guid HostId, Guid Actor, Guid SessionId,
    string ExpectedRevision, DateTimeOffset ExpiresAt);

[JsonConverter(typeof(JsonStringEnumConverter<TailscaleLoginState>))]
public enum TailscaleLoginState
{
    Prepared,
    Applying,
    Issued,
    Completed,
    ApprovalRequired,
    Unknown,
    Rejected,
}

/// <summary>URL-free observation of the daemon's login state and public preference digest.</summary>
public sealed record TailscaleLoginObservation(string Revision, string PreferencesRevision, string BackendState, bool CanStart);

/// <summary>Server supplies the confirmed plan; the Helper re-validates every field.</summary>
public sealed record TailscaleLoginCommand(TailscaleLoginIntent Intent, bool Confirmed);

/// <summary>Immutable Helper evidence. It holds only a challenge digest, never the authentication URL.</summary>
public sealed record TailscaleLoginReceipt(Guid OperationId, TailscaleLoginState State, DateTimeOffset RecordedAt,
    string? PreferencesRevision = null, string? ObservedRevision = null, string? ChallengeFingerprint = null);

public sealed record TailscaleLoginCloseRequest(Guid OperationId, string ExpectedRevision, bool Confirmed);

public sealed record TailscaleLoginCloseCommand(TailscaleLoginIntent Intent, TailscaleLoginCloseRequest Request);

public sealed record TailscaleLoginClosure(TailscaleLoginIntent Intent, TailscaleLoginObservation Observation, DateTimeOffset ClosedAt);

public sealed record TailscaleLoginRecoverySnapshot(Guid OperationId, TailscaleLoginObservation? Observation, bool CanClose,
    TailscaleLoginClosure? Closure = null, string? ProblemCode = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Transient delivery shape for the browser challenge. It is only returned by the dedicated
/// challenge operation; its diagnostic text never contains the URL.
/// </summary>
public sealed record TailscaleLoginChallenge(Guid OperationId, string AuthenticationUrl, TailscaleLoginObservation Observation)
{
    public override string ToString() => "TailscaleLoginChallenge { OperationId = " + OperationId + ", AuthenticationUrl = <redacted> }";
}

// Server-side operation contract. The Helper never receives these records.
public sealed record TailscaleLoginPrepareRequest(Guid OperationId);

public sealed record TailscaleLoginExecuteRequest(bool Confirmed);

public sealed record TailscaleLoginOperation(Guid OperationId, TailscaleLoginState State, string ExpectedRevision,
    DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt, string? ProblemCode = null, TailscaleLoginClosure? Closure = null);

/// <summary>The authentication URL is delivered once through this dedicated response, not stored in the operation.</summary>
public sealed record TailscaleLoginChallengeResponse(Guid OperationId, string AuthenticationUrl, DateTimeOffset ExpiresAt);
