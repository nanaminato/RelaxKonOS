using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Endpoints;

internal static class ModuleRemovalChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "module-removal-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ModuleManagement:Authority"] = "managed" }).Build();
        try
        {
            Guid operationId;
            using (var store = new ModuleManagementStore(configuration, root))
            {
                var package = new InstalledModuleRecord("sample", "1.0.0", "sample-1", "official", "publisher",
                    new string('a', 64), new string('b', 64), "packages/sample/1.0.0", "server/sample.dll",
                    "Sample.Entry", [], [], "module-host", DateTimeOffset.UtcNow);
                InstalledModuleStore.Write(root, [package]);
                var packageRoot = Path.Combine(root, "data/module-management/packages/sample/1.0.0");
                Directory.CreateDirectory(packageRoot);
                File.WriteAllText(Path.Combine(packageRoot, "payload"), "generated-package");
                var data = Path.Combine(root, "data/module-data/sample/accounts.json");
                Directory.CreateDirectory(Path.GetDirectoryName(data)!);
                File.WriteAllText(data, "generated-account-state");
                var installed = new ServerModuleDto("sample", false, [], [], [], [], DesiredEnabled: false, Packaging: "independent");
                var facts = store.DesiredIds.Select(id => new ServerModuleDto(id, true, [], [], [], [])).Append(installed).ToArray();
                ServerModulePlanDto Preview(string key, IReadOnlyList<ServerModuleDto>? inventory = null) => store.Preview("owner",
                    new(store.ServiceId, ServerModuleAction.Remove, ["sample"], store.DesiredIds.ToArray(), store.Revision, key), inventory ?? facts);
                var running = facts.Select(item => item.Id == "sample" ? item with { EffectiveEnabled = true } : item).ToArray();
                check(Preview("running", running).BlockedBy.Any(item => item.ProblemCode == "module-remove-restart-required"),
                    "removal refuses a package still registered in the running process");
                var dependent = facts.Append(new ServerModuleDto("consumer", false, [], ["sample"], [], [], DesiredEnabled: false)).ToArray();
                check(Preview("dependent", dependent).BlockedBy.Any(item => item.ProblemCode == "module-dependency-blocked"),
                    "removal rejects even disabled installed dependents");
                var bundled = facts.Select(item => item.Id == "sample" ? item with { Packaging = "bundled" } : item).ToArray();
                check(Preview("bundled", bundled).BlockedBy.Any(item => item.ProblemCode == "module-bundled-not-removable"),
                    "removal cannot remove bundled modules");
                var frozen = Preview("frozen");
                InstalledModuleStore.Write(root, [package with { ArtifactSha256 = new string('c', 64) }]);
                try { store.Apply("owner", frozen.PlanId, facts); throw new Exception("Changed package accepted"); }
                catch (ModuleManagementException error) when (error.Code == "module-package-plan-target-changed")
                { check(true, "removal freezes the exact installation fact across preview and apply"); }
                InstalledModuleStore.Write(root, [package]);
                var startupModules = ServerModules.CreateManaged(store.DesiredIds, InstalledModuleCatalog.Load(root));
                var plan = Preview("remove");
                check(plan.BlockedBy.Count == 0 && !plan.RestartRequired, "disabled independent package can be removed without another restart");
                try { store.Apply("observer", plan.PlanId, facts); throw new Exception("Wrong actor accepted"); }
                catch (ModuleManagementException error) when (error.Code == "module-plan-not-found")
                { check(true, "removal plan is actor bound"); }
                var operation = store.Apply("owner", plan.PlanId, facts);
                operationId = operation.OperationId;
                check(operation.State == ServerModuleOperationState.Succeeded, "removal completes with a durable success receipt");
                check(InstalledModuleStore.Read(root).Count == 0 && !Directory.Exists(packageRoot), "removal unregisters and removes active package directory");
                check(File.ReadAllText(data) == "generated-account-state", "removal preserves account and business data");
                check(!startupModules.IsInstalled(root, "sample")
                    && startupModules.CheckHealthAsync(root, CancellationToken.None).GetAwaiter().GetResult().Modules.All(item => item.ModuleId != "sample"),
                    "health and installation status stop advertising removed modules without restart");
                check(File.Exists(Path.Combine(root, "data/module-management/removal-backups", operationId.ToString("N"), "packages/1.0.0/payload")),
                    "removal retains recoverable package backup outside the install directory");
                check(store.Apply("owner", plan.PlanId, facts).OperationId == operationId, "retry returns the original receipt without repeating filesystem work");
            }
            using (var restarted = new ModuleManagementStore(configuration, root))
                check(restarted.Operation("owner", operationId).State == ServerModuleOperationState.Succeeded, "removal receipt survives restart");
            // Simulate interruption after unregister/archive but before final receipt publication.
            var statePath = Path.Combine(root, "data/module-management/state.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
            var operationNode = document["operations"]!.AsArray().Single(item => item!["operationId"]!.GetValue<Guid>() == operationId)!;
            operationNode["state"] = "Prepared";
            File.WriteAllText(statePath, document.ToJsonString());
            using var recovered = new ModuleManagementStore(configuration, root);
            check(recovered.Operation("owner", operationId).State == ServerModuleOperationState.RecoveryRequired,
                "interrupted removal is recoverable at startup and never inferred successful");
            try { ServerModuleManagementEndpoints.EnsurePackageMutationAllowed(recovered, "sample"); throw new Exception("Pending removal accepted"); }
            catch (ModuleManagementException error) when (error.Code == "module-operation-in-progress")
            { check(true, "all package mutation routes share the pending removal guard"); }
            check(InstalledModuleStore.Read(root).Count == 0 && recovered.DesiredIds.All(id => id != "sample"),
                "interrupted removal cannot resurrect the unregistered module");
            var failureRoot = Path.Combine(root, "failed-before-unregister");
            using (var failureStore = new ModuleManagementStore(configuration, failureRoot))
            {
                var record = new InstalledModuleRecord("sample", "1.0.0", "sample-1", "official", "publisher",
                    new string('a', 64), new string('b', 64), "packages/sample/1.0.0", "server/sample.dll",
                    "Sample.Entry", [], [], "module-host", DateTimeOffset.UtcNow);
                InstalledModuleStore.Write(failureRoot, [record]);
                Directory.CreateDirectory(Path.Combine(failureRoot, "data/module-management/packages/sample/1.0.0"));
                // A file where the backup directory should be reliably fails before unregister.
                File.WriteAllText(Path.Combine(failureRoot, "data/module-management/removal-backups"), "blocked");
                var facts = failureStore.DesiredIds.Select(id => new ServerModuleDto(id, true, [], [], [], []))
                    .Append(new ServerModuleDto("sample", false, [], [], [], [], DesiredEnabled: false, Packaging: "independent")).ToArray();
                var plan = failureStore.Preview("owner", new(failureStore.ServiceId, ServerModuleAction.Remove, ["sample"],
                    failureStore.DesiredIds.ToArray(), failureStore.Revision, "failed"), facts);
                var failed = failureStore.Apply("owner", plan.PlanId, facts);
                check(failed.State == ServerModuleOperationState.RecoveryRequired && InstalledModuleStore.Read(failureRoot).Count == 1,
                    "failure before unregister preserves installation and reports recovery required");
                try { ServerModuleManagementEndpoints.EnsurePackageMutationAllowed(failureStore, "sample"); throw new Exception("Recovery gate bypassed"); }
                catch (ModuleManagementException error) when (error.Code == "module-operation-in-progress")
                { check(true, "pre-unregister failure also blocks all package mutations"); }
                check(failureStore.Apply("owner", plan.PlanId, facts).OperationId == failed.OperationId,
                    "retry of a failed removal returns its original recovery receipt");
            }
            using var restartedFailure = new ModuleManagementStore(configuration, failureRoot);
            check(restartedFailure.CaptureInventory().PendingOperations.ContainsKey("sample"),
                "pre-unregister recovery guard survives host restart");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
