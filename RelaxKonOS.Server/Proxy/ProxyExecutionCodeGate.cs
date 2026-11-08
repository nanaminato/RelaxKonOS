using System.Runtime.Loader;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Bounds active and not-yet-collected execution contexts without forcing GC.</summary>
internal sealed class ProxyExecutionCodeGate(string purpose)
{
    internal const int MaxOutstandingContexts = 16;
    private readonly object _gate = new();
    private readonly List<WeakReference> _code = [];

    internal Lease Acquire()
    {
        var path = ProxyExecutionLoadContext.BundledAssemblyPath();
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
        internal WeakReference Reference => reference;

        internal T Create<T>(string name, params object[] args)
        {
            try
            {
                var code = owner ?? throw new ObjectDisposedException(nameof(Lease));
                var type = code.LoadFromAssemblyPath(path).GetType(name, throwOnError: true)!;
                return (T)Activator.CreateInstance(type, args)!;
            }
            catch (Exception) { throw new InvalidDataException("Proxy execution could not be activated."); }
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Unload();
    }
}
