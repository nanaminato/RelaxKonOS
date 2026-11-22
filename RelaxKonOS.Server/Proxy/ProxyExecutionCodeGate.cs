using System.Runtime.Loader;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Bounds active and not-yet-collected execution contexts without forcing GC.</summary>
internal sealed class ProxyExecutionCodeGate(string purpose, Func<string>? resolvePath = null)
{
    internal const int MaxOutstandingContexts = 16;
    private readonly object _gate = new();
    private readonly List<WeakReference> _code = [];

    internal Lease Acquire()
    {
        var path = resolvePath is null ? ProxyExecutionLoadContext.BundledAssemblyPath() : resolvePath();
        lock (_gate)
        {
            _code.RemoveAll(reference => !reference.IsAlive);
            if (_code.Count >= MaxOutstandingContexts)
                throw new InvalidDataException("Proxy execution code collection is pending; retry after collection or arrange host recovery.");
            var context = new ProxyExecutionLoadContext(purpose);
            var reference = new WeakReference(context);
            _code.Add(reference);
            return new Lease(context, path, reference);
        }
    }

    internal sealed class Lease(ProxyExecutionLoadContext context, string path, WeakReference reference) : IDisposable
    {
        private AssemblyLoadContext? owner = context;
        private System.Reflection.Assembly? assembly;
        internal WeakReference Reference => reference;

        internal T Create<T>(string name, params object[] args)
        {
            var code = owner ?? throw new ObjectDisposedException(nameof(Lease));
            try
            {
                if (assembly is null)
                {
                    using var stream = File.OpenRead(path);
                    assembly = code.LoadFromStream(stream);
                }
            }
            catch (Exception) { throw new InvalidDataException($"Proxy execution assembly could not be loaded for entry '{name}'."); }

            try
            {
                var type = assembly.GetType(name, throwOnError: true)!;
                return (T)Activator.CreateInstance(type, args)!;
            }
            catch (TypeLoadException) { throw new InvalidDataException($"Proxy execution entry '{name}' could not be loaded."); }
            catch (Exception) { throw new InvalidDataException($"Proxy execution entry '{name}' could not be activated."); }
        }

        public void Dispose()
        {
            assembly = null;
            Interlocked.Exchange(ref owner, null)?.Unload();
        }
    }
}
