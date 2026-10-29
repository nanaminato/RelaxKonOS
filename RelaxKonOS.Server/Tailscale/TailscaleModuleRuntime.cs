using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Tailscale;

internal sealed class TailscaleModuleRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => "relaxkonos.tailscale";
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IServerModuleRuntime>(new TailscaleModuleRuntime(services));
    }
}

internal sealed class TailscaleModuleRuntime : IServerModuleRuntime
{
    private readonly ModuleServiceContainer _services;
    public string ModuleId => "relaxkonos.tailscale";
    internal TailscaleModuleRuntime(IServiceProvider root)
    {
        _services = new ModuleServiceContainer(ModuleId, root);
        _services.AddSingleton<ITailscaleControlAuthorization>(services => services.Create<TailscaleControlAuthorization>());
        _services.AddSingleton<TailscaleOperationJournal>(services => services.Create<TailscaleOperationJournal>());
        _services.AddSingleton<ITailscaleControlHost>(services => services.Create<HelperTailscaleControlHost>());
        _services.AddSingleton<ITailscaleControl>(services => services.Create<TailscaleOperationCoordinator>());
        _services.AddSingleton<TailscaleLoginJournal>(services => services.Create<TailscaleLoginJournal>());
        _services.AddSingleton<ITailscaleLoginHost>(services => services.Create<HelperTailscaleLoginHost>());
        _services.AddSingleton<ITailscaleLogin>(services => services.Create<TailscaleLoginCoordinator>());
        _services.AddSingleton<ITailscaleManager>(_ => new HelperTailscaleManager(root.GetRequiredService<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>()));
    }
    public object Resolve(Type serviceType) => _services.GetRequired(serviceType);
    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

