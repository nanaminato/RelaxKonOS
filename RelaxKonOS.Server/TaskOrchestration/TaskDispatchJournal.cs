using System.Text.Json;
using System.Collections.Immutable;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Exact dispatch intent, saved before contacting the installation domain. Contains no elevation credentials.</summary>
public sealed record TaskDispatchIntent(string StepId, string AdapterRevision, InstallationServiceId Service,
    InstallationOperationKind Kind, JsonElement Request, string RequestDigest);

public sealed partial class TaskPlanStore
{
    public TaskDispatchIntent PrepareDispatch(Guid runId, string stepId, TaskExecutionIdentity identity, long expectedRevision) => Guard(() =>
    {
        ValidateIdentity(identity.CanonicalSubject, identity.SessionIdentity, identity.HostId, identity.PolicyRevision);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, identity.CanonicalSubject)
            ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var approval = ReadApproval(connection, transaction, run.ApprovalId, identity.CanonicalSubject)!.Record;
        RequireSameIdentity(approval, identity);
        if (approval.State != TaskApprovalState.Consumed || approval.RunAuthorizationExpiresAt <= _clock.GetUtcNow())
            throw new TaskPlanStoreException("task.approval_unavailable", 403);
        var plan = Read(connection, transaction, run.PlanId, identity.CanonicalSubject)!.Record;
        if (plan.Validity != TaskPlanValidity.Resolved) throw new TaskPlanStoreException("task.plan_expired", 409);
        var step = run.Steps.SingleOrDefault(item => item.StepId == stepId)
            ?? throw new TaskPlanStoreException("task.step_not_found", 404);
        var proposal = plan.Plan.Semantics.Steps.Single(item => item.StepId == stepId);
        var dispatch = BuildDispatch(run, proposal, step);
        if (run.CancellationRequested || run.State is not (TaskRunState.Queued or TaskRunState.Running))
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        if (stored.Dispatches.SingleOrDefault(item => item.StepId == stepId) is { } previous)
            return previous; // The immutable intent can be reread; this is never fresh execution authority.
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.State is not (TaskRunState.Queued or TaskRunState.Running) || run.CancellationRequested
            || step.State != TaskStepRunState.Pending || run.Steps.Any(item => item.State is TaskStepRunState.Dispatching or TaskStepRunState.Running)
            || proposal.DependsOn.Any(id => run.Steps.Single(item => item.StepId == id).State is not (TaskStepRunState.Succeeded or TaskStepRunState.NoOp)))
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        var changed = stored with
        {
            Record = run with { State = TaskRunState.Running, Revision = run.Revision + 1,
                Steps = run.Steps.Select(item => item.StepId == stepId ? item with
                    { State = TaskStepRunState.Dispatching, RequestDigest = dispatch.RequestDigest } : item).ToImmutableArray() },
            Dispatches = [.. stored.Dispatches, dispatch],
            Audit = [.. stored.Audit, new("dispatch_prepared", stepId, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.DispatchWritten);
        transaction.Commit();
        return dispatch;
    });

