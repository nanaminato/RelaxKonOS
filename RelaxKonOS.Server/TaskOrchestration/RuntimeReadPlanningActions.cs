using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Trusted read providers only; not a general API router or an installation capability.</summary>
public interface IRuntimeTaskObservations
{
    Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct);
    Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, CancellationToken ct);
}

/// <summary>First compiled Action Catalog: sing-box read checks. Neither action freezes a runtime change.</summary>
public static class RuntimeReadPlanningActions
{
    public static IReadOnlyList<ITaskPlanningAction> Create(IRuntimeTaskObservations observations,
        TimeProvider? clock = null) => [new InstallationCheck(observations), new CatalogCheck(observations, clock ?? TimeProvider.System)];

    private static T Parse<T>(JsonElement value)
    {
        try { return value.Deserialize<T>() ?? throw new JsonException(); }
        catch (JsonException) { throw new TaskPlanningException("task.input_invalid"); }
    }

    private sealed class InstallationCheck(IRuntimeTaskObservations observations) : ITaskPlanningAction
    {
        public string ActionId => "runtime.verifyInstallation";
        public int SchemaVersion => 1;
        public string ImplementationRevision => "sing-box-read-1";
        public bool IsReadOnly => true;
        public IReadOnlyList<string> WriteResources => [];
        public void ValidateInputs(JsonElement inputs)
        {
            var value = Parse<VerifyRuntimeInstallationInput>(inputs);
            if (value.InstallationId == Guid.Empty || value.ExpectedStateRevision < 0)
                throw new TaskPlanningException("task.input_invalid");
        }
        public async Task<TaskActionObservation> ObserveAsync(TaskPlanningContext context, JsonElement inputs, CancellationToken ct)
        {
            var expected = Parse<VerifyRuntimeInstallationInput>(inputs);
            var observed = await observations.InstallationAsync(ct);
            var state = observed.Installation;
            if (state.HostId != context.HostId || state.RuntimeId != "sing-box")
                throw new TaskPlanningException("task.observation_identity_mismatch");
            var problems = new List<string>();
            if (state.InstallationId != expected.InstallationId || state.StateRevision != expected.ExpectedStateRevision)
                problems.Add("task.baseline_changed");
            if (state.Ownership != RuntimeOwnership.Managed) problems.Add("runtime.not_managed");
            if (state.RecoveryRequired) problems.Add("runtime.recovery_required");
            if (observed.PendingOperationId is not null) problems.Add("runtime.operation_pending");
            return new(problems.Count == 0 ? TaskStepDisposition.NoOp : TaskStepDisposition.Conflict,
                problems, ["runtime:sing-box"], [new("runtime:sing-box", state.InstallationId.ToString("D"), state.StateRevision)]);
        }
    }

    private sealed class CatalogCheck(IRuntimeTaskObservations observations, TimeProvider clock) : ITaskPlanningAction
    {
        public string ActionId => "runtime.verifyCatalog";
        public int SchemaVersion => 1;
        public string ImplementationRevision => "sing-box-read-1";
        public bool IsReadOnly => true;
        public IReadOnlyList<string> WriteResources => [];
        public void ValidateInputs(JsonElement inputs)
        {
            var value = Parse<VerifyRuntimeCatalogInput>(inputs);
            if (string.IsNullOrWhiteSpace(value.SourceId) || value.SourceId.Length > 128
                || value.SourceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                || value.ExpectedCatalogRevision < 0 || value.ExpectedTrustRevision < 0)
                throw new TaskPlanningException("task.input_invalid");
        }
        public async Task<TaskActionObservation> ObserveAsync(TaskPlanningContext context, JsonElement inputs, CancellationToken ct)
        {
            var expected = Parse<VerifyRuntimeCatalogInput>(inputs);
            var snapshot = await observations.CatalogAsync(expected.SourceId, ct);
            if (snapshot.Source.Identity.SourceId != expected.SourceId || snapshot.Source.RuntimeId != "sing-box")
                throw new TaskPlanningException("task.observation_identity_mismatch");
            var source = snapshot.Source.Identity;
            var catalog = snapshot.Catalog;
            var problems = new List<string>();
            if (!source.Enabled || source.Trust != RuntimeTrust.Confirmed) problems.Add("runtime.source_untrusted");
            if (!snapshot.Fresh || catalog is null) problems.Add("runtime.catalog_missing");
            if (source.TrustRevision != expected.ExpectedTrustRevision) problems.Add("runtime.trust_revision_conflict");
            if (catalog is not null)
            {
                if (catalog.SourceId != source.SourceId || catalog.TrustRevision != source.TrustRevision)
                    problems.Add("runtime.source_mismatch");
                if (catalog.Revision != expected.ExpectedCatalogRevision || catalog.Revision < snapshot.Source.CatalogRevision)
                    problems.Add("runtime.catalog_revision_conflict");
                if (!catalog.SignatureVerified) problems.Add("runtime.catalog_unverified");
                if (catalog.ExpiresAt <= clock.GetUtcNow()) problems.Add("runtime.catalog_expired");
            }
            var key = "catalog:sing-box:" + source.SourceId;
            return new(problems.Count == 0 ? TaskStepDisposition.NoOp : TaskStepDisposition.Conflict,
                problems, [key], [new(key, source.SourceId, catalog?.Revision ?? 0),
                    new("trust:sing-box:" + source.SourceId, source.SourceId, source.TrustRevision)]);
        }
    }
}
