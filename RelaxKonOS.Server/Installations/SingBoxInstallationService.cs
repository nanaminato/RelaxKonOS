using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Installations;

/// <summary>Uses the existing host ledger; channel secrets and binary paths never enter its request.</summary>
public sealed class SingBoxInstallationService(SingBoxRuntimeService runtime) : InstallationService<ManagedRuntimeInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.SingBox;
    public override IReadOnlyList<string> Resources => ["service:SingBox", "runtime:sing-box"];
    protected override bool Confirmed(ManagedRuntimeInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install
        or InstallationOperationKind.Upgrade or InstallationOperationKind.Repair or InstallationOperationKind.Rollback
        or InstallationOperationKind.Uninstall;

    protected override void ValidateRequest(InstallationOperationKind kind, ManagedRuntimeInstallationRequest request, string idempotencyKey)
    {
        var plan = request.Plan;
        if (plan is not { RuntimeId: "sing-box", Request: not null })
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        var change = kind switch
        {
            InstallationOperationKind.Install => RuntimeChange.Install,
            InstallationOperationKind.Upgrade => RuntimeChange.Update,
            InstallationOperationKind.Repair => RuntimeChange.Repair,
            InstallationOperationKind.Rollback => RuntimeChange.Rollback,
            InstallationOperationKind.Uninstall => RuntimeChange.Uninstall,
            _ => throw new InstallationException(InstallationProblemCodes.NotSupported, 400)
        };
        // Removal is the only change that carries no release; a retained-data default is the request's own field.
        if (change == RuntimeChange.Uninstall)
        {
            if (plan.Release is not null || plan.Request.ReleaseId is not null)
                throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        }
        else if (plan.Release is not { RuntimeId: "sing-box" } release || plan.Request.ReleaseId != release.ReleaseId)
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        if (plan.Request.Change != change || plan.Request.InstallationId == Guid.Empty)
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        Check(plan.Request.IdempotencyKey == idempotencyKey, InstallationProblemCodes.IdempotencyConflict);
    }

    protected override async Task ExecuteAsync(InstallationOperationKind kind, ManagedRuntimeInstallationRequest options,
        string actor, IInstallationProgress progress, CancellationToken ct)
    {
        await progress.ReportAsync(new(InstallationStage.Preflight, Cancellable: false), ct);
        try
        {
            var result = await runtime.ExecuteAsync(progress.OperationId, options.Plan, ct);
            Check(result.Outcome == RuntimeSwitchOutcome.Succeeded, result.ProblemCode ?? "runtime.operation_failed");
        }
        catch (RuntimeLifecycleException ex) { throw new InstallationException(ex.ProblemCode); }
    }

    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await runtime.QueryAsync(ct, operation.OperationId);
                if (snapshot.Receipt is { } receipt) return FromReceipt(receipt);
                if (snapshot.PendingOperationId is null)
                    return new(InstallationOperationState.Interrupted, "runtime.operation_interrupted");
                if (snapshot.PendingOperationId != operation.OperationId)
                    return new(InstallationOperationState.Failed, "runtime.recovery_target_conflict");
                var recovered = await runtime.RecoverAsync(Guid.NewGuid(), operation.OperationId, ct);
                if (recovered is not null)
                    return recovered.OperationId == operation.OperationId ? FromReceipt(recovered)
                        : new(InstallationOperationState.Failed, "runtime.helper_response_invalid");
            }
            catch (RuntimeLifecycleException ex) when (ex.ProblemCode == "runtime.instance_busy")
            {
                // An OS-held instance lock proves another worker still owns the transaction.
                // Keep its ledger entry active; do not restart it or infer termination from a wait timeout.
                await Task.Delay(250, ct);
            }
            catch (RuntimeLifecycleException ex) { return new(InstallationOperationState.Failed, ex.ProblemCode); }
        }
    }

    private static InstallationRecovery FromReceipt(RuntimeSwitchResult result) => result.Outcome switch
    {
        RuntimeSwitchOutcome.Succeeded => new(InstallationOperationState.Succeeded),
        RuntimeSwitchOutcome.Interrupted => new(InstallationOperationState.Interrupted, result.ProblemCode),
        RuntimeSwitchOutcome.Cancelled => new(InstallationOperationState.Interrupted, result.ProblemCode),
        _ => new(InstallationOperationState.Failed, result.ProblemCode ?? "runtime.recovery_required")
    };
}
