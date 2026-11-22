using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Settings;
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
        _services.AddSingleton<ITailscaleControlHost>(services => services.Create<HelperTailscaleControlHost>());
        _services.AddSingleton<ITailscaleControl>(services => BundledTailscaleExecution.Instance.CreateControl(
            root.GetRequiredService<IHostEnvironment>(), root.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequired<ITailscaleControlHost>(), services.GetRequired<ITailscaleControlAuthorization>()));
        _services.AddSingleton<ITailscaleLoginHost>(services => services.Create<HelperTailscaleLoginHost>());
        _services.AddSingleton<ITailscaleLogin>(services => BundledTailscaleExecution.Instance.CreateLogin(
            root.GetRequiredService<IHostEnvironment>(), root.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequired<ITailscaleLoginHost>(), services.GetRequired<ITailscaleControlAuthorization>(),
            () => root.GetRequiredService<ServerRuntimeSettingsStore>().Current.InstanceId));
        _services.AddSingleton<ITailscaleManager>(_ => new HelperTailscaleManager(root.GetRequiredService<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>()));
    }
    public object Resolve(Type serviceType) => _services.GetRequired(serviceType);
    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

