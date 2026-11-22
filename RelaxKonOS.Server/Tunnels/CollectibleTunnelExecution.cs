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
            var entry = ReadEntryType(context, path);
            _module = (ITunnelExecution)Activator.CreateInstance(entry)!;
            _context = context;
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    internal static void VerifyStagedExecution(string path)
    {
        var context = new ExecutionLoadContext();
        try { ReadEntryType(context, path); }
        catch { throw new InvalidDataException("Tunnels execution entry does not match its host contract."); }
        finally { context.Unload(); }
    }

    private static Type ReadEntryType(ExecutionLoadContext context, string path)
    {
        using var stream = File.OpenRead(path);
        var entry = context.LoadFromStream(stream).GetType(EntryType, throwOnError: true)!;
        if (entry.IsAbstract || !typeof(ITunnelExecution).IsAssignableFrom(entry)
            || entry.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidDataException("Tunnels execution entry does not match its host contract.");
        return entry;
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
