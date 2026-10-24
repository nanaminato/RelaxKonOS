using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.TaskOrchestration;

public interface IRuntimeTaskChangePlanner
{
    Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct);
    Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct);
}

/// <summary>Only lifecycle planning; Helper remains responsible for domain admission and no execution is reachable.</summary>
public static class RuntimeChangePlanningActions
{
    // The key is a planning probe only and is deliberately excluded from frozen execution semantics.
    public const string PlanningProbeKey = "task-planning-probe";
    public const string ImplementationRevision = "sing-box-change-1";
    public static IReadOnlyList<ITaskPlanningAction> Create(IRuntimeTaskChangePlanner domain) =>
        Enum.GetValues<RuntimeChange>().Where(change => change != RuntimeChange.Configure)
            .Select(change => (ITaskPlanningAction)new Action(domain, change)).ToArray();

    public static string ActionId(RuntimeChange change) => change switch
    {
        RuntimeChange.Install => "runtime.install", RuntimeChange.Update => "runtime.update",
        RuntimeChange.Repair => "runtime.repair", RuntimeChange.Rollback => "runtime.rollback",
        RuntimeChange.Uninstall => "runtime.uninstall", _ => throw new ArgumentOutOfRangeException(nameof(change))
    };

    private sealed class Action(IRuntimeTaskChangePlanner domain, RuntimeChange change) : ITaskPlanningAction
    {
        public string ActionId => RuntimeChangePlanningActions.ActionId(change);
        public int SchemaVersion => 1;
        public string ImplementationRevision => RuntimeChangePlanningActions.ImplementationRevision;
        public bool IsReadOnly => false;
        public IReadOnlyList<string> WriteResources => SingBoxInstallationService.ResourceKeys;

        private static RuntimeTaskChangeInput Parse(JsonElement inputs)
        {
            try { return inputs.Deserialize<RuntimeTaskChangeInput>() ?? throw new JsonException(); }
            catch (JsonException) { throw new TaskPlanningException("task.input_invalid"); }
        }

        public void ValidateInputs(JsonElement inputs)
        {
            var input = Parse(inputs);
            if (input.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(input.HostId) || input.HostId.Length > 512
                || input.HostId.Any(char.IsControl) || input.ExpectedStateRevision < 0 || input.ExpectedCatalogRevision < 0
                || input.ExpectedTrustRevision < 0
                || input.SourceId is { } source && !RuntimeValidation.Identifier(source)
                || change == RuntimeChange.Uninstall && (input.ReleaseId is not null || input.SourceId is not null || input.AllowPrerelease
                    || input.ExpectedCatalogRevision != 0 || input.ExpectedTrustRevision != 0)
                || change != RuntimeChange.Uninstall && (!RuntimeValidation.Identifier(input.SourceId)
                    || string.IsNullOrWhiteSpace(input.ReleaseId) || input.ReleaseId.Length > 256
                    || input.ReleaseId.Any(char.IsControl) || !input.KeepData))
                throw new TaskPlanningException("task.input_invalid");
        }

