using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>Historical host observations retained by reconciliation. Reading never observes or executes new work.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskStepResultEvidence(string StepId, Guid OperationId, InstallationOperationState OperationState,
    bool RecoveryCheck, RuntimeInstallationSnapshot? Snapshot);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRunEvidence(TaskRun Run, ImmutableArray<TaskStepResultEvidence> Observations);
