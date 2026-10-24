using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.TaskOrchestration;

public interface IRuntimeTaskChangePlanningService
{
    Task<TaskPlan> PlanAsync(string subject, TaskPlanIntent intent, CancellationToken ct);
}

/// <summary>Produces a durable proposal only. No ledger submission, elevation or execution is performed.</summary>
public sealed class RuntimeTaskChangePlanningService(SingBoxRuntimeService runtime, RuntimeCatalogService catalogs) : IRuntimeTaskChangePlanningService
{
    public async Task<TaskPlan> PlanAsync(string subject, TaskPlanIntent intent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512 || subject.Any(char.IsControl))
            throw new TaskPlanningException("task.intent_invalid");
        var domain = new Domain(runtime, catalogs);
        var planner = new TaskPlanner(RuntimeReadPlanningActions.Create(domain).Concat(RuntimeChangePlanningActions.Create(domain)));
        planner.ValidateIntent(intent);
        var installation = await domain.InstallationAsync(ct);
        if (installation.Installation.RuntimeId != "sing-box")
            throw new TaskPlanningException("task.observation_identity_mismatch");
        return await planner.PlanAsync(new(installation.Installation.HostId, subject), intent, ct);
    }

    private sealed class Domain(SingBoxRuntimeService runtime, RuntimeCatalogService catalogs) : IRuntimeTaskChangePlanner, IRuntimeTaskObservations
    {
        private RuntimeInstallationSnapshot? _installation;
        private readonly Dictionary<string, RuntimeCatalogSnapshot> _catalogs = new(StringComparer.Ordinal);
        public async Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct) =>
            _installation ??= await runtime.QueryAsync(ct);
        public async Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct)
        {
            var baseline = await InstallationAsync(ct);
            var result = await runtime.PlanAsync(sourceId, request, ct);
            if (result.Allowed)
            {
                var current = await runtime.QueryAsync(ct);
                if (current.Installation != baseline.Installation || current.ActiveRevisionId != baseline.ActiveRevisionId
                    || current.ConfigurationIdentity != baseline.ConfigurationIdentity || current.MeasuredVersion != baseline.MeasuredVersion
                    || current.PendingOperationId != baseline.PendingOperationId)
                    throw new TaskPlanningException("task.observation_changed");
            }
            return result;
        }
        public async Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, CancellationToken ct)
        {
            if (_catalogs.TryGetValue(sourceId, out var snapshot)) return snapshot;
            return _catalogs[sourceId] = await catalogs.CatalogAsync(sourceId, refresh: false, includePrerelease: false, ct);
        }
    }
}
