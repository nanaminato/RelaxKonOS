using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

internal sealed partial class ModuleManagementStore
{
    internal static bool IsRuntimeAction(ServerModuleAction action) => action is ServerModuleAction.Wake or ServerModuleAction.TrySleep;
    internal (ServerModuleOperationDto Operation, ServerModulePlanDto Plan, bool Created) BeginRuntime(
        string actor, Guid planId, IReadOnlyList<ServerModuleDto> inventory)
    {
        lock (_gate)
        {
            ReadCurrent();
            var saved = _document.Plans.SingleOrDefault(x => x.Actor == actor && x.Plan.PlanId == planId)
                ?? throw new ModuleManagementException("module-plan-not-found", 404);
            if (saved.OperationId is { } existing) return (_document.Operations.Single(x => x.OperationId == existing), saved.Plan, false);
            var plan = saved.Plan;
            if (!IsRuntimeAction(plan.Target.Action)) throw new ModuleManagementException("module-request-invalid", 400);
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new ModuleManagementException("module-plan-expired");
            if (plan.ExpectedRevision != _document.Revision) throw new ModuleManagementException("module-revision-conflict");
            var blockers = plan.BlockedBy.Concat(Validate(plan.Target, inventory)).ToArray();
            if (blockers.Length != 0) throw new ModuleManagementException(blockers[0].ProblemCode);
            var now = DateTimeOffset.UtcNow;
            var operation = new ServerModuleOperationDto(Guid.NewGuid(), planId, ServiceId, ServerModuleOperationState.Prepared,
                now, now, _document.Revision, _document.EnabledIds);
            Commit(_document with { Plans = _document.Plans.Select(x => x == saved ? x with { OperationId = operation.OperationId } : x).ToList(),
                Operations = [.. _document.Operations, operation] });
            return (operation, plan, true);
        }
    }

    internal void UpdateRuntime(Guid operationId, ServerModuleOperationState state, string? problemCode = null,
        IReadOnlyList<ServerModuleActionResult>? results = null)
    {
        lock (_gate)
        {
            ReadCurrent();
            var current = _document.Operations.Single(x => x.OperationId == operationId);
            if (current.State is not (ServerModuleOperationState.Prepared or ServerModuleOperationState.Applying))
                throw new ModuleManagementException("module-operation-state-conflict");
            var next = current with { State = state, UpdatedAt = DateTimeOffset.UtcNow, ProblemCode = problemCode,
                ModuleResults = results?.ToArray() ?? current.ModuleResults };
            Commit(_document with { Operations = _document.Operations.Select(x => x.OperationId == operationId ? next : x).ToList() });
        }
    }
}
