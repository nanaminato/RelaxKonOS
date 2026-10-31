using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Startup;

internal static class ModuleRecoveryChecks
{
    internal static async Task RejectAsync(Action<bool, string> check, Action action, string code)
    {
        try { action(); throw new Exception("Expected recovery refusal: " + code); }
        catch (ModuleManagementException exception) when (exception.Code == code) { check(true, "offline recovery rejects " + code); }
        await Task.CompletedTask;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "module-recovery-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ModuleManagement:Authority"] = "managed" }).Build();
        var modules = ServerModules.Create(config);
        var all = modules.EnabledIds.Order(StringComparer.Ordinal).ToArray();
        var running = all.Select(id => new ServerModuleDto(id, true, [], [], [], [])).ToArray();
        var offline = running.Select(module => module with { EffectiveEnabled = false, OrphanedWork = ["module-offline-work-unknown"] }).ToArray();
        try
        {
            using var store = new ModuleManagementStore(config, root);
            var plan = store.Preview("actor", new(store.ServiceId, ServerModuleAction.Disable, [ServerModules.GitId],
                all.Where(id => id != ServerModules.GitId).ToArray(), store.Revision, "disable"), running);
            var disabled = store.Apply("actor", plan.PlanId, running);
            var path = Path.Combine(root, "data", "module-management", "state.json");
            var before = File.ReadAllBytes(path);
            var report = ModuleManagementStore.ReadRecoveryReport(root);
            check(report.ObservationScope == "desired-only" && !report.DesiredEnabledModuleIds.Contains(ServerModules.GitId)
                && report.UnresolvedOperations.Any(operation => operation.OperationId == disabled.OperationId),
                "offline diagnostics report desired intent and unresolved receipt without claiming effective health");
            check(before.SequenceEqual(File.ReadAllBytes(path)), "offline diagnostic reads do not mutate or verify pending operations");
            var output = new StringWriter();
            var previous = Console.Out;
            int exit;
            try
            {
                Console.SetOut(output);
                exit = await ModuleMaintenanceCommand.RunAsync(["modules", "diagnose", "--content-root", root]);
            }
            finally { Console.SetOut(previous); }
            var parsed = JsonSerializer.Deserialize<ServerModuleRecoveryReportDto>(output.ToString(), RelaxKonOSJsonOptions.Default);
            check(exit == 0 && parsed?.ServiceId == store.ServiceId && before.SequenceEqual(File.ReadAllBytes(path)),
                "maintenance diagnose command is a read-only entry independent of the web host");
            var serverAssembly = typeof(ModuleMaintenanceCommand).Assembly.Location;
            var serverHost = Path.Combine(Path.GetDirectoryName(serverAssembly)!, OperatingSystem.IsWindows() ? "RelaxKonOS.Server.exe" : "RelaxKonOS.Server");
            var useAppHost = File.Exists(serverHost) && File.Exists(Path.ChangeExtension(serverAssembly, ".deps.json"));
            var start = new System.Diagnostics.ProcessStartInfo(useAppHost ? serverHost : "dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (!useAppHost) start.ArgumentList.Add(serverAssembly);
            foreach (var argument in new[] { "modules", "diagnose", "--content-root", root })
                start.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                var nativeOutput = await standardOutput;
                var nativeError = await standardError;
                check(process.ExitCode == 0, "native Server maintenance exits successfully: " + nativeError);
                var nativeReport = JsonSerializer.Deserialize<ServerModuleRecoveryReportDto>(nativeOutput, RelaxKonOSJsonOptions.Default);
                check(string.IsNullOrWhiteSpace(nativeError) && nativeReport?.ServiceId == store.ServiceId
                    && before.SequenceEqual(File.ReadAllBytes(path)), "native Server command dispatch diagnoses state without starting web services or mutating it");
            }
            await RejectAsync(check, () => ModuleMaintenanceCommand.RecoverAll(store, "other", store.Revision, modules.EnabledIds, offline, "local:test"), "module-server-mismatch");
            await RejectAsync(check, () => ModuleMaintenanceCommand.RecoverAll(store, store.ServiceId, "old", modules.EnabledIds, offline, "local:test"), "module-revision-conflict");
            check(before.SequenceEqual(File.ReadAllBytes(path)), "rejected recovery targets leave the authoritative state byte-for-byte unchanged");
            if (!AuthMaintenanceCommand.IsAdministrator())
                await RejectAsync(check, () => ModuleManagementStore.OpenForLocalRecovery(config, root, store.ServiceId, store.Revision), "module-local-administrator-required");
            var policies = store.CaptureInventory().Policies;
            var originalRevision = store.Revision;
            var recovered = ModuleMaintenanceCommand.RecoverAll(store, store.ServiceId, originalRevision, modules.EnabledIds, offline, "local:test");
            check(recovered.State == ServerModuleOperationState.AwaitingRestart && store.DesiredIds.SetEquals(all),
                "explicit recovery only restores the full compiled set and remains awaiting restart");
            check(store.CaptureInventory().Policies.All(pair => policies[pair.Key] == pair.Value),
                "enable-all recovery preserves residency policy and never installs external software");
            check(store.PlanStatus("local:test", recovered.PlanId).Plan.Target.Action == ServerModuleAction.Enable,
                "offline recovery keeps its exact enable intent and original plan receipt in the normal ledger");
            store.RecordStartupFailure("deactivation");
            var failedStartup = store.Operation("local:test", recovered.OperationId);
            check(failedStartup.State == ServerModuleOperationState.RecoveryRequired
                && failedStartup.ProblemCode == "module-startup-deactivation-failed"
                && failedStartup.EnabledModuleIds.SequenceEqual(recovered.EnabledModuleIds),
                "candidate startup failure preserves the exact original target and records its failed stage");
            check(store.Operation("actor", disabled.OperationId).State == ServerModuleOperationState.AwaitingRestart,
                "candidate startup failure does not rewrite superseded revision receipts");
            check(ModuleManagementStore.ReadRecoveryReport(root).UnresolvedOperations.Any(operation =>
                operation.OperationId == recovered.OperationId && operation.State == ServerModuleOperationState.RecoveryRequired),
                "offline diagnostics retain durable startup-failure evidence without claiming health");
            store.VerifyStartup(modules.EnabledIds);
            check(store.Operation("local:test", recovered.OperationId).State == ServerModuleOperationState.Succeeded
                && store.Operation("actor", disabled.OperationId).ProblemCode == "module-target-superseded",
                "successful startup verifies a failed candidate against the original target and rejects a superseded target");
            var verified = File.ReadAllBytes(path);
            store.RecordStartupFailure("host");
            check(verified.SequenceEqual(File.ReadAllBytes(path)),
                "later host failure cannot rewrite already verified operation evidence");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
