using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Bundled current ABI, loaded once per generation. Host caches only the weak code observation.</summary>
internal sealed class CollectibleFirewallExecution : IAsyncDisposable
{
    private AssemblyLoadContext? _context;
    private IFirewallExecutionModule? _module;
    internal WeakReference CodeReference { get; }
    internal IFirewallExecutionModule Module => _module ?? throw new ObjectDisposedException(nameof(CollectibleFirewallExecution));
    internal CollectibleFirewallExecution(FirewallExecutionContext services, Action<WeakReference>? created = null)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Firewall");
        var entry = Path.Combine(directory, "RelaxKonOS.ServerModule.Firewall.dll");
        if (!File.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Firewall execution assembly is missing or linked.");
        var context = new ExecutionLoadContext(); CodeReference = new(context);
        created?.Invoke(CodeReference);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entry);
            var type = assembly.GetType("RelaxKonOS.Server.Firewall.FirewallExecutionModule", throwOnError: true)!;
            _module = (IFirewallExecutionModule)Activator.CreateInstance(type, services)!;
            _context = context;
        }
        catch
        {
            context.Unload();
            // Activator/constructor exceptions may contain private Types and stack handles.
            // Only a fresh bounded Host failure may escape this current bundled-code boundary.
            throw new InvalidDataException("Bundled Firewall execution could not be activated.");
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        await _module.DisposeAsync().ConfigureAwait(false);
        _module = null; var context = _context; _context = null; context?.Unload();
    }
    private sealed class ExecutionLoadContext() : AssemblyLoadContext("firewall-execution:" + Guid.NewGuid().ToString("N"), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "RelaxKonOS.Server" || name.Name?.StartsWith("RelaxKonOS.ServerModule.", StringComparison.Ordinal) == true
                && name.Name != "RelaxKonOS.ServerModule.Firewall.Contracts")
                throw new InvalidDataException("Firewall execution cannot depend on Host or another implementation.");
            // The current implementation has no private managed or native dependencies. Shared
            // contracts, Protocol and framework identities are supplied by the default context.
            return null;
        }
    }
}
