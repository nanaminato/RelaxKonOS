using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Git;

/// <summary>
/// Creates the Git module's object graph on first business use. Everything the module owns is created
/// here and disposed when the residency coordinator releases it: the CLI wrapper, the repository service
/// with its per-repository write locks and the build ledger with its in-memory operations.
/// CLI, build and repository implementations share one collectible execution assembly.
/// Configuration and repository data on disk are untouched by a release.
/// </summary>
internal sealed class GitModuleRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => ServerModules.GitId;

    private readonly List<WeakReference> _code = [];
    internal WeakReference? LastCodeReference { get; private set; }

    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _code.RemoveAll(reference => !reference.IsAlive);
        // Natural GC may lag a release. Bound pending generations without forcing a process-wide GC.
        if (_code.Count >= 4) throw new InvalidOperationException("Git execution code collection is pending; host recovery may be required.");
        var runtime = new GitModuleRuntime(services, context);
        LastCodeReference = runtime.CodeReference;
        _code.Add(runtime.CodeReference);
        return ValueTask.FromResult<IServerModuleRuntime>(runtime);
    }
}

internal sealed class GitModuleRuntime : IServerModuleRuntime
{
    private readonly string _moduleId = ServerModules.GitId;
    private readonly CollectibleGitExecution _execution;
    internal WeakReference CodeReference => _execution.CodeReference;

    internal GitModuleRuntime(IServiceProvider root, ServerModuleContext context)
    {
        var work = new ModuleWorkLeaseSource(root.GetRequiredService<ModuleResidencyCoordinator>(), this);
        _execution = new CollectibleGitExecution(new(context.ContentRootPath, root.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            context.Configuration.GetSection("GitBuilds").Get<GitBuildOptions>() ?? new(), work, root.GetRequiredService<ILoggerFactory>(),
            new Lazy<IGitRepositoryStorage>(() => root.GetRequiredService<IGitRepositoryStorage>()),
            new Lazy<IGitRepositoryHost>(() => root.GetRequiredService<IGitRepositoryHost>())));
    }

    public string ModuleId => _moduleId;

    public object Resolve(Type serviceType) => serviceType == typeof(IGitBuildService) ? _execution.Module.Builds
        : serviceType == typeof(IGitRepositoryService) ? _execution.Module.Repositories
        : serviceType == typeof(IHostGitCli) ? _execution.Module.Cli
        : throw new InvalidOperationException("Unsupported Git module service contract.");

    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_execution.Module.UnloadBlocker is { } reason ? new ModuleUnloadBlocker(reason) : null);

    public async ValueTask DisposeAsync()
    {
        await _execution.DisposeAsync().ConfigureAwait(false);
    }
}
