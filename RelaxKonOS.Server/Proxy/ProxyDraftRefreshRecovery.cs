using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

internal sealed class ProxyDraftRefreshRecovery(IProxyPlatformPaths paths, IProxySubscriptionRepository subscriptions,
    MihomoPublicationStore publication)
{
    private const string MarkerPrefix = "# RelaxKonOS draft refresh ";

    private static string Marker(Guid operationId) => $"{MarkerPrefix}{operationId:N}\n";
    internal static string Mark(string yaml, Guid operationId) => Marker(operationId) + yaml;

    internal static string RemoveMarker(string yaml)
    {
        var end = MarkerPrefix.Length + 32;
        return yaml.Length > end && yaml.StartsWith(MarkerPrefix, StringComparison.Ordinal) && yaml[end] == '\n'
            && Guid.TryParseExact(yaml.AsSpan(MarkerPrefix.Length, 32), "N", out _)
            ? yaml[(end + 1)..] : yaml;
    }

    internal static string ProfilePath(IProxyPlatformPaths paths, Guid profileId) =>
        Path.Combine(paths.GetProtectedConfigurationDirectory(), "profiles", profileId.ToString("N") + ".yaml");

    internal static string StagedPath(IProxyPlatformPaths paths, Guid operationId) =>
        Path.Combine(paths.GetProtectedConfigurationDirectory(), "profiles", ".draft-" + operationId.ToString("N"));

    internal async Task<(bool Pending, ProxyDraftRefreshReceipt? Receipt)> ReconcileAsync(Guid operationId, Guid? subscriptionId, CancellationToken ct)
    {
        try
        {
            var receipt = await subscriptions.ReadDraftRefreshReceiptAsync(operationId, subscriptionId, ct);
            if (receipt is not null) return (false, receipt);
            var intent = await subscriptions.ReadDraftRefreshIntentAsync(operationId, ct);
            if (intent is null || subscriptionId is { } expected && intent.SubscriptionId != expected) return default;

            // Do not hold the queue gate waiting for a live publication or another draft writer.
            if (!await publication.Gate.WaitAsync(0, ct)) return (true, null);
            try
            {
                var current = await subscriptions.ReadDraftRefreshIntentAsync(operationId, ct);
                if (current != intent)
                {
                    var receiptAfterLock = await subscriptions.ReadDraftRefreshReceiptAsync(operationId, subscriptionId, ct);
                    return (receiptAfterLock is null, receiptAfterLock);
                }
                var target = ProfilePath(paths, intent.ProfileId);
                if (!intent.Abandoning && await ReadIdentityAsync(target, ct, intent.OperationId) == intent.Identity)
                {
                    if (await ReadIdentityAsync(StagedPath(paths, intent.OperationId), ct, intent.OperationId) is not null)
                        return (true, null);
                    if (await subscriptions.CommitPreparedDraftRefreshAsync(intent, CancellationToken.None))
                        return (false, new(true, intent.RetrievedAt));
                }
                else if (await AbandonUnappliedAsync(intent, target, ct) is { } abandoned)
                    return (false, abandoned);
                var committed = await subscriptions.ReadDraftRefreshReceiptAsync(operationId, subscriptionId, ct);
                return (committed is null, committed);
            }
            finally { publication.Gate.Release(); }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or RuntimePolicyException)
        { return (true, null); }
    }

    private async Task<ProxyDraftRefreshReceipt?> AbandonUnappliedAsync(ProxyDraftRefreshIntent intent, string target, CancellationToken ct)
    {
        if (intent.PreviousIdentity is not { } previous || await ReadIdentityAsync(target, ct) != previous) return null;
        var staged = StagedPath(paths, intent.OperationId);
        var identity = await ReadIdentityAsync(staged, ct, intent.OperationId);
        if (!intent.Abandoning)
        {
            if (identity != intent.Identity || !await subscriptions.BeginDraftAbandonmentAsync(intent, CancellationToken.None))
                return null;
            identity = await ReadIdentityAsync(staged, ct, intent.OperationId);
        }
        // The durable abandonment decision makes a missing stage retryable after cleanup.
        if (identity is not null)
        {
            if (identity != intent.Identity) return null;
            File.Delete(staged);
        }
        if (await ReadIdentityAsync(target, ct) != previous) return null;
        var completedAt = DateTimeOffset.UtcNow;
        return await subscriptions.CompleteDraftAbandonmentAsync(intent, completedAt, CancellationToken.None)
            ? new(false, completedAt) : null;
    }

    internal static async Task<string?> ReadIdentityAsync(string path, CancellationToken ct, Guid? operationId = null)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new RuntimePolicyException("runtime.linked_path_forbidden");
            RuntimeSourceStore.RequireNoLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > 5 * 1024 * 1024) throw new InvalidDataException("Invalid draft file length.");
            if (operationId is { } id)
            {
                var expected = Encoding.UTF8.GetBytes(Marker(id));
                if (input.Length < expected.Length) return "";
                var prefix = new byte[expected.Length];
                await input.ReadExactlyAsync(prefix, ct);
                if (!prefix.AsSpan().SequenceEqual(expected)) return "";
                input.Position = 0;
            }
            return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