    public TaskRun BindDomainOperation(Guid runId, string stepId, string subject, string requestDigest, Guid operationId, long expectedRevision) => Guard(() =>
    {
        if (operationId == Guid.Empty) throw new TaskPlanStoreException("task.domain_operation_invalid", 400);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var step = run.Steps.SingleOrDefault(item => item.StepId == stepId) ?? throw new TaskPlanStoreException("task.step_not_found", 404);
        if (step.RequestDigest != requestDigest || step.RequestDigest is null) throw new TaskPlanStoreException("task.dispatch_conflict", 409);
        if (step.DomainOperationId is { } existing)
            return existing == operationId ? run : throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.State is not (TaskRunState.Running or TaskRunState.Cancelling) || step.State != TaskStepRunState.Dispatching)
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        var changed = stored with
        {
            Record = run with { Revision = run.Revision + 1, Steps = run.Steps.Select(item => item.StepId == stepId
                ? item with { State = TaskStepRunState.Running, DomainOperationId = operationId } : item).ToImmutableArray() },
            Audit = [.. stored.Audit, new("operation_bound", stepId, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.OperationBound);
        transaction.Commit();
        return changed.Record;
    });

    public TaskDispatchIntent? GetDispatch(Guid runId, string stepId, string subject) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        return ReadRun(connection, transaction, runId, subject)?.Dispatches.SingleOrDefault(item => item.StepId == stepId);
    });

    private static TaskDispatchIntent BuildDispatch(TaskRun run, TaskStep proposal, TaskStepRun step)
    {
        if (proposal.RequiredCapabilities is not [{ Capability: "SingBoxInstall", Target: "singbox" }]
            || proposal.Disposition != TaskStepDisposition.Ready)
            throw new TaskPlanStoreException("task.action_revision_unsupported", 409);
        var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(proposal.Inputs);
        var selection = frozen.Selection;
        var request = new RuntimePlanRequest(selection.InstallationId, selection.HostId, frozen.Change,
            selection.ExpectedStateRevision, selection.ExpectedCatalogRevision, selection.ExpectedTrustRevision,
            selection.ReleaseId, step.IdempotencyKey, selection.AllowPrerelease, selection.KeepData);
        var body = TaskPlanJson.FreezeInputs(new ManagedRuntimeInstallationRequest(true,
            new RuntimeChangePlan(request, frozen.RuntimeId, frozen.Release, frozen.PreviousReleaseId)));
        var kind = frozen.Change switch
        {
            RuntimeChange.Install => InstallationOperationKind.Install, RuntimeChange.Update => InstallationOperationKind.Upgrade,
            RuntimeChange.Repair => InstallationOperationKind.Repair, RuntimeChange.Rollback => InstallationOperationKind.Rollback,
            RuntimeChange.Uninstall => InstallationOperationKind.Uninstall, _ => throw new InvalidDataException()
        };
        const string adapter = "sing-box-dispatch-1";
        return new(proposal.StepId, adapter, InstallationServiceId.SingBox, kind, body,
            Fingerprint(new { run.RunId, run.HostId, proposal.StepId, adapter, service = InstallationServiceId.SingBox, kind, request = body }));
    }

    private void UpdateRun(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, StoredRun stored)
    {
        using var command = Command(connection, transaction,
            "UPDATE task_runs SET revision=$revision,protected_record=$record WHERE run_id=$id AND revision=$previous;",
            ("$id", stored.Record.RunId.ToString("D")), ("$revision", stored.Record.Revision),
            ("$previous", stored.Record.Revision - 1), ("$record", EncodeExecution(stored)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException();
    }

    private static void ValidateRunJournal(StoredRun stored, TaskPlan plan)
    {
        var run = stored.Record;
        if (stored.Dispatches.IsDefault || stored.Dispatches.Length > run.Steps.Length
            || stored.Dispatches.Any(item => item is null) || stored.Dispatches.Select(item => item.StepId).Distinct().Count() != stored.Dispatches.Length
            || stored.Results.IsDefault || stored.Results.Length > stored.Dispatches.Length || stored.Results.Any(item => item is null || item.Operation is null)
            || stored.Results.Any(item => !run.Steps.Any(step => step.StepId == item.StepId))
            || stored.Results.Select(item => item.StepId).Distinct().Count() != stored.Results.Length
            || stored.ReadResults.IsDefault || stored.ReadResults.Length > run.Steps.Length || stored.ReadResults.Any(item => item is null
                || !run.Steps.Any(step => step.StepId == item.StepId))
            || stored.ReadResults.Select(item => item.StepId).Distinct().Count() != stored.ReadResults.Length
            || stored.RecoveryResults.IsDefault || stored.RecoveryResults.Length > 32
            || stored.RecoveryResults.Any(item => item is null || item.Operation is null || !run.Steps.Any(step => step.StepId == item.StepId))
            || stored.Audit.IsDefaultOrEmpty || stored.Audit.Length != run.Revision
            || stored.Audit[0] is not { Event: "queued", StepId: null } || stored.Audit[0].At != run.CreatedAt)
            throw new InvalidDataException();
        var states = run.Steps.ToDictionary(item => item.StepId, _ => TaskStepRunState.Pending, StringComparer.Ordinal);
        var at = run.CreatedAt;
        var cancellationRequested = false;
        var recoveryIndex = 0;
        foreach (var audit in stored.Audit.Skip(1))
        {
            if (audit is { Event: "cancel_requested", StepId: null } && !cancellationRequested && audit.At >= at
                && states.Values.Any(value => value is TaskStepRunState.Pending or TaskStepRunState.Dispatching or TaskStepRunState.Running))
            {
                at = audit.At; cancellationRequested = true;
                foreach (var pending in states.Where(item => item.Value == TaskStepRunState.Pending).Select(item => item.Key).ToArray())
                    states[pending] = TaskStepRunState.Skipped;
                continue;
            }
            if (audit is null || audit.At < at || audit.StepId is null || !states.TryGetValue(audit.StepId, out var state))
                throw new InvalidDataException();
            at = audit.At;
            if (audit.Event == "dispatch_prepared" && state == TaskStepRunState.Pending
                && !states.Values.Any(value => value is TaskStepRunState.Dispatching or TaskStepRunState.Running)
                && plan.Semantics.Steps.Single(item => item.StepId == audit.StepId).DependsOn.All(id => states[id] is TaskStepRunState.Succeeded or TaskStepRunState.NoOp))
                states[audit.StepId] = TaskStepRunState.Dispatching;
            else if (state == TaskStepRunState.Pending && audit.Event is "read_noop" or "read_failed"
                && stored.ReadResults.SingleOrDefault(item => item.StepId == audit.StepId) is { } read
                && !states.Values.Any(value => value is TaskStepRunState.Dispatching or TaskStepRunState.Running)
                && plan.Semantics.Steps.Single(item => item.StepId == audit.StepId).DependsOn.All(id => states[id] is TaskStepRunState.Succeeded or TaskStepRunState.NoOp))
            {
                var proposal = plan.Semantics.Steps.Single(item => item.StepId == audit.StepId);
                if (!ValidReadWitness(run, plan.Semantics, proposal, read)) throw new InvalidDataException();
                var outcome = ClassifyRead(proposal, read);
                if (audit.Event != (outcome == TaskStepRunState.NoOp ? "read_noop" : "read_failed")) throw new InvalidDataException();
                states[audit.StepId] = outcome;
                if (outcome == TaskStepRunState.Failed)
                    foreach (var pending in states.Where(item => item.Value == TaskStepRunState.Pending).Select(item => item.Key).ToArray())
                        states[pending] = TaskStepRunState.Skipped;
            }
            else if (audit.Event == "operation_bound" && state == TaskStepRunState.Dispatching)
                states[audit.StepId] = TaskStepRunState.Running;
            else if (audit.Event == "dispatch_cancelled" && state == TaskStepRunState.Dispatching && cancellationRequested)
                states[audit.StepId] = TaskStepRunState.Cancelled;
            else if (audit.Event == "recovery_checked" && state == TaskStepRunState.NeedsRecovery && recoveryIndex < stored.RecoveryResults.Length)
            {
                var recovery = stored.RecoveryResults[recoveryIndex++];
                var original = stored.Results.Single(item => item.StepId == audit.StepId);
                if (recovery.StepId != audit.StepId || recovery.Operation.OperationId != original.Operation.OperationId
                    || recovery.Operation.Service != original.Operation.Service || recovery.Operation.Kind != original.Operation.Kind
                    || InstallationOperationStoreActive(recovery.Operation)) throw new InvalidDataException();
                states[audit.StepId] = ClassifyRecoveryResult(plan.Semantics.Steps.Single(item => item.StepId == audit.StepId), recovery,
                    states.Values.Any(value => value == TaskStepRunState.Skipped));
            }
            else if (state == TaskStepRunState.Running && stored.Results.SingleOrDefault(item => item.StepId == audit.StepId) is { } result
                && !InstallationOperationStoreActive(result.Operation))
            {
                var outcome = ClassifyResult(plan.Semantics.Steps.Single(item => item.StepId == audit.StepId), result);
                if (audit.Event != "step_" + outcome.ToString().ToLowerInvariant()) throw new InvalidDataException();
                states[audit.StepId] = outcome;
                if (outcome != TaskStepRunState.Succeeded)
                    foreach (var pending in states.Where(item => item.Value == TaskStepRunState.Pending).Select(item => item.Key).ToArray())
                        states[pending] = TaskStepRunState.Skipped;
            }
            else throw new InvalidDataException();
        }
        var problem = run.Steps.FirstOrDefault(item => item.State is TaskStepRunState.Failed or TaskStepRunState.Cancelled or TaskStepRunState.NeedsRecovery);
        if (recoveryIndex != stored.RecoveryResults.Length || run.CancellationRequested != cancellationRequested || run.State != ResultRunState(run.Steps, cancellationRequested) || run.ProblemCode != problem?.ProblemCode)
            throw new InvalidDataException();
        foreach (var step in run.Steps)
        {
            if (step.State != states[step.StepId]) throw new InvalidDataException();
            var dispatch = stored.Dispatches.SingleOrDefault(item => item.StepId == step.StepId);
            var read = stored.ReadResults.SingleOrDefault(item => item.StepId == step.StepId);
            if (read is not null)
            {
                if (dispatch is not null || stored.Results.Any(item => item.StepId == step.StepId)
                    || step.RequestDigest is not null || step.DomainOperationId is not null
                    || step.State != ClassifyRead(plan.Semantics.Steps.Single(item => item.StepId == step.StepId), read)
                    || step.ProblemCode != (step.State == TaskStepRunState.NoOp ? null : "task.read_check_failed")) throw new InvalidDataException();
                continue;
            }
            if (step.State is TaskStepRunState.Pending or TaskStepRunState.Skipped)
            {
                if (dispatch is not null || step.RequestDigest is not null || step.DomainOperationId is not null || step.ProblemCode is not null) throw new InvalidDataException();
                continue;
            }
            if (dispatch is null || !Hash(dispatch.RequestDigest) || dispatch.RequestDigest != step.RequestDigest)
                throw new InvalidDataException();
            var expected = BuildDispatch(run, plan.Semantics.Steps.Single(item => item.StepId == step.StepId), step);
            if (Fingerprint(dispatch) != Fingerprint(expected)) throw new InvalidDataException();
            var result = stored.Results.SingleOrDefault(item => item.StepId == step.StepId);
            if (step.State == TaskStepRunState.Cancelled && step.DomainOperationId is null && cancellationRequested
                && step.ProblemCode == "task.cancelled_before_acceptance" && result is null) continue;
            if ((step.State != TaskStepRunState.Dispatching) != (step.DomainOperationId is not null) || step.DomainOperationId == Guid.Empty)
                throw new InvalidDataException();
            if (step.State is TaskStepRunState.Dispatching or TaskStepRunState.Running)
            { if (result is not null || step.ProblemCode is not null) throw new InvalidDataException(); }
            else if (result is null || result.Operation.OperationId != step.DomainOperationId || result.Operation.Service != dispatch.Service
                || result.Operation.Kind != dispatch.Kind || step.ProblemCode != ResultProblem(step.State)) throw new InvalidDataException();
        }
    }
}
