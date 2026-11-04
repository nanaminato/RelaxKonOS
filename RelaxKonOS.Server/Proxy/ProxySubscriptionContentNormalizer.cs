using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host call gate. Only text crosses the independently loaded subscription implementation boundary.</summary>
internal static class ProxySubscriptionContentNormalizer
{
    private static readonly ProxySubscriptionExecutionGate Gate = new();

    internal static string Normalize(string content) => Gate.Normalize(content, out _);

    internal static string Normalize(string content, out WeakReference code) => Gate.Normalize(content, out code);
}

/// <summary>Bounds active and not-yet-collected conversion contexts without forcing GC.</summary>
internal sealed class ProxySubscriptionExecutionGate
{
    internal const int MaxOutstandingContexts = 16;
    private readonly object _gate = new();
    private readonly List<WeakReference> _code = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal string Normalize(string content, out WeakReference code)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Proxy");
        var path = Path.Combine(directory, "RelaxKonOS.ServerModule.Proxy.dll");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Proxy subscription assembly is missing or linked.");
        ExecutionContext context;
        lock (_gate)
        {
            _code.RemoveAll(reference => !reference.IsAlive);
            if (_code.Count >= MaxOutstandingContexts)
                throw new InvalidDataException("Proxy subscription code collection is pending; retry after collection or arrange host recovery.");
            context = new ExecutionContext();
            code = new WeakReference(context);
            _code.Add(code);
        }
        try { return Convert(context, path, content); }
        finally { context.Unload(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Convert(AssemblyLoadContext context, string path, string content)
    {
        try
        {
            var type = context.LoadFromAssemblyPath(path).GetType("RelaxKonOS.Server.Proxy.ProxySubscriptionNormalizer", true)!;
            return ((IProxySubscriptionNormalizer)Activator.CreateInstance(type)!).Normalize(content);
        }
        catch (Exception)
        {
            // Do not leak a private exception/stack through a root operation record or logging pipeline.
            throw new InvalidDataException("Proxy subscription conversion failed.");
        }
    }

    private sealed class ExecutionContext() : AssemblyLoadContext("proxy-subscription:" + Guid.NewGuid().ToString("N"), true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "RelaxKonOS.Server") throw new InvalidDataException("Proxy execution cannot reference Server implementation.");
            return null;
        }
    }
}
