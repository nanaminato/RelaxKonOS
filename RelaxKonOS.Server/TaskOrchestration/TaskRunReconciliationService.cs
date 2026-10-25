using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.TaskOrchestration;

public interface ITaskReconciliationDomain
{
    InstallationOperationDto? Find(Guid runId, TaskDispatchIntent dispatch, string subject);
    Task<RuntimeInstallationSnapshot> ObserveAsync(Guid operationId, CancellationToken ct);
}

/// <summary>Only reads the original domain ledger and the matching Helper receipt; never resumes a transaction.</summary>
public sealed class SingBoxTaskReconciliationDomain(InstallationCoordinator installations, SingBoxRuntimeService runtime) : ITaskReconciliationDomain
{
    public InstallationOperationDto? Find(Guid runId, TaskDispatchIntent dispatch, string subject)
    {
        var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
        var entry = installations.FindByKey(dispatch.Service, dispatch.Kind, dispatch.Request, subject, request.Plan.Request.IdempotencyKey);
        if (entry is not null && entry.TaskOwner != new InstallationTaskOwner(runId, dispatch.StepId, dispatch.RequestDigest))
            throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        return entry?.Operation;
    }
    public Task<RuntimeInstallationSnapshot> ObserveAsync(Guid operationId, CancellationToken ct) => runtime.QueryAsync(ct, operationId);
}

/// <summary>Read-only external reconciliation plus durable evidence. Does not grant or consume execution authority.</summary>
public interface ITaskRunReconciler
{
    Task<TaskRun> ReconcileAsync(Guid runId, string stepId, string subject, CancellationToken ct);
}
public sealed class TaskRunReconciliationService(TaskPlanStore store, ITaskReconciliationDomain domain) : ITaskRunReconciler
{
    public async Task<TaskRun> ReconcileAsync(Guid runId, string stepId, string subject, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = store.GetRun(runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        if (run.State is TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Cancelled or TaskRunState.NeedsRecovery) return run;
        var dispatch = store.GetDispatch(runId, stepId, subject) ?? throw new TaskPlanStoreException("task.step_not_ready", 409);
        var operation = domain.Find(runId, dispatch, subject);
        if (operation is null) return run; // An unaccepted intent stays dispatching; absence never schedules a retry here.
        run = store.BindDomainOperation(runId, stepId, subject, dispatch.RequestDigest, operation.OperationId, run.Revision);
        if (operation.State is InstallationOperationState.Queued or InstallationOperationState.Running) return run;
        RuntimeInstallationSnapshot? snapshot;
        try { snapshot = await domain.ObserveAsync(operation.OperationId, ct); }
        catch (RuntimeLifecycleException) { snapshot = null; }
        ct.ThrowIfCancellationRequested();
        return store.RecordReconciliation(runId, subject, new(stepId, operation, snapshot), run.Revision);
    }
}
