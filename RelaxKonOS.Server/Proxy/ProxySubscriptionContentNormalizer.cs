using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host call gate for an authorized subscription response body.</summary>
internal static class ProxySubscriptionContentNormalizer
{
    private static readonly ProxySubscriptionExecutionGate Gate = new();

    internal static Task<ProxySubscriptionBody> ReadAsync(Stream stream, CancellationToken ct, Action<WeakReference>? observe = null) =>
        Gate.ReadAsync(stream, ct, observe);

    internal static async Task<ProxySubscriptionBody> ReadOwnedBodyAsync(IProxySubscriptionNormalizer normalizer,
        Stream stream, CancellationToken ct)
    {
        await using var body = stream;
        return await ConvertAsync(normalizer, stream, ct);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<ProxySubscriptionBody> ConvertAsync(IProxySubscriptionNormalizer normalizer, Stream stream, CancellationToken ct)
    {
        try { return await normalizer.ReadAsync(stream, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception) { throw new InvalidDataException("Proxy subscription conversion failed."); }
    }

    internal static ProxySubscriptionExecutionGate.Lease Acquire() => Gate.Acquire();
}

/// <summary>Bounds active and not-yet-collected conversion contexts without forcing GC.</summary>
internal sealed class ProxySubscriptionExecutionGate
{
    internal const int MaxOutstandingContexts = 16;
    private readonly object _gate = new();
    private readonly List<WeakReference> _code = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal async Task<ProxySubscriptionBody> ReadAsync(Stream stream, CancellationToken ct, Action<WeakReference>? observe = null)
    {
        ProxySubscriptionExecutionGate.Lease? code = null;
        try
        {
            await using var body = stream;
            ct.ThrowIfCancellationRequested();
            code = Acquire();
            observe?.Invoke(code.Reference);
            return await ProxySubscriptionContentNormalizer.ConvertAsync(
                code.Create<IProxySubscriptionNormalizer>("RelaxKonOS.Server.Proxy.ProxySubscriptionNormalizer"), stream, ct);
        }
        finally { code?.Dispose(); }
    }

    internal Lease Acquire()
    {
        var path = ProxyExecutionLoadContext.BundledAssemblyPath();
        lock (_gate)
        {
            _code.RemoveAll(reference => !reference.IsAlive);
            if (_code.Count >= MaxOutstandingContexts)
                throw new InvalidDataException("Proxy subscription code collection is pending; retry after collection or arrange host recovery.");
            var context = new ProxyExecutionLoadContext("subscription");
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
            catch (Exception) { throw new InvalidDataException("Proxy subscription execution could not be activated."); }
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Unload();
    }
}
