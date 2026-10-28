using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Tailscale;

public sealed class HelperTailscaleControlHost(IPrivilegedOperationTransport transport) : ITailscaleControlHost
{
    public async Task<TailscaleControlSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleControlRead, OperationId: Guid.NewGuid()), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleControl is { } snapshot
            ? snapshot : throw new TailscaleControlException(503, "tailscale.control_unavailable");
    }
    public async Task<TailscaleControlResult> ExecuteAsync(TailscalePrepareRequest request, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleControlExecute,
            OperationId: request.OperationId, TailscaleChange: request), cancellationToken);
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleResult is { } receipt
            && receipt.OperationId == request.OperationId ? receipt : new(request.OperationId, false, ProblemCode: "tailscale.operation.outcome_unknown");
    }
    public async Task<TailscaleControlResult?> GetResultAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleControlReceipt, OperationId: operationId), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleResult?.OperationId == operationId
            ? result.TailscaleResult : null;
    }
    public async Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleRecoveryRead, OperationId: operationId), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleRecovery is { } recovery && recovery.OperationId == operationId
            ? recovery : throw RecoveryFailure(result);
    }
    public async Task<TailscaleRecoveryClosure> CloseAsync(TailscaleCloseRequest request, string actor, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleRecoveryClose, OperationId: request.OperationId,
            TailscaleRecovery: new(request, actor)), cancellationToken);
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleClosure is { } closure && closure.OperationId == request.OperationId
            ? closure : throw RecoveryFailure(result);
    }
    public async Task<TailscaleRecoveryClosure?> GetClosureAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.TailscaleRecoveryReceipt, OperationId: operationId), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.Version == PrivilegedOperationProtocol.Version && result.TailscaleClosure?.OperationId == operationId ? result.TailscaleClosure : null;
    }
    private static TailscaleControlException RecoveryFailure(PrivilegedOperationResult result) => new(
        result.Success || result.ProblemCode == PrivilegedProblemCode.Conflict ? 409 : 503,
        result.Success || result.ProblemCode == PrivilegedProblemCode.Conflict ? "tailscale.recovery.not_ready" : "tailscale.recovery.unavailable");

}
