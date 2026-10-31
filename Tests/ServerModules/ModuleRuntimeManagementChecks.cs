using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Residency;

internal static class ModuleRuntimeManagementChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "runtime-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            using var store = new ModuleManagementStore(builder.Configuration, root);
            var modules = ServerModules.Create(builder.Configuration);
            var factory = new FakeRuntimeFactory(ServerModules.GitId);
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            var options = ModuleResidencyOptions.Parse(builder.Configuration, modules.KnownModuleIds, modules.ReleasableModuleIds);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<ModuleResidencyCoordinator>(sp => new([factory], options, sp, context, sp.GetRequiredService<ILogger<ModuleResidencyCoordinator>>()));
            await using var provider = services.BuildServiceProvider();
            var coordinator = provider.GetRequiredService<ModuleResidencyCoordinator>();
            using var executor = new ModuleManagementExecutor(store, provider, provider.GetRequiredService<ILogger<ModuleManagementExecutor>>());
            await executor.StartAsync(CancellationToken.None);
            var all = store.DesiredIds.Order(StringComparer.Ordinal).ToArray();
            IReadOnlyList<ServerModuleDto> Inventory() => all.Select(id => new ServerModuleDto(id, true, [], [], [], [],
                id == ServerModules.GitId ? new ServerModuleResidencyDto(coordinator.Describe(id)!.State.ToString().ToLowerInvariant(), true,
                    "IdleDelayed", 300, coordinator.Describe(id)!.ActiveLeases, null, null, null, null, null) : null,
                ResourceReleaseLevel: id == ServerModules.GitId ? "objects" : "none")).ToArray();
            ServerModulePlanDto Preview(ServerModuleAction action) => store.Preview("actor", new(store.ServiceId, action,
                [ServerModules.GitId], all, store.Revision, Guid.NewGuid().ToString("N")), Inventory());
            async Task<ServerModuleOperationDto> Complete(Guid id)
            {
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    var operation = store.Operation("actor", id);
                    if (operation.State is ServerModuleOperationState.Succeeded or ServerModuleOperationState.Failed) return operation;
                    await Task.Delay(10);
                }
                throw new Exception("Runtime management operation did not settle");
            }
            var revision = store.Revision;
            var wake = Preview(ServerModuleAction.Wake);
            check(wake.BlockedBy.Count == 0 && !wake.RestartRequired, "runtime wake is available independently of deployment-owned configuration");
            var started = executor.Apply("actor", wake.PlanId, Inventory());
            var completed = await Complete(started.OperationId);
            check(completed.State == ServerModuleOperationState.Succeeded && factory.Created == 1 && store.Revision == revision,
                "runtime wake persists a successful receipt without changing desired configuration revision");
            check(executor.Apply("actor", wake.PlanId, Inventory()).OperationId == started.OperationId && factory.Created == 1,
                "runtime apply retries recover one original operation without replaying initialization");
            using (var business = await coordinator.AcquireAsync(ServerModules.GitId))
            {
                var sleep = Preview(ServerModuleAction.TrySleep);
                check(sleep.BlockedBy.Any(x => x.ProblemCode == "module-active-work") && factory.Last!.Disposed == 0,
                    "manual sleep preview reports active work and never forces a business lease to end");
            }
            var release = Preview(ServerModuleAction.TrySleep);
            var released = await Complete(executor.Apply("actor", release.PlanId, Inventory()).OperationId);
            check(released.State == ServerModuleOperationState.Succeeded && factory.Last!.Disposed == 1
                && coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Dormant,
                "manual sleep uses the real release boundary and persists its module result");
            check(released.ModuleResults is [{ Succeeded: true, ModuleId: ServerModules.GitId }], "runtime receipt identifies completed module actions");
            await executor.StopAsync(CancellationToken.None);
            Guid interruptedId;
            var interruptedRoot = Path.Combine(root, "interrupted");
            using (var first = new ModuleManagementStore(builder.Configuration, interruptedRoot))
            {
                var intention = first.Preview("actor", new(first.ServiceId, ServerModuleAction.Wake, [ServerModules.GitId], all,
                    first.Revision, "crash-intention"), Inventory());
                interruptedId = first.BeginRuntime("actor", intention.PlanId, Inventory()).Operation.OperationId;
            }
            using (var second = new ModuleManagementStore(builder.Configuration, interruptedRoot))
            {
                var interrupted = second.Operation("actor", interruptedId);
                check(interrupted.State == ServerModuleOperationState.Failed && interrupted.ProblemCode == "module-operation-interrupted",
                    "a committed but interrupted runtime intention is diagnosed after restart without automatic replay");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
