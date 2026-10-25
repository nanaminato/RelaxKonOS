using System.Collections.Immutable;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed partial class TaskPlanStore
{
    public TaskRunEvidence? GetRunEvidence(Guid runId, string subject) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, subject);
        if (stored is null) return null;
        var plan = Read(connection, transaction, stored.Record.PlanId, subject)!.Record.Plan;
        TaskStepResultEvidence Project(TaskReconciliationEvidence evidence, bool recovery)
        {
            var proposal = plan.Semantics.Steps.Single(step => step.StepId == evidence.StepId);
            var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(proposal.Inputs);
            var snapshot = evidence.Snapshot;
            // A mismatched observation is not evidence about this installation. Preserve its absence,
            // rather than exposing another host's details or rewriting the historical task result.
            if (snapshot?.Installation is not { } installation || installation.HostId != stored.Record.HostId
                || installation.RuntimeId != frozen.RuntimeId || installation.InstallationId != frozen.Selection.InstallationId
                || snapshot.Receipt is { } receipt && receipt.OperationId != evidence.Operation.OperationId) snapshot = null;
            if (snapshot?.CommitWitness is { } witness && !RuntimeCommitWitnessRules.BoundToSnapshot(witness, snapshot))
                snapshot = snapshot with { CommitWitness = null };
            return new(evidence.StepId, evidence.Operation.OperationId, evidence.Operation.State, recovery, snapshot);
        }
        var observations = stored.Results.Select(item => Project(item, false))
            .Concat(stored.RecoveryResults.Select(item => Project(item, true))).ToImmutableArray();
        transaction.Commit();
        return new TaskRunEvidence(stored.Record, observations);
    });
}
