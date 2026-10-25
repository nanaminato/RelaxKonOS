using System.Collections.Immutable;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed record TaskReadStepEvidence(string StepId, TaskPlan Observation);

public sealed partial class TaskPlanStore
{
    public TaskRun RecordReadStep(Guid runId, TaskExecutionIdentity identity, TaskReadStepEvidence evidence, long expectedRevision) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, identity.CanonicalSubject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var approval = ReadApproval(connection, transaction, run.ApprovalId, identity.CanonicalSubject)!.Record;
        RequireSameIdentity(approval, identity);
        if (approval.State != TaskApprovalState.Consumed || approval.RunAuthorizationExpiresAt <= _clock.GetUtcNow())
            throw new TaskPlanStoreException("task.approval_unavailable", 403);
        var plan = Read(connection, transaction, run.PlanId, identity.CanonicalSubject)!.Record;
        if (plan.Validity != TaskPlanValidity.Resolved) throw new TaskPlanStoreException("task.plan_expired", 409);
        var proposal = plan.Plan.Semantics.Steps.SingleOrDefault(item => item.StepId == evidence.StepId)
            ?? throw new TaskPlanStoreException("task.step_not_found", 404);
        if (!ValidReadWitness(run, plan.Plan.Semantics, proposal, evidence)) throw new TaskPlanStoreException("task.observation_invalid", 409);
        if (stored.ReadResults.SingleOrDefault(item => item.StepId == evidence.StepId) is { } previous)
            return Fingerprint(previous.Observation.Semantics) == Fingerprint(evidence.Observation.Semantics)
                ? run : throw new TaskPlanStoreException("task.result_conflict", 409);
        var step = run.Steps.Single(item => item.StepId == evidence.StepId);
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.State is not (TaskRunState.Queued or TaskRunState.Running) || step.State != TaskStepRunState.Pending || run.CancellationRequested
            || run.Steps.Any(item => item.State is TaskStepRunState.Dispatching or TaskStepRunState.Running)
            || proposal.DependsOn.Any(id => run.Steps.Single(item => item.StepId == id).State is not (TaskStepRunState.Succeeded or TaskStepRunState.NoOp)))
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        var outcome = ClassifyRead(proposal, evidence);
        var steps = run.Steps.Select(item => item.StepId == step.StepId ? item with { State = outcome,
                ProblemCode = outcome == TaskStepRunState.NoOp ? null : "task.read_check_failed" }
            : outcome == TaskStepRunState.Failed && item.State == TaskStepRunState.Pending ? item with { State = TaskStepRunState.Skipped } : item).ToImmutableArray();
        var changed = stored with
        {
            Record = run with { Revision = run.Revision + 1, Steps = steps, State = ResultRunState(steps),
                ProblemCode = outcome == TaskStepRunState.NoOp ? null : "task.read_check_failed" },
            ReadResults = [.. stored.ReadResults, evidence],
            Audit = [.. stored.Audit, new(outcome == TaskStepRunState.NoOp ? "read_noop" : "read_failed", evidence.StepId, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.ReadResultWritten);
        transaction.Commit();
        return changed.Record;
    });

    private static bool ValidReadWitness(TaskRun run, TaskPlanSemantics expected, TaskStep proposal, TaskReadStepEvidence evidence)
    {
        var observation = evidence.Observation;
        if (observation?.Semantics is not { Steps.Length: 1 } semantics || observation.PlanId == Guid.Empty
            || semantics.HostId != run.HostId || semantics.CanonicalSubject != run.CanonicalSubject
            || observation.CreatedAt < run.CreatedAt || semantics.SourceKind != expected.SourceKind
            || semantics.SourceIdentity != expected.SourceIdentity || semantics.InputRevision != expected.InputRevision
            || observation.PlanDigest != TaskPlanCanonicalizer.Digest(semantics) || proposal.RiskClass != "read-only"
            || proposal.RequiredCapabilities.Length != 0 || proposal.SchemaVersion != 1 || proposal.ImplementationRevision != "sing-box-read-1"
            || proposal.ActionId is not ("runtime.verifyInstallation" or "runtime.verifyCatalog")) return false;
        var step = semantics.Steps[0];
        return step.StepId == proposal.StepId && step.ActionId == proposal.ActionId && step.SchemaVersion == proposal.SchemaVersion
            && step.ImplementationRevision == proposal.ImplementationRevision && step.RiskClass == "read-only" && step.RequiredCapabilities.IsEmpty
            && step.Disposition is TaskStepDisposition.NoOp or TaskStepDisposition.Conflict && step.DependsOn.IsEmpty
            && Fingerprint(step.Inputs) == Fingerprint(proposal.Inputs);
    }
    private static TaskStepRunState ClassifyRead(TaskStep proposal, TaskReadStepEvidence evidence) =>
        evidence.Observation.Semantics.Steps[0].Disposition == TaskStepDisposition.NoOp
        && Fingerprint(proposal) == Fingerprint(evidence.Observation.Semantics.Steps[0] with { DependsOn = proposal.DependsOn })
            ? TaskStepRunState.NoOp : TaskStepRunState.Failed;
}
