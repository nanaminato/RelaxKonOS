using System.Reflection;
using System.Runtime.Loader;
using RelaxKonOS.ServerModule.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

internal sealed class CollectibleTunnelExecution : IDisposable
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.Tunnels.dll";
    internal const string EntryType = "RelaxKonOS.ServerModule.Tunnels.TunnelExecution";
    private AssemblyLoadContext? _context;
    private ITunnelExecution? _module;
    internal ITunnelExecution Module => _module ?? throw new ObjectDisposedException(nameof(CollectibleTunnelExecution));

    internal CollectibleTunnelExecution() : this(ResolveBundledPath()) { }

    private static string ResolveBundledPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Tunnels");
        var path = Path.Combine(directory, AssemblyFile);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Tunnels execution assembly is missing or linked.");
        return path;
    }

    internal CollectibleTunnelExecution(string path)
    {
        var context = new ExecutionLoadContext();
        try
        {
            using var stream = File.OpenRead(path);
            var assembly = context.LoadFromStream(stream);
            var entry = assembly.GetType(EntryType, throwOnError: true)!;
            _module = (ITunnelExecution)Activator.CreateInstance(entry)!;
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

    private sealed class ExecutionLoadContext() : AssemblyLoadContext("tunnels-execution", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "RelaxKonOS.Server")
                throw new InvalidDataException("Tunnels execution cannot depend on Server implementation.");
            return null;
        }
    }
}
