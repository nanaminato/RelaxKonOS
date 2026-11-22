using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Bundled current ABI, loaded once per generation. Host caches only the weak code observation.</summary>
internal sealed class CollectibleFirewallExecution : IAsyncDisposable
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.Firewall.dll";
    internal const string EntryType = "RelaxKonOS.Server.Firewall.FirewallExecutionModule";
    private AssemblyLoadContext? _context;
    private IFirewallExecutionModule? _module;
    internal WeakReference CodeReference { get; }
    internal IFirewallExecutionModule Module => _module ?? throw new ObjectDisposedException(nameof(CollectibleFirewallExecution));
    internal CollectibleFirewallExecution(FirewallExecutionContext services, Action<WeakReference>? created = null)
        : this(ResolveBundledPath(), services, created) { }
    internal static string ResolveBundledPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Firewall");
        var entry = Path.Combine(directory, AssemblyFile);
        if (!File.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Firewall execution assembly is missing or linked.");
        return entry;
    }

    internal CollectibleFirewallExecution(string entry, FirewallExecutionContext services, Action<WeakReference>? created = null)
    {
        var context = new ExecutionLoadContext(); CodeReference = new(context);
        created?.Invoke(CodeReference);
        try
        {
            var type = ReadEntryType(context, entry);
            _module = (IFirewallExecutionModule)Activator.CreateInstance(type, services)!;
            _context = context;
        }
        catch
        {
            context.Unload();
            // Activator/constructor exceptions may contain private Types and stack handles.
            // Only a fresh bounded Host failure may escape this current bundled-code boundary.
            throw new InvalidDataException("Firewall execution could not be activated.");
        }
    }

    internal static void VerifyStagedExecution(string entry)
    {
        var context = new ExecutionLoadContext();
        try { ReadEntryType(context, entry); }
        catch { throw new InvalidDataException("Firewall execution entry does not match its host contract."); }
        finally { context.Unload(); }
    }

    private static Type ReadEntryType(ExecutionLoadContext context, string entry)
    {
        using var stream = File.OpenRead(entry);
        var assembly = context.LoadFromStream(stream);
        var type = assembly.GetType(EntryType, throwOnError: true)!;
        if (type.IsAbstract || !typeof(IFirewallExecutionModule).IsAssignableFrom(type)
            || type.GetConstructor([typeof(FirewallExecutionContext)]) is null)
            throw new InvalidDataException("Firewall execution entry does not match its host contract.");
        return type;
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
