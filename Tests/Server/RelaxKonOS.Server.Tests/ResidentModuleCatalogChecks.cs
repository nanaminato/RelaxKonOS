using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ResidentModuleCatalogChecks
{
    // Unknown responsibility remains rooted until the test process exits; never simulate production recovery.
    private static readonly List<object> RetainedFailures = [];
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message)
        { if (!ok) throw new Exception(message); Console.WriteLine($"PASS RESIDENT CATALOG {++count}: {message}"); }
        await using (var app = Build(Environments.Production, null))
        {
            var leases = app.Services.GetRequiredService<IResidentModuleWorkLeaseSource>();
            try { await leases.AcquireAsync("demo.unknown"); throw new Exception("Unknown work identity accepted"); }
            catch (InvalidOperationException) { }
            Check(app.Services.GetRequiredService<ModuleResidencyCoordinator>().Describe("demo.unknown") is null,
                "All-disabled host still resolves mandatory admission and fails closed without inventing a module");
        }
        var root = Path.Combine(Path.GetTempPath(), "resident-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new Dictionary<string, string?>
            {
                ["ModuleHosts:Enabled"] = "true", ["ModuleHosts:Entries:0:ModuleId"] = "demo.development",
                ["ModuleHosts:Entries:0:PackageRoot"] = root,
                ["ModuleHosts:Entries:1:ModuleId"] = "demo.development", ["ModuleHosts:Entries:1:PackageRoot"] = root,
            };
            await using (var app = Build(Environments.Development, config))
            {
                var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
                var leases = app.Services.GetRequiredService<IResidentModuleWorkLeaseSource>();
                Check(coordinator.Describe("demo.development") is { State: ModuleResidencyState.Resident, Releasable: false },
                    "Explicit development entry has a single resident metadata identity without starting code");
                using (await leases.AcquireAsync("demo.development"))
                    Check(coordinator.Describe("demo.development")!.ActiveLeases == 1,
                        "Development generation uses the same real coordinator work lease");
                Check(!coordinator.HasBackgroundWork("demo.development")
                    && await coordinator.TrySleepAsync("demo.development", default) == "module-resident-work-owner",
                    "Released development work remains resident and does not claim idle process disposal");
            }
            await using (var app = Build(Environments.Production, config))
                Check(app.Services.GetRequiredService<ModuleResidencyCoordinator>().Describe("demo.development") is null,
                    "Development configuration never registers an executable identity in Production");
            config["ModuleHosts:Entries:0:ModuleId"] = ServerModules.TerminalId;
            config["ModuleHosts:Entries:1:ModuleId"] = "demo.invalid-access";
            config["ModuleHosts:Entries:1:AllowedUsers:0"] = "not-a-user-id";
            await using (var app = Build(Environments.Development, config))
            {
                var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
                Check(coordinator.Describe(ServerModules.TerminalId) is null && coordinator.Describe("demo.invalid-access") is null,
                    "Development entries cannot replace a disabled known module or admit invalid access metadata");
            }
        }
        finally { Directory.Delete(root); }
        var graphApp = Build(Environments.Production, null);
        var ids = new HashSet<string>(["milestone.parent", "milestone.child", "milestone.leaf"], StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder().Build();
        var graph = new ModuleResidencyCoordinator(ids.Select(id => (IServerModuleRuntimeFactory)new ResidentModuleWorkRuntimeFactory(id)).ToArray(),
            ModuleResidencyOptions.Parse(configuration, ids, new HashSet<string>()), graphApp.Services,
            ServerModuleContext.Create(configuration, graphApp.Environment, "memory"),
            graphApp.Services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>(),
            dependencies: new Dictionary<string, IReadOnlyList<string>>
            { ["milestone.parent"] = ["milestone.child"], ["milestone.child"] = ["milestone.leaf"] }, residentWorkModuleIds: ids);
        using (var admitted = await graph.AcquireAsync("milestone.parent"))
        {
            var failed = await new ResidentModuleWorkLeaseSource(graph).AcquireAsync("milestone.leaf");
            failed.RequireRecovery();
            RetainedFailures.Add((graphApp, graph, failed));
            try { using var unexpected = await graph.AcquireAsync("milestone.parent"); throw new Exception("Faulted dependency reused"); }
            catch (ModuleUnavailableException) { }
            Check(graph.Describe("milestone.leaf") is { State: ModuleResidencyState.Faulted, ActiveLeases: > 0 },
                "Already-loaded consumers reject fresh calls when a transitive dependency is quarantined");
            try { using var unexpected = new ModuleWorkLeaseSource(graph, admitted.Runtime).Acquire(); throw new Exception("Late dependency fault bypassed"); }
            catch (ModuleUnavailableException) { }
            Check(graph.Describe("milestone.parent")!.ActiveLeases == 1,
                "An older admission cannot publish new background work after dependency failure or discard its existing scope");
        }
        Console.WriteLine($"Resident module catalog checks passed: {count}");
    }

    private static WebApplication Build(string environment, Dictionary<string, string?>? config)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        if (config is not null) builder.Configuration.AddInMemoryCollection(config);
        var modules = ServerModules.CreateManaged(new HashSet<string>());
        modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
        return builder.Build();
    }
}
