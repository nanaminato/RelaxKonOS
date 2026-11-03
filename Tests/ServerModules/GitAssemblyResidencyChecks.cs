using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class GitAssemblyResidencyChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(!typeof(ServerModules).Assembly.GetReferencedAssemblies().Any(name => name.Name == "RelaxKonOS.ServerModule.Git"),
            "Server has no static reference to the Git execution implementation");
        check(!typeof(GitBuildLedger).Assembly.GetReferencedAssemblies().Any(name => name.Name is "RelaxKonOS.Server" or "RelaxKonOS.ServerModule.Git"),
            "Git shared contracts and read-only ledger inspection do not reference implementations");
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "git-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            var factory = new GitModuleRuntimeFactory();
            var ids = new HashSet<string> { ServerModules.GitId };
            builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([factory],
                ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services, context,
                services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>()));
            await using var app = builder.Build();
            var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
            var proxy = ModuleServiceProxy<IGitBuildService>.Create(coordinator, ServerModules.GitId);
            check(factory.LastCodeReference is null && coordinator.DescribeAll().Count == 1,
                "describing dormant Git does not load its implementation assembly");
            var owner = Guid.NewGuid();
            var original = proxy.SetCredential(owner, new("code-unload-test", "test-token"));
            check(IsCollectible(coordinator), "actual GitBuildService executes inside a collectible assembly context");
            check(await coordinator.TrySleepAsync(ServerModules.GitId, default) is null, "Git execution generation releases after its real ledger write");
            Collect();
            check(!factory.LastCodeReference!.IsAlive, "actual Git execution ALC is collected after release");
            check(proxy.Credentials(owner).Single().Id == original.Id,
                "a new Git execution generation reads its original persisted credential instead of resetting state");
            await coordinator.TrySleepAsync(ServerModules.GitId, default);
            Collect();
            var releases = new List<WeakReference>();
            for (var cycle = 0; cycle < 100; cycle++)
            {
                check(proxy.Credentials(owner).Single().Id == original.Id, $"Git code cycle {cycle + 1} retains ledger identity");
                releases.Add(factory.LastCodeReference!);
                if (await coordinator.TrySleepAsync(ServerModules.GitId, default) is not null)
                    throw new InvalidOperationException("Git generation failed to release.");
                Collect();
            }
            check(releases.All(reference => !reference.IsAlive), "100 actual Git execution contexts are collectible without accumulated generations");
            var held = new List<AssemblyLoadContext>();
            var bounded = new GitModuleRuntimeFactory();
            for (var generation = 0; generation < 4; generation++)
            {
                var runtime = await bounded.CreateAsync(app.Services, context, default);
                held.Add(HoldCode(runtime));
                await runtime.DisposeAsync();
            }
            var refused = false;
            try { await bounded.CreateAsync(app.Services, context, default); }
            catch (InvalidOperationException error) when (error.Message.Contains("code collection is pending", StringComparison.Ordinal)) { refused = true; }
            check(refused, "pending code contexts are bounded while a caller deliberately retains old implementation metadata");
            GC.KeepAlive(held);
            GC.KeepAlive(proxy);
            GC.KeepAlive(coordinator);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCollectible(ModuleResidencyCoordinator coordinator)
    {
        using var lease = coordinator.AcquireAsync(ServerModules.GitId).GetAwaiter().GetResult();
        var service = lease.Runtime.Resolve(typeof(IGitBuildService));
        return AssemblyLoadContext.GetLoadContext(service.GetType().Assembly)!.IsCollectible;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AssemblyLoadContext HoldCode(IServerModuleRuntime runtime) =>
        AssemblyLoadContext.GetLoadContext(runtime.Resolve(typeof(IGitBuildService)).GetType().Assembly)!;

    private static void Collect()
    {
        // Verification only; the production factory bounds pending contexts without forcing GC.
        for (var attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }
}
