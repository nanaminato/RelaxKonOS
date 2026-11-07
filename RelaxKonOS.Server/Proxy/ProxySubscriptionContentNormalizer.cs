using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host call gate for an authorized subscription response body.</summary>
internal static class ProxySubscriptionContentNormalizer
{
    private static readonly ProxySubscriptionExecutionGate Gate = new();

    internal static Task<ProxySubscriptionBody> ReadAsync(Stream stream, CancellationToken ct, Action<WeakReference>? observe = null) =>
        Gate.ReadAsync(stream, ct, observe);
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
        ProxyExecutionLoadContext? context = null;
        try
        {
            await using var body = stream;
            ct.ThrowIfCancellationRequested();
            var path = ProxyExecutionLoadContext.BundledAssemblyPath();
            WeakReference code;
            lock (_gate)
            {
                _code.RemoveAll(reference => !reference.IsAlive);
                if (_code.Count >= MaxOutstandingContexts)
                    throw new InvalidDataException("Proxy subscription code collection is pending; retry after collection or arrange host recovery.");
                context = new ProxyExecutionLoadContext("subscription");
                code = new WeakReference(context);
                _code.Add(code);
            }
            observe?.Invoke(code);
            return await ConvertAsync(context, path, stream, ct);
        }
        finally
        {
            context?.Unload();
            context = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ProxySubscriptionBody> ConvertAsync(AssemblyLoadContext context, string path, Stream stream, CancellationToken ct)
    {
        try
        {
            var type = context.LoadFromAssemblyPath(path).GetType("RelaxKonOS.Server.Proxy.ProxySubscriptionNormalizer", true)!;
            return await ((IProxySubscriptionNormalizer)Activator.CreateInstance(type)!).ReadAsync(stream, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception)
        {
            // Do not leak a private exception/stack through a root operation record or logging pipeline.
            throw new InvalidDataException("Proxy subscription conversion failed.");
        }
    }

}
