using System.Security.Claims;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.Server.Tailscale;

public interface ITailscaleManager
{
    Task<TailscaleStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}

public interface ITailscaleControl
{
    Task<TailscaleControlSnapshot> ReadAsync(ClaimsPrincipal principal, CancellationToken ct);
    Task<TailscaleOperation> PrepareAsync(ClaimsPrincipal principal, TailscalePrepareRequest request, CancellationToken ct);
    Task<TailscaleOperation> ExecuteAsync(ClaimsPrincipal principal, Guid operationId, bool confirmed, CancellationToken ct);
    Task<TailscaleOperation> GetAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleOperation> CloseAsync(ClaimsPrincipal principal, TailscaleCloseRequest request, CancellationToken ct);
}

public interface ITailscaleControlHost
{
    Task<TailscaleControlSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<TailscaleControlResult> ExecuteAsync(TailscalePrepareRequest request, CancellationToken cancellationToken);
    Task<TailscaleControlResult?> GetResultAsync(Guid operationId, CancellationToken cancellationToken);
    Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(Guid operationId, CancellationToken cancellationToken);
    Task<TailscaleRecoveryClosure> CloseAsync(TailscaleCloseRequest request, string actor, CancellationToken cancellationToken);
    Task<TailscaleRecoveryClosure?> GetClosureAsync(Guid operationId, CancellationToken cancellationToken);
}

public sealed class TailscaleControlException(int statusCode, string problemCode) : Exception(problemCode)
{
    public int StatusCode { get; } = statusCode;
    public string ProblemCode { get; } = problemCode;
}

public interface ITailscaleLogin
{
    Task<TailscaleLoginOperation> PrepareAsync(ClaimsPrincipal principal, TailscaleLoginPrepareRequest request, CancellationToken ct);
    Task<TailscaleLoginOperation> ExecuteAsync(ClaimsPrincipal principal, Guid operationId, bool confirmed, CancellationToken ct);
    Task<TailscaleLoginOperation> GetAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleLoginChallengeResponse> ReadChallengeAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleLoginRecoverySnapshot> ReadRecoveryAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleLoginOperation> CloseAsync(ClaimsPrincipal principal, TailscaleLoginCloseRequest request, CancellationToken ct);
}

public interface ITailscaleLoginHost
{
    Task<TailscaleLoginObservation> ObserveAsync(CancellationToken cancellationToken);
    Task<TailscaleLoginReceipt> BeginAsync(TailscaleLoginCommand command, CancellationToken cancellationToken);
    Task<TailscaleLoginReceipt?> GetReceiptAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken);
    Task<TailscaleLoginChallenge> ReadChallengeAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken);
    Task<TailscaleLoginObservation> ReadRecoveryAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken);
    Task<TailscaleLoginClosure> CloseAsync(TailscaleLoginIntent intent, TailscaleLoginCloseRequest request, CancellationToken cancellationToken);
    Task<TailscaleLoginClosure?> GetClosureAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken);
}

public interface ITailscaleControlAuthorization
{
    bool IsGranted(ClaimsPrincipal principal);
}
