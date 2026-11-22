using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Tailscale;

internal sealed class CollectibleTailscaleExecution : IDisposable
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.Tailscale.dll";
    internal const string EntryType = "RelaxKonOS.ServerModule.Tailscale.TailscaleExecution";
    private AssemblyLoadContext? _context;
    private ITailscaleExecution? _module;
    internal ITailscaleExecution Module => _module ?? throw new ObjectDisposedException(nameof(CollectibleTailscaleExecution));

    internal CollectibleTailscaleExecution() : this(ResolveBundledPath()) { }

    private static string ResolveBundledPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Tailscale");
        var path = Path.Combine(directory, AssemblyFile);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Tailscale execution assembly is missing or linked.");
        return path;
    }

    internal CollectibleTailscaleExecution(string path)
    {
        var context = new ExecutionLoadContext();
        try
        {
            using var stream = File.OpenRead(path);
            var assembly = context.LoadFromStream(stream);
            var entry = assembly.GetType(EntryType, throwOnError: true)!;
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
