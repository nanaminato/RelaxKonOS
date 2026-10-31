using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup;

internal static class ModuleManagementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "module-management-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ModuleManagement:Authority"] = "managed" }).Build();
        static void Error(Action action, string code)
        {
            try { action(); }
            catch (ModuleManagementException ex) when (ex.Code == code) { return; }
            throw new Exception("Expected " + code);
        }
        var inventory = new[]
        {
            new ServerModuleDto("base", true, [], [], ["dependent"], []),
            new ServerModuleDto("dependent", true, [], ["base"], [], []),
            new ServerModuleDto("durable", true, [], [], [], ["unresolved"]),
        };
        // The store is tested with the real compiled set; synthetic members below only exercise
        // dependency admission and do not become a startup module registration.
        try
        {
            Guid planId; Guid operationId; string service;
            using (var store = new ModuleManagementStore(configuration, root))
            {
                service = store.ServiceId;
                var all = ServerModules.Create(configuration).EnabledIds.Order(StringComparer.Ordinal).ToArray();
                var facts = all.Select(id => new ServerModuleDto(id, true, [], [], [], [])).ToArray();
                var target = all.Where(id => id != ServerModules.GitId).ToArray();
                var request = new ServerModulePlanRequest(service, ServerModuleAction.Disable, [ServerModules.GitId], target, store.Revision, "one");
                var plan = store.Preview("actor", request, facts);
                planId = plan.PlanId;
                check(plan.RestartRequired && plan.BlockedBy.Count == 0, "management exact-set preview freezes restart impact");
                check(store.Preview("actor", request, facts).PlanId == planId, "management preview is idempotent");
                Error(() => store.Preview("actor", request with { ModuleIds = [ServerModules.ProxyId] }, facts), "module-idempotency-conflict");
                Error(() => store.Apply("other", planId, facts), "module-plan-not-found");
                var stale = store.Preview("actor", request with { IdempotencyKey = "stale" }, facts);
                var operation = store.Apply("actor", planId, facts);
                foreach (var wire in new[] { "0", "\"0\"", "\"disable\"" })
                {
                    var invalidWire = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default))!.AsObject();
                    invalidWire["action"] = System.Text.Json.Nodes.JsonNode.Parse(wire);
                    try { System.Text.Json.JsonSerializer.Deserialize<ServerModulePlanRequest>(invalidWire.ToJsonString(), RelaxKonOSJsonOptions.Default); throw new Exception("Accepted enum alias"); }
                    catch (System.Text.Json.JsonException) { check(true, "module action wire rejects numeric or noncanonical alias " + wire); }
                }
                operationId = operation.OperationId;
                check(operation.State == ServerModuleOperationState.AwaitingRestart && !store.DesiredIds.Contains(ServerModules.GitId),
                    "management atomically persists desired set and awaiting-restart receipt");
                check(store.Apply("actor", planId, facts).OperationId == operationId, "management lost apply reply recovers the original receipt");
                Error(() => store.Apply("actor", stale.PlanId, facts), "module-revision-conflict");
                check(store.Operations("other", null, 0, 25).Items.Count == 0, "management history isolates actors");
                check(store.Operations("actor", ServerModules.GitId, 0, 1).Items.Count == 1, "management history filters by module");
                var invalid = new ServerModulePlanRequest(service, ServerModuleAction.Disable, ["base"], ["dependent"], store.Revision, "dependency");
                var rejected = store.Preview("actor", invalid, inventory);
                check(rejected.BlockedBy.Any(x => x.ProblemCode == "module-dependency-blocked")
                    && rejected.BlockedBy.Any(x => x.ProblemCode == "module-recovery-blocked"), "management dependency and durable work block invalid exact sets");
            }
            using (var restarted = new ModuleManagementStore(configuration, root))
            {
                check(restarted.ServiceId == service && restarted.Operation("actor", operationId).PlanId == planId,
                    "management identity and operation survive a host restart");
                restarted.VerifyStartup(restarted.DesiredIds);
                check(restarted.Operation("actor", operationId).State == ServerModuleOperationState.Succeeded,
                    "management verifies original effective target after restart");
                File.AppendAllText(Path.Combine(root, "data", "module-management", "state.json"), " ");
                Error(() => _ = restarted.Revision, "module-revision-conflict");
                check(true, "management external state edits invalidate running-process previews");
            }
            var readonlyRoot = Path.Combine(root, "readonly");
            using var readOnly = new ModuleManagementStore(new ConfigurationBuilder().Build(), readonlyRoot);
            check(readOnly.Admissions().All(x => !x.Allowed), "deployment-owned configuration exposes no writable action");
            var conflicting = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ModuleManagement:Authority"] = "managed", ["Modules:Git:Enabled"] = "false" }).Build();
            try { using var _ = new ModuleManagementStore(conflicting, Path.Combine(root, "conflict")); throw new Exception("Accepted competing authorities"); }
            catch (InvalidOperationException) { check(true, "management refuses competing configuration authorities"); }
        }
        finally { Directory.Delete(root, true); }
    }
}
