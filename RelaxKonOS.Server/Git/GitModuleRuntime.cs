using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Git;

/// <summary>
/// Creates the Git module's object graph on first business use. Everything the module owns is created
/// here and disposed when the residency coordinator releases it: the CLI wrapper, the repository service
/// with its per-repository write locks and the build ledger with its in-memory operations. The assembly,
/// configuration and repository data on disk are untouched by a release.
/// </summary>
internal sealed class GitModuleRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => ServerModules.GitId;

    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IServerModuleRuntime>(new GitModuleRuntime(services, context));
}

internal sealed class GitModuleRuntime : IServerModuleRuntime
{
    private readonly string _moduleId = ServerModules.GitId;
    private readonly ModuleServiceContainer _services;

    internal GitModuleRuntime(IServiceProvider root, ServerModuleContext context)
    {
        _services = new ModuleServiceContainer(_moduleId, root);
        _services.AddSingleton<GitBuildOptions>(_ =>
            context.Configuration.GetSection("GitBuilds").Get<GitBuildOptions>() ?? new GitBuildOptions());
        _services.AddSingleton<IHostGitCli>(_ => new HostGitCli());
        _services.AddSingleton<IModuleWorkLeaseSource>(_ => new ModuleWorkLeaseSource(root.GetRequiredService<ModuleResidencyCoordinator>(), this));
        _services.AddSingleton<IGitBuildCommandRunner>(_ => new GitBuildCommandRunner());
        _services.AddSingleton<IGitRepositoryService>(container => container.Create<LocalGitRepositoryService>());
        _services.AddSingleton<IGitBuildService>(container => container.Create<GitBuildService>());
    }

    public string ModuleId => _moduleId;

    public object Resolve(Type serviceType) => _services.GetRequired(serviceType);

    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken)
    {
        if (_services.TryGetSingleton(typeof(IGitBuildService), out var buildState)
            && buildState is GitBuildService { RequiresRecovery: true })
            return ValueTask.FromResult<ModuleUnloadBlocker?>(new("module-recovery-blocked"));
        // Nothing is disposed while a build is still writing to its ledger: the coordinator keeps the
        // module and retries after the grace period instead of cancelling the work silently.
        if (_services.TryGetSingleton(typeof(IGitBuildService), out var instance)
            && instance is GitBuildService { HasActiveWork: true } builds)
            return ValueTask.FromResult<ModuleUnloadBlocker?>(
                new ModuleUnloadBlocker($"{builds.ActiveBuildCount} git build(s) are still running"));

        return ValueTask.FromResult<ModuleUnloadBlocker?>(null);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
