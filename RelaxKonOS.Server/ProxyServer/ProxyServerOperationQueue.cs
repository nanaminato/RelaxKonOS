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
    public Task<ProxyOperationDto> EnqueueAsync(string actor, string key, string kind, object request,
        ProxyOperationStore operations, Func<CancellationToken, Task<string?>> action, CancellationToken ct) =>
        EnqueueIdentifiedAsync(actor, key, kind, request, operations, (_, token) => action(token), null, ct);

    internal async Task<ProxyOperationDto> EnqueueIdentifiedAsync(string actor, string key, string kind, object request,
        ProxyOperationStore operations, Func<Guid, CancellationToken, Task<string?>> action,
        Func<Guid, CancellationToken, Task<string?>>? recoverPublication, CancellationToken ct)
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
            ProxyOperationDto? item = null;
            if (recoverPublication is not null)
            {
                var accepted = state.Receipts.Values.Select(entry => entry.OperationId).ToHashSet();
                var pending = state.PendingQueuedPublication ? state.PendingPublicationOperationId : null;
                if (state.PendingQueuedPublication && (pending is not { } id || !accepted.Contains(id)))
                    throw new ProxyServerException("proxy_server.recovery_required");
                var known = state.QueuedPublicationCompletions.Keys.Where(accepted.Contains).ToHashSet();
                item = await operations.ResumeInboundPublicationAsync(pending, known, recoverPublication, ct);
                if (pending is not null && item is null)
                    throw new ProxyServerException("proxy_server.recovery_required");
            }
            item ??= await operations.EnqueueIdentifiedAsync("proxy-server:" + scope, "proxy_server." + kind, async (id, _, token) =>
            {
                try { return await action(id, token); }
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
