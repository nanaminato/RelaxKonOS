using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Installations;

/// <summary>One installation ledger entry: Linux uses publication-aware signed plans;
/// Windows uses the selected official version or user-supplied archive and its independent service host.</summary>
public sealed class MihomoRuntimeInstallationService(MihomoPublicationStore store, MihomoRuntimeService runtime,
    MihomoInstallationBootstrapService bootstrap, MihomoInstallationPublicationService installation,
    MihomoRemovalPublicationService removal, MihomoRuntimeManager native, InstallationFileReferenceStore files) : InstallationService<MihomoInstallationRequest>
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> nativeCancellations = new();
    public override InstallationServiceId Id => InstallationServiceId.Mihomo;
    public override IReadOnlyList<string> Resources => ["service:Mihomo", "runtime:mihomo"];
    public override bool CooperativeCancel => true;
    protected override bool Confirmed(MihomoInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install
        or InstallationOperationKind.Upgrade or InstallationOperationKind.Repair or InstallationOperationKind.Rollback
        or InstallationOperationKind.Uninstall;

    protected override void ValidateRequest(InstallationOperationKind kind, MihomoInstallationRequest request, string key)
    {
        if (request.Version is not null)
        {
            Check(OperatingSystem.IsWindows() && request.Plan is null && kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Repair,
                InstallationProblemCodes.InvalidRequest);
            _ = RelaxKonOS.ManagedRuntimes.ComponentPackages.OfficialComponentReleases.Describe("mihomo",
                RelaxKonOS.Protocol.Proxy.MihomoRuntimeManifest.CurrentRid(), request.Version);
            return;
        }
        Check(request.FileReferenceId is null, InstallationProblemCodes.InvalidRequest);
        var plan = request.Plan;
        if (plan is not { RuntimeId: "mihomo", Request: not null } || plan.Request.InstallationId == Guid.Empty
            || plan.Request.Change != Change(kind)) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        if (plan.Request.Change == RuntimeChange.Uninstall
            ? plan.Release is not null || plan.Request.ReleaseId is not null
            : plan.Release is not { RuntimeId: "mihomo" } || plan.Request.ReleaseId != plan.Release.ReleaseId)
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        Check(plan.Request.IdempotencyKey == key, InstallationProblemCodes.IdempotencyConflict);
    }

    protected override async Task ExecuteAsync(InstallationOperationKind kind, MihomoInstallationRequest request,
        string actor, IInstallationProgress progress, CancellationToken ct)
    {
        if (request.Version is { } version)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            nativeCancellations[progress.OperationId] = cancellation;
            try
            {
                using var source = request.FileReferenceId is { } reference ? files.Open(Id, actor, reference) : null;
                var result = await native.InstallOfficialAsync(version, source, progress, cancellation.Token);
                Check(result.IntegrityVerified && string.IsNullOrEmpty(result.ProblemCode), result.ProblemCode);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { throw new InstallationCancelledException(InstallationProblemCodes.Cancelled); }
            finally { nativeCancellations.TryRemove(progress.OperationId, out _); }
            return;
        }
        await progress.ReportAsync(new(InstallationStage.Preflight, Cancellable: kind != InstallationOperationKind.Uninstall), ct);
        try
        {
            var result = kind switch
            {
                InstallationOperationKind.Install => await bootstrap.InstallAsync(progress.OperationId, request.Plan!, ct),
                InstallationOperationKind.Uninstall => await removal.RemoveAsync(progress.OperationId, request.Plan!, ct),
                _ => await ExecuteProgramAsync(progress.OperationId, request.Plan!, ct)
            };
            RequireCompleted(result, progress.OperationId);
        }
        catch (Exception ex) when (ex is IOException || ex is RuntimeLifecycleException { ProblemCode: "runtime.helper_unavailable" }
            or RuntimePolicyException { ProblemCode: "runtime.helper_unavailable" })
        {
            // An accepted Helper/Server operation may already have committed. Resolve its durable evidence,
            // without starting a second install or reporting transport loss as the runtime's final outcome.
            var recovery = await RecoverCoreAsync(progress.OperationId, kind, CancellationToken.None);
            RequireRecovery(recovery);
        }
        catch (RuntimePolicyException ex) { throw new InstallationException(ex.ProblemCode); }
        catch (RuntimeLifecycleException ex) { throw new InstallationException(ex.ProblemCode); }
    }

    private async Task<MihomoPublicationResult> ExecuteProgramAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        var receipt = await runtime.ExecuteAsync(operationId, plan, ct);
        var outcome = receipt.Outcome == RuntimeSwitchOutcome.Succeeded ? MihomoPublicationOutcome.Confirmed
            : receipt.Outcome == RuntimeSwitchOutcome.RecoveryRequired ? MihomoPublicationOutcome.RecoveryRequired
            : MihomoPublicationOutcome.Compensated;
        return new(operationId, outcome, receipt);
    }

    public override async Task RequestCancelAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        if (nativeCancellations.TryGetValue(operation.OperationId, out var nativeCancellation))
        {
            try { nativeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        try { await runtime.RequestCancelAsync(operation.OperationId, ct); }
        catch (RuntimeLifecycleException ex) { throw new InstallationException(ex.ProblemCode); }
    }

    public override Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct) =>
        native is not null && native.OwnsInstallationOperation(operation.OperationId) ? Task.FromResult(new InstallationRecovery(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted))
            : RecoverCoreAsync(operation.OperationId, operation.Kind, ct);

    private async Task<InstallationRecovery> RecoverCoreAsync(Guid operationId, InstallationOperationKind kind, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await store.Gate.WaitAsync(ct);
                try
                {
                    var state = await store.ReadAsync(ct);
                    if (state.Pending is { } pending)
                    {
                        if (pending.OperationId != operationId) return Failed("runtime.recovery_target_conflict");
                        MihomoPublicationResult publication;
                        if (kind == InstallationOperationKind.Install && pending.InstallationPlan is not null)
                            publication = await installation.RecoverUnderGateAsync(state, ct);
                        else if (kind == InstallationOperationKind.Uninstall && pending.RemovalPlan is not null)
                            publication = await removal.RecoverUnderGateAsync(state, ct);
                        else return Failed("runtime.publication_unconfirmed");
                        return FromPublication(publication, operationId);
                    }
                    var observed = await runtime.QueryHelperAsync(ct, operationId);
                    if (observed.PendingOperationId is { } other && other != operationId)
                        return Failed("runtime.recovery_target_conflict");
                    if (observed.PendingOperationId == operationId || observed.Receipt is null)
                    {
                        // Even "not started" needs the Helper instance lock. Its recovery returns null
                        // without requesting credentials only when there is no root transaction to resume.
                        var recovered = await runtime.RecoverUnderGateAsync(Guid.NewGuid(), operationId, ct);
                        observed = await runtime.QueryHelperAsync(ct, operationId);
                        if (recovered is not null && recovered.OperationId != operationId) return Failed("runtime.operation_id_conflict");
                        if (recovered is { Outcome: RuntimeSwitchOutcome.RecoveryRequired }) return Failed("runtime.recovery_required");
                        if (recovered is not null && observed.Receipt != recovered) return Failed("runtime.helper_response_invalid");
                    }
                    if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired)
                        return Failed("runtime.recovery_required");
                    if (observed.Receipt is not { } receipt)
                        return new(InstallationOperationState.Interrupted, "runtime.operation_not_started");
                    if (receipt.OperationId != operationId) return Failed("runtime.operation_id_conflict");
                    if (receipt.Outcome == RuntimeSwitchOutcome.Succeeded)
                    {
                        // A successful native install/removal is insufficient until its Server publication
                        // also confirms. Never use process liveness or infer a missing publication success.
                        if (kind == InstallationOperationKind.Install && state.LastInstallation is not { Confirmed: true })
                            return Failed("runtime.publication_unconfirmed");
                        if (kind == InstallationOperationKind.Install && state.LastInstallation!.OperationId != operationId
                            || kind == InstallationOperationKind.Uninstall && (state.LastRemoval is not { Confirmed: true }
                                || state.LastRemoval.OperationId != operationId)) return Failed("runtime.publication_unconfirmed");
                        if (observed.ConfigurationIdentity != state.Identity) return Failed("runtime.configuration_drift");
                        if (kind != InstallationOperationKind.Uninstall && (observed.Installation.Ownership != RuntimeOwnership.Managed
                            || observed.ActiveRevisionId is null || state.Published is null)) return Failed("runtime.state_unknown");
                        if (kind == InstallationOperationKind.Uninstall && (observed.Installation.Ownership != RuntimeOwnership.Absent
                            || observed.ActiveRevisionId is not null)) return Failed("runtime.state_unknown");
                    }
                    return FromReceipt(receipt);
                }
                finally { store.Gate.Release(); }
            }
            catch (RuntimeLifecycleException ex) when (ex.ProblemCode is "runtime.instance_busy" or "runtime.helper_unavailable")
            { await Task.Delay(250, ct); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode is "runtime.instance_busy" or "runtime.helper_unavailable") { await Task.Delay(250, ct); }
            catch (RuntimeLifecycleException ex) { return Failed(ex.ProblemCode); }
            catch (RuntimePolicyException ex) { return Failed(ex.ProblemCode); }
            catch (IOException) { return Failed("runtime.publication_unconfirmed"); }
        }
    }

    private static void RequireCompleted(MihomoPublicationResult result, Guid operationId)
    {
        if (result.OperationId != operationId) throw new InstallationException("runtime.operation_id_conflict");
        if (result.Outcome == MihomoPublicationOutcome.RecoveryRequired)
            throw new InstallationException(result.Receipt?.ProblemCode ?? "runtime.publication_unconfirmed");
        if (result.Outcome == MihomoPublicationOutcome.NotStarted) throw new InstallationInterruptedException("runtime.operation_not_started");
        if (result.Receipt is null || (result.Outcome == MihomoPublicationOutcome.Confirmed)
            != (result.Receipt.Outcome == RuntimeSwitchOutcome.Succeeded)) throw new InstallationException("runtime.helper_response_invalid");
        RequireRecovery(FromReceipt(result.Receipt));
    }
    private static void RequireRecovery(InstallationRecovery result)
    {
        if (result.State == InstallationOperationState.Succeeded) return;
        if (result.State == InstallationOperationState.Cancelled) throw new InstallationCancelledException(result.ProblemCode ?? "runtime.operation_cancelled");
        if (result.State == InstallationOperationState.Interrupted) throw new InstallationInterruptedException(result.ProblemCode ?? "runtime.operation_interrupted");
        throw new InstallationException(result.ProblemCode ?? "runtime.operation_failed");
    }
    private static InstallationRecovery FromPublication(MihomoPublicationResult result, Guid operationId) =>
        result.OperationId != operationId ? Failed("runtime.operation_id_conflict")
        : result.Outcome == MihomoPublicationOutcome.RecoveryRequired ? Failed(result.Receipt?.ProblemCode ?? "runtime.publication_unconfirmed")
        : result.Outcome == MihomoPublicationOutcome.NotStarted ? new(InstallationOperationState.Interrupted, "runtime.operation_not_started")
        : result.Receipt is null || (result.Outcome == MihomoPublicationOutcome.Confirmed) != (result.Receipt.Outcome == RuntimeSwitchOutcome.Succeeded)
            ? Failed("runtime.helper_response_invalid") : FromReceipt(result.Receipt);
    private static InstallationRecovery FromReceipt(RuntimeSwitchResult result) => result.Outcome switch
    {
        RuntimeSwitchOutcome.Succeeded => new(InstallationOperationState.Succeeded),
        RuntimeSwitchOutcome.Cancelled => new(InstallationOperationState.Cancelled, result.ProblemCode),
        RuntimeSwitchOutcome.Interrupted => new(InstallationOperationState.Interrupted, result.ProblemCode),
        _ => Failed(result.ProblemCode ?? "runtime.recovery_required")
    };
    private static InstallationRecovery Failed(string code) => new(InstallationOperationState.Failed, code);
    private static RuntimeChange Change(InstallationOperationKind kind) => kind switch
    {
        InstallationOperationKind.Install => RuntimeChange.Install, InstallationOperationKind.Upgrade => RuntimeChange.Update,
        InstallationOperationKind.Repair => RuntimeChange.Repair, InstallationOperationKind.Rollback => RuntimeChange.Rollback,
        InstallationOperationKind.Uninstall => RuntimeChange.Uninstall, _ => throw new InstallationException(InstallationProblemCodes.NotSupported, 400)
    };
}
