using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Owns the request-only facade; Core retains the privileged transport and OS owns the rules.</summary>
internal sealed class FirewallModuleRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => ServerModules.FirewallId;
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IServerModuleRuntime>(new FirewallModuleRuntime(services, context.Platform));
    }
}

internal sealed class FirewallModuleRuntime : IServerModuleRuntime
{
    private readonly ModuleServiceContainer _services;
    public string ModuleId => ServerModules.FirewallId;
    internal FirewallModuleRuntime(IServiceProvider root, HostPlatformKind platform)
    {
        _services = new(ModuleId, root);
        _services.AddSingleton<IHostFirewallService>(container => platform switch
        {
            HostPlatformKind.Linux => container.Create<LinuxUfwFirewallService>(),
            HostPlatformKind.Windows => container.Create<WindowsFirewallService>(),
            _ => container.Create<UnavailableHostFirewallService>(),
        });
    }
    public object Resolve(Type serviceType) => _services.GetRequired(serviceType);
    // Every operation returns a fully materialized DTO and is covered by the proxy's task lease.
    // There are no module-owned background duties or long-lived streams to transfer on release.
    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
