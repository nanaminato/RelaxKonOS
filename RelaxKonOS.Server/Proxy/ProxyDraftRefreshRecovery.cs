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

    internal async Task<(bool Pending, DateTimeOffset? CompletedAt)> ReconcileAsync(Guid operationId, Guid? subscriptionId, CancellationToken ct)
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
                if (!await MatchesAsync(intent, ct)) return (true, null);
                if (await subscriptions.CommitPreparedDraftRefreshAsync(intent, CancellationToken.None))
                    return (false, intent.RetrievedAt);
                var committed = await subscriptions.ReadDraftRefreshReceiptAsync(operationId, subscriptionId, ct);
                return (committed is null, committed);
            }
            finally { publication.Gate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or RuntimePolicyException)
        { return (true, null); }
    }

    private async Task<bool> MatchesAsync(ProxyDraftRefreshIntent intent, CancellationToken ct)
    {
        var path = ProfilePath(paths, intent.ProfileId);
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var expected = Encoding.UTF8.GetBytes(Marker(intent.OperationId));
        if (input.Length < expected.Length || input.Length > 5 * 1024 * 1024) return false;
        var prefix = new byte[expected.Length];
        await input.ReadExactlyAsync(prefix, ct);
        if (!prefix.AsSpan().SequenceEqual(expected)) return false;
        input.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) == intent.Identity;
    }
}
