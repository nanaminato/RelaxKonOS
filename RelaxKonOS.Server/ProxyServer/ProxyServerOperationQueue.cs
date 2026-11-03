using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.ProxyServer;

/// <summary>Protected, actor-scoped acceptance receipts. Does not construct management engines.</summary>
public sealed class ProxyServerOperationQueue(ProxyServerStore store)
{
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromDays(7);
    public async Task<ProxyOperationDto> EnqueueAsync(string actor, string key, string kind, object request,
        ProxyOperationStore operations, Func<CancellationToken, Task<string?>> action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ProxyServerException("proxy_server.idempotency_key_required");
        var scope = Hash(actor + "\n" + key);
        var fingerprint = Hash(kind + "\n" + JsonSerializer.Serialize(request));
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Receipts.TryGetValue(scope, out var receipt))
            {
                if (DateTimeOffset.UtcNow - receipt.IssuedAt > ReceiptLifetime || receipt.Fingerprint != fingerprint)
                    throw new ProxyServerException("proxy_server.idempotency_conflict");
                if (receipt.OperationId != Guid.Empty)
                    return await operations.GetAsync(receipt.OperationId, ct) ?? throw new ProxyServerException("proxy_server.operation_unavailable");
            }
            else
            {
                if (state.Receipts.Count >= 10000) throw new ProxyServerException("proxy_server.operation_limit");
                state.Receipts.Add(scope, new(fingerprint, Guid.Empty, DateTimeOffset.UtcNow));
                await store.WriteAsync(state, CancellationToken.None);
            }
            var item = await operations.EnqueueAsync("proxy-server:" + scope, "proxy_server." + kind, async (_, token) =>
            {
                try { return await action(token); }
                catch (ProxyServerException ex) { return ex.Message; }
            }, ct);
            // Re-read under the same gate: the queued action may already have committed a newer revision.
            var latest = await store.ReadAsync(ct);
            latest.Receipts[scope] = new(fingerprint, item.OperationId, DateTimeOffset.UtcNow);
            await store.WriteAsync(latest, CancellationToken.None);
            return item;
        }
        finally { store.Gate.Release(); }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
