using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ModulePolicyManagementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "module-policy-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ModuleManagement:Authority"] = "managed", ["ModuleResidency:WakeTimeoutSeconds"] = "17" }).Build();
        var catalog = ServerModules.Create(configuration);
        var all = catalog.EnabledIds.Order(StringComparer.Ordinal).ToArray();
        var initial = new ServerModuleResidencyPolicyDto("IdleDelayed", 300);
        var target = new ServerModuleResidencyPolicyDto("KeepLoaded", 42);
        var facts = all.Select(id => new ServerModuleDto(id, true, [], [], [], [],
            ResourceReleaseLevel: id == ServerModules.GitId ? "objects" : "none",
            DesiredResidencyPolicy: id == ServerModules.GitId ? initial : null,
            EffectiveResidencyPolicy: id == ServerModules.GitId ? initial : null)).ToArray();
        try
        {
            Guid operationId;
            using (var store = new ModuleManagementStore(configuration, root))
            {
                ServerModulePlanRequest Request(string key, string strategy = "KeepLoaded", int grace = 42) => new(store.ServiceId,
                    ServerModuleAction.SetResidencyPolicy, [ServerModules.GitId], all, store.Revision, key,
                    Strategy: strategy, IdleGraceSeconds: grace);
                var request = Request("policy");
                var plan = store.Preview("actor", request, facts);
                var stale = store.Preview("actor", Request("stale"), facts);
                check(plan.BlockedBy.Count == 0 && plan.RestartRequired, "policy preview binds explicit strategy and grace with restart impact");
                var operation = store.Apply("actor", plan.PlanId, facts);
                operationId = operation.OperationId;
                check(operation.State == ServerModuleOperationState.AwaitingRestart && store.CaptureInventory().Policies[ServerModules.GitId] == target
                    && store.EffectivePolicies[ServerModules.GitId] == initial,
                    "policy apply atomically persists desired settings while leaving effective policy unchanged");
                check(store.Apply("actor", plan.PlanId, facts).OperationId == operationId, "policy apply is idempotent through the original plan");
                try { store.Apply("actor", stale.PlanId, facts); throw new Exception("Accepted stale policy plan"); }
                catch (ModuleManagementException exception) when (exception.Code == "module-revision-conflict")
                { check(true, "policy changes invalidate older configuration plans"); }
                check(store.Preview("actor", Request("same-pending"), facts).RestartRequired,
                    "unchanged desired policy remains pending when its effective startup snapshot differs");
                check(store.Preview("actor", Request("invalid", "IdleDelayed", 604801), facts).BlockedBy.Any(x => x.ProblemCode == "module-residency-policy-invalid"),
                    "policy preview rejects out-of-range grace periods");
                var unsupported = Request("unsupported") with { ModuleIds = [ServerModules.TerminalId] };
                check(store.Preview("actor", unsupported, facts).BlockedBy.Any(x => x.ProblemCode == "module-action-unsupported"),
                    "policy changes cannot create a release boundary for a resident module");
            }
            using (var restarted = new ModuleManagementStore(configuration, root))
            {
                var options = ModuleResidencyOptions.Parse(configuration, catalog.KnownModuleIds, catalog.KnownReleasableModuleIds)
                    .WithPolicies(restarted.EffectivePolicies, catalog.KnownReleasableModuleIds);
                check(options.For(ServerModules.GitId) is { Strategy: ModuleResidencyStrategy.KeepLoaded }
                    && options.For(ServerModules.GitId).IdleGracePeriod.TotalSeconds == 42 && options.For(ServerModules.GitId).WakeTimeout.TotalSeconds == 17,
                    "startup applies the exact managed policy while preserving deployment wake limits");
                try
                {
                    options.WithPolicies(new Dictionary<string, ServerModuleResidencyPolicyDto> { [ServerModules.GitId] = new("1", 42) }, catalog.KnownReleasableModuleIds);
                    throw new Exception("Accepted numeric strategy alias");
                }
                catch (InvalidOperationException) { check(true, "managed policies accept canonical strategy names without numeric aliases"); }
                restarted.VerifyStartup(restarted.DesiredIds);
                check(restarted.Operation("actor", operationId).State == ServerModuleOperationState.Succeeded,
                    "policy operation succeeds only after the startup policy snapshot matches its target");
            }
            foreach (var key in new[] { "ModuleResidency:Strategy", "ModuleResidency:Modules:relaxkonos.git:IdleGraceSeconds" })
            {
                var conflict = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ModuleManagement:Authority"] = "managed", [key] = key.EndsWith("Strategy") ? "KeepLoaded" : "12" }).Build();
                try { using var _ = new ModuleManagementStore(conflict, Path.Combine(root, Guid.NewGuid().ToString("N"))); throw new Exception("Accepted competing policy authority"); }
                catch (InvalidOperationException) { check(true, "managed policy authority rejects deployment override " + key); }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