        public async Task<TaskActionObservation> ObserveAsync(TaskPlanningContext context, JsonElement inputs, CancellationToken ct)
        {
            var input = Parse(inputs);
            var observed = await domain.InstallationAsync(ct);
            var state = observed.Installation;
            if (context.HostId != state.HostId || state.RuntimeId != "sing-box")
                throw new TaskPlanningException("task.observation_identity_mismatch");
            var problems = new List<string>();
            if (input.HostId != state.HostId || input.InstallationId != state.InstallationId
                || input.ExpectedStateRevision != state.StateRevision) problems.Add("runtime.state_revision_conflict");
            if (state.Ownership is RuntimeOwnership.Unknown or RuntimeOwnership.External)
                problems.Add(state.Ownership == RuntimeOwnership.External ? "runtime.external_installation" : "runtime.state_unknown");
            if (state.RecoveryRequired) problems.Add("runtime.recovery_required");
            if (observed.PendingOperationId is not null) problems.Add("runtime.operation_pending");
            if (change == RuntimeChange.Install && state.Ownership == RuntimeOwnership.Managed) problems.Add("runtime.already_installed");
            if (change != RuntimeChange.Install && state.Ownership == RuntimeOwnership.Absent) problems.Add("runtime.not_managed");
            if (change == RuntimeChange.Repair && input.ReleaseId != state.ActiveReleaseId) problems.Add("runtime.repair_target_mismatch");
            if (change == RuntimeChange.Rollback && !((observed.InstalledReleases ?? []).Any(item => item.ReleaseId == input.ReleaseId)))
                problems.Add("runtime.rollback_unavailable");
            RuntimePlanResult? result = null;
            if (problems.Count == 0)
            {
                var request = new RuntimePlanRequest(input.InstallationId, input.HostId, change, input.ExpectedStateRevision,
                    input.ExpectedCatalogRevision, input.ExpectedTrustRevision, input.ReleaseId, PlanningProbeKey,
                    input.AllowPrerelease, input.KeepData);
                result = await domain.PlanAsync(input.SourceId, request, ct);
                if (result.Problems is null || result.Problems.Any(problem => problem is null || problem.Length > 120
                    || !problem.StartsWith("runtime.", StringComparison.Ordinal)
                    || problem.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('_' or '.'))))
                    throw new TaskPlanningException("task.domain_plan_mismatch");
                if (result.Plan is { } plan)
                {
                    if (plan.Request != request || plan.RuntimeId != "sing-box" || plan.PreviousReleaseId != state.ActiveReleaseId
                        || change == RuntimeChange.Uninstall && plan.Release is not null
                        || change != RuntimeChange.Uninstall && (plan.Release is not { RuntimeId: "sing-box" } release
                            || release.ReleaseId != input.ReleaseId || release.SourceId != input.SourceId || release.Target != state.Target))
                        throw new TaskPlanningException("task.domain_plan_mismatch");
                    if (change != RuntimeChange.Uninstall && (!plan.Release!.ConfigurationSupported
                        || string.IsNullOrWhiteSpace(plan.Release.ConfigurationCapability) || plan.Release.Trust != RuntimeTrust.Confirmed
                        || plan.Release.Artifact is not { } artifact || !RuntimeArchiveRules.Valid(artifact)
                        || artifact.MaximumBytes is <= 0 or > 2L * 1024 * 1024 * 1024
                        || artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(Uri.IsHexDigit)
                        || string.IsNullOrWhiteSpace(artifact.VerificationIdentity)
                        || result.Dependencies is not { Observations: not null } dependencies
                        || dependencies.Observations.Any(item => item is null)
                        || !dependencies.Satisfies(RuntimeArchiveDependencies.For(plan.Release))))
                        throw new TaskPlanningException("task.domain_plan_mismatch");
                    if (change is RuntimeChange.Update or RuntimeChange.Rollback)
                    {
                        if (!RuntimeSemanticVersion.TryParse(observed.MeasuredVersion, out var current)
                            || !RuntimeSemanticVersion.TryParse(plan.Release!.SortVersion, out var target))
                            problems.Add("runtime.version_invalid");
                        else if (change == RuntimeChange.Update ? target.CompareTo(current) <= 0 : target.CompareTo(current) >= 0)
                            problems.Add("runtime.target_unchanged");
                    }
                }
                if (!result.Allowed) problems.AddRange(result.Problems.Count > 0 ? result.Problems : ["runtime.plan_blocked"]);
            }
            var impact = new RuntimeTaskChangeImpact(change is RuntimeChange.Update or RuntimeChange.Repair or RuntimeChange.Rollback,
                change == RuntimeChange.Uninstall, change == RuntimeChange.Uninstall,
                input.KeepData, change == RuntimeChange.Uninstall && !input.KeepData, false);
            var frozen = new RuntimeTaskFrozenChange("sing-box", change, input, result?.Plan?.Release,
                state.ActiveReleaseId, observed.MeasuredVersion, observed.ConfigurationIdentity, impact, result?.Dependencies);
            var baselines = new List<TaskResourceBaseline>
            { new("runtime:sing-box", state.InstallationId.ToString("D"), state.StateRevision) };
            if (change != RuntimeChange.Uninstall && result?.Allowed == true && input.SourceId is { } sourceId)
            {
                baselines.Add(new("catalog:sing-box:" + sourceId, sourceId, input.ExpectedCatalogRevision));
                baselines.Add(new("trust:sing-box:" + sourceId, sourceId, input.ExpectedTrustRevision));
            }
            return new(problems.Count == 0 ? TaskStepDisposition.Ready : TaskStepDisposition.Conflict,
                problems, WriteResources, baselines, TaskPlanJson.FreezeInputs(frozen),
                [new(InstallationEndpoints.Capability(InstallationServiceId.SingBox).ToString(), InstallationEndpoints.Target(InstallationServiceId.SingBox))],
                impact.DeletesBusinessData ? "destructive" : "host-management", "domain-idempotent", "domain-transaction");
        }
    }
}
