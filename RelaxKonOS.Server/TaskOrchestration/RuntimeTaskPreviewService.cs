using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Request-local read snapshot. Preview never refreshes sources or invokes a runtime plan.</summary>
public sealed class RuntimeTaskPreviewService(SingBoxRuntimeService runtime, RuntimeCatalogService catalogs)
    : IRuntimeTaskPreviewService
{
    public async Task<TaskPlan> PreviewAsync(string subject, TaskPlanIntent intent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512 || subject.Any(char.IsControl))
            throw new TaskPlanningException("task.intent_invalid");
        var observations = new Observations(runtime, catalogs);
        var planner = new TaskPlanner(RuntimeReadPlanningActions.Create(observations));
        planner.ValidateIntent(intent); // No Helper access for malformed graphs or schemas.
        var installation = await observations.InstallationAsync(ct);
        if (installation.Installation.RuntimeId != "sing-box")
            throw new TaskPlanningException("task.observation_identity_mismatch");
        return await planner.PlanAsync(new(installation.Installation.HostId, subject), intent, ct);
    }

    private sealed class Observations(SingBoxRuntimeService runtime, RuntimeCatalogService catalogs) : IRuntimeTaskObservations
    {
        private RuntimeInstallationSnapshot? _installation;
        private readonly Dictionary<string, RuntimeCatalogSnapshot> _catalogs = new(StringComparer.Ordinal);
        public async Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct) =>
            _installation ??= await runtime.QueryAsync(ct);
        public async Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, CancellationToken ct)
        {
            if (_catalogs.TryGetValue(sourceId, out var snapshot)) return snapshot;
            return _catalogs[sourceId] = await catalogs.CatalogAsync(sourceId, refresh: false, includePrerelease: false, ct);
        }
    }
}

public interface IRuntimeTaskPreviewService
{
    Task<TaskPlan> PreviewAsync(string subject, TaskPlanIntent intent, CancellationToken ct);
}
