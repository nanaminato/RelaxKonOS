using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Tailscale;

internal sealed class CollectibleTailscaleExecution : IDisposable
{
    private AssemblyLoadContext? _context;
    private ITailscaleExecution? _module;
    internal ITailscaleExecution Module => _module ?? throw new ObjectDisposedException(nameof(CollectibleTailscaleExecution));

    internal CollectibleTailscaleExecution()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Tailscale");
        var path = Path.Combine(directory, "RelaxKonOS.ServerModule.Tailscale.dll");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Tailscale execution assembly is missing or linked.");
        var context = new ExecutionLoadContext();
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            var entry = assembly.GetType("RelaxKonOS.ServerModule.Tailscale.TailscaleExecution", throwOnError: true)!;
            _module = (ITailscaleExecution)Activator.CreateInstance(entry)!;
            _context = context;
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public void Dispose()
    {
        _module = null;
        Interlocked.Exchange(ref _context, null)?.Unload();
    }

    private sealed class ExecutionLoadContext() : AssemblyLoadContext("tailscale-execution", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "RelaxKonOS.Server")
                throw new InvalidDataException("Tailscale execution cannot depend on Server implementation.");
            return null;
        }
    }
}
