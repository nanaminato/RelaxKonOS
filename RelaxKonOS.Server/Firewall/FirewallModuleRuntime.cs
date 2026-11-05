using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Owns one collectible request-only facade; Core retains the Helper and OS rules stay external.</summary>
internal sealed class FirewallModuleRuntimeFactory : IServerModuleRuntimeFactory
{
    private readonly List<WeakReference> _code = [];
    internal WeakReference? LastCodeReference { get; private set; }
    public string ModuleId => ServerModules.FirewallId;
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _code.RemoveAll(reference => !reference.IsAlive);
        if (_code.Count >= 4) throw new InvalidOperationException("Firewall execution code collection is pending; host recovery may be required.");
        var runtime = new FirewallModuleRuntime(services, context, code => { LastCodeReference = code; _code.Add(code); });
        return ValueTask.FromResult<IServerModuleRuntime>(runtime);
    }
}

internal sealed class FirewallModuleRuntime : IServerModuleRuntime
{
    private readonly CollectibleFirewallExecution _execution;
    private readonly FirewallServiceBoundary _service;
    internal WeakReference CodeReference => _execution.CodeReference;
    public string ModuleId => ServerModules.FirewallId;
    internal FirewallModuleRuntime(IServiceProvider root, ServerModuleContext context, Action<WeakReference> created)
    {
        _execution = new(new(context.Platform, new FirewallHostOperations(root.GetRequiredService<IPrivilegedOperationTransport>()),
            new FirewallExecutionLog(root.GetRequiredService<ILoggerFactory>().CreateLogger("RelaxKonOS.Firewall"))), created);
        _service = new(_execution.Module.Firewall);
    }
    public object Resolve(Type serviceType) => serviceType == typeof(IHostFirewallService) ? _service
        : throw new InvalidOperationException("Unsupported Firewall service contract.");
    // Every operation returns a fully materialized DTO and is covered by the proxy's task lease.
    // There are no module-owned background duties or long-lived streams to transfer on release.
    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
    public async ValueTask DisposeAsync()
    {
        await _execution.DisposeAsync().ConfigureAwait(false);
        _service.Release();
    }
}
