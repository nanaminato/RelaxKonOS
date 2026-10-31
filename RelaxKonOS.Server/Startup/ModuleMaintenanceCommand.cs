using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

internal static class ModuleMaintenanceCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "modules" && args[1] == "pack-package")
                return await ModulePackageBuildCommand.RunAsync(args);
            if (args.Length >= 2 && args[0] == "modules" && args[1] is "verify-package" or "catalog-status" or "accept-catalog"
                    or "install-package" or "list-installed" or "list-versions" or "list-package-operations"
                    or "rollback-installed" or "remove-installed" or "host-probe")
                return await ModulePackageMaintenanceCommand.RunAsync(args);
            if (args.Length < 2 || args[0] != "modules" || args[1] is not ("diagnose" or "recover-enable-all"))
                throw new ModuleManagementException("module-maintenance-command-invalid", 400);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 2; index < args.Length; index += 2)
                if (index + 1 >= args.Length || args[index] is not ("--content-root" or "--service-id" or "--expected-revision")
                    || !values.TryAdd(args[index], args[index + 1])) throw new ModuleManagementException("module-maintenance-command-invalid", 400);
            if (!values.TryGetValue("--content-root", out var root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                throw new ModuleManagementException("module-content-root-required", 400);
            root = Path.GetFullPath(root);
            if (args[1] == "diagnose")
            {
                if (values.Count != 1) throw new ModuleManagementException("module-maintenance-command-invalid", 400);
                Console.WriteLine(JsonSerializer.Serialize(ModuleManagementStore.ReadRecoveryReport(root), RelaxKonOSJsonOptions.Default));
                return 0;
            }
            if (!AuthMaintenanceCommand.IsAdministrator()) throw new ModuleManagementException("module-local-administrator-required", 403);
            if (!values.TryGetValue("--service-id", out var serviceId) || !values.TryGetValue("--expected-revision", out var revision))
                throw new ModuleManagementException("module-recovery-identity-required", 400);
            var localIdentity = ServerProcessIdentity.CurrentStableIdentity()
                ?? throw new ModuleManagementException("module-host-identity-unavailable", 403);
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = environment });
            using var configurationLifetime = (IDisposable)builder.Configuration;
            builder.Configuration.AddJsonFile("appsettings.host.json", optional: true, reloadOnChange: false);
            using var store = ModuleManagementStore.OpenForLocalRecovery(builder.Configuration, root, serviceId, revision);
            var installedModules = InstalledModuleCatalog.Load(root);
            var catalog = ServerModules.Create(new ConfigurationBuilder().Build(), installedModules);
            var stopped = ServerModules.CreateManaged(new HashSet<string>(StringComparer.Ordinal), installedModules);
            using var services = new ServiceCollection().BuildServiceProvider();
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            var facts = await stopped.DescribeAsync(services, context, CancellationToken.None);
            // Offline work is unknown, never assumed absent. This command only enables the entire
            // compiled set, so it cannot introduce a new deactivation or discard durable ownership.
            facts = facts.Select(module => module with { OrphanedWork = ["module-offline-work-unknown"] }).ToArray();
            var operation = RecoverAll(store, serviceId, revision, catalog.EnabledIds, facts,
                "local-recovery:" + localIdentity);
            Console.WriteLine(JsonSerializer.Serialize(operation, RelaxKonOSJsonOptions.Default));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is ModuleManagementException management ? management.Code
                : exception is ModulePackageException package ? package.ProblemCode
                : exception is IOException ? "module-state-unavailable" : "module-maintenance-failed");
            return 1;
        }
    }

    internal static ServerModuleOperationDto RecoverAll(ModuleManagementStore store, string serviceId, string revision,
        IReadOnlySet<string> catalogIds, IReadOnlyList<ServerModuleDto> offlineFacts, string actor)
    {
        var target = catalogIds.Order(StringComparer.Ordinal).ToArray();
        if (!catalogIds.SetEquals(offlineFacts.Select(x => x.Id))) throw new ModuleManagementException("module-recovery-catalog-mismatch");
        var plan = store.Preview(actor, new(serviceId, ServerModuleAction.Enable, target, target, revision,
            "local-recovery-" + revision), offlineFacts);
        return store.Apply(actor, plan.PlanId, offlineFacts);
    }
}
