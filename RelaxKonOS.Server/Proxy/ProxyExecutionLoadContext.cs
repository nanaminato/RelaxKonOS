using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Proxy;

internal sealed class ProxyExecutionLoadContext(string purpose) : AssemblyLoadContext($"proxy-{purpose}:{Guid.NewGuid():N}", true)
{
    internal static string BundledAssemblyPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Proxy");
        var path = Path.Combine(directory, "RelaxKonOS.ServerModule.Proxy.dll");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Proxy execution assembly is missing or linked.");
        return path;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "RelaxKonOS.Server") throw new InvalidDataException("Proxy execution cannot reference Server implementation.");
        return null;
    }
}
