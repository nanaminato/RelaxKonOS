using System.Runtime.CompilerServices;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host call gate for an authorized subscription response body.</summary>
internal static class ProxySubscriptionContentNormalizer
{
    internal static ProxyExecutionCodeGate BundledGate { get; } = new("subscription");

    internal static Task<ProxySubscriptionBody> ReadAsync(Stream stream, CancellationToken ct, Action<WeakReference>? observe = null) =>
        BundledGate.ReadAsync(stream, ct, observe);

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<ProxySubscriptionBody> ReadAsync(this ProxyExecutionCodeGate gate, Stream stream, CancellationToken ct, Action<WeakReference>? observe = null)
    {
        ProxyExecutionCodeGate.Lease? code = null;
        try
        {
            await using var body = stream;
            ct.ThrowIfCancellationRequested();
            code = gate.Acquire();
            observe?.Invoke(code.Reference);
            return await ProxySubscriptionContentNormalizer.ConvertAsync(
                code.Create<IProxySubscriptionNormalizer>("RelaxKonOS.Server.Proxy.ProxySubscriptionNormalizer"), stream, ct);
        }
        finally { code?.Dispose(); }
    }
}
