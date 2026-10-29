using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Tailscale;

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

/// <summary>The Helper is the only producer of login evidence and of the official browser challenge.</summary>
public sealed class HelperTailscaleLoginHost(IPrivilegedOperationTransport transport) : ITailscaleLoginHost
{
    public async Task<TailscaleLoginObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginObserve, OperationId: Guid.NewGuid()), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleLoginObservation is { } observation
            ? observation : throw new TailscaleControlException(503, "tailscale.login.unavailable");
    }

    public async Task<TailscaleLoginReceipt> BeginAsync(TailscaleLoginCommand command, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginBegin,
            OperationId: command.Intent.OperationId, TailscaleLogin: command), cancellationToken);
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleLoginReceipt is { } receipt
            && receipt.OperationId == command.Intent.OperationId ? receipt : throw Failure(result);
    }

    public async Task<TailscaleLoginReceipt?> GetReceiptAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginReceipt,
            OperationId: intent.OperationId, TailscaleLoginIdentity: intent), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version
            && result.TailscaleLoginReceipt is { } receipt && receipt.OperationId == intent.OperationId ? receipt : null;
    }

    public async Task<TailscaleLoginChallenge> ReadChallengeAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginChallenge,
            OperationId: intent.OperationId, TailscaleLoginIdentity: intent), cancellationToken);
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleLoginChallenge is { } challenge
            && challenge.OperationId == intent.OperationId && challenge.AuthenticationUrl.Length is > 0 and <= 512 ? challenge : throw Failure(result);
    }

    public async Task<TailscaleLoginObservation> ReadRecoveryAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginRecoveryRead,
            OperationId: intent.OperationId, TailscaleLoginIdentity: intent), cancellationToken);
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleLoginRecovery is { } recovery
            && recovery.OperationId == intent.OperationId && recovery.Observation is { } observation ? observation : throw Failure(result);
    }

    public async Task<TailscaleLoginClosure> CloseAsync(TailscaleLoginIntent intent, TailscaleLoginCloseRequest request, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginClose,
            OperationId: intent.OperationId, TailscaleLoginIdentity: intent, TailscaleLoginClose: request), cancellationToken);
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleLoginClosure is { } closure
            && closure.Intent.OperationId == request.OperationId ? closure : throw Failure(result);
    }

    public async Task<TailscaleLoginClosure?> GetClosureAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleLoginRecoveryReceipt,
            OperationId: intent.OperationId, TailscaleLoginIdentity: intent), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version
            && result.TailscaleLoginClosure is { } closure && closure.Intent.OperationId == intent.OperationId ? closure : null;
    }

    private static TailscaleControlException Failure(PrivilegedOperationResult result) =>
        result.ProblemCode == PrivilegedProblemCode.Conflict
            ? new(409, "tailscale.login.state_conflict")
            : new(503, "tailscale.login.unavailable");
}
