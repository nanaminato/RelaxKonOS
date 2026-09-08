using System.Collections.Concurrent;
using System.Net;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Identity;

public interface IAuthenticationProtectionStore
{
    Task<AccountFailureState?> FindAccountAsync(string accountKey, CancellationToken cancellationToken);
    Task SaveAccountAsync(AccountFailureState state, CancellationToken cancellationToken);
    Task AddEventAsync(AuthenticationSecurityEvent entry, CancellationToken cancellationToken);
}

public sealed record LoginProtectionDecision(bool IsBlocked, DateTimeOffset? RetryAt)
{
    public static readonly LoginProtectionDecision Allowed = new(false, null);
}

/// <summary>三维登录保护：持久化账号状态，加上短生命周期 IP 与账号+IP 状态。</summary>
public sealed class LoginProtectionService(
    IAuthenticationProtectionStore store,
    Microsoft.Extensions.Options.IOptions<AuthSecurityOptions> options)
{
    private static readonly ConcurrentDictionary<string, TransientFailureState> IpFailures = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TransientFailureState> AccountIpFailures = new(StringComparer.Ordinal);
    private readonly AuthSecurityOptions _options = options.Value;

    public async Task<LoginProtectionDecision> CheckAsync(string username, IPAddress? sourceIp, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var key = AccountKey(username);
        var ipKey = IpKey(sourceIp);
        var account = await store.FindAccountAsync(key, ct);
        var accountIp = AccountIpFailures.TryGetValue(key + "|" + ipKey, out var pair) ? pair : null;
        var ip = IpFailures.TryGetValue(ipKey, out var source) ? source : null;
        var retryAt = new[] { account?.BlockedUntil, accountIp?.BlockedUntil, ip?.BlockedUntil }
            .Where(value => value.HasValue && value.Value > now).Max();
        return retryAt is null ? LoginProtectionDecision.Allowed : new LoginProtectionDecision(true, retryAt);
    }

    public async Task RecordFailureAsync(string username, IPAddress? sourceIp, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var key = AccountKey(username);
        var ipKey = IpKey(sourceIp);
        var account = await store.FindAccountAsync(key, ct) ?? new AccountFailureState { AccountKey = key };
        if (account.LastFailureAt < now.AddHours(-_options.AccountFailureRetentionHours))
            account.FailureCount = 0;
        account.FirstFailureAt = account.FailureCount == 0 ? now : account.FirstFailureAt;
        account.FailureCount++;
        account.LastFailureAt = now;
        account.BlockedUntil = Max(account.BlockedUntil, now.Add(PenaltyFor(account.FailureCount)));
        await store.SaveAccountAsync(account, ct);

        var pair = AccountIpFailures.GetOrAdd(key + "|" + ipKey, _ => new TransientFailureState(now));
        pair.Record(now, _options.IpFailureWindowMinutes, PenaltyFor(pair.FailureCount + 1));
        var ip = IpFailures.GetOrAdd(ipKey, _ => new TransientFailureState(now));
        ip.Record(now, _options.IpFailureWindowMinutes,
            ip.FailureCount + 1 >= _options.IpFailureLimit ? TimeSpan.FromMinutes(_options.IpBlockMinutes) : TimeSpan.Zero);
        await EventAsync("authentication_failed", key, ipKey, now, ct);
    }

    public async Task RecordSuccessAsync(string username, IPAddress? sourceIp, CancellationToken ct)
    {
        var key = AccountKey(username);
        var now = DateTimeOffset.UtcNow;
        var account = await store.FindAccountAsync(key, ct);
        if (account is not null)
        {
            account.FailureCount = 0;
            account.FirstFailureAt = default;
            account.LastFailureAt = default;
            account.BlockedUntil = null;
            await store.SaveAccountAsync(account, ct);
        }
        AccountIpFailures.TryRemove(key + "|" + IpKey(sourceIp), out _);
        await EventAsync("authentication_succeeded", key, IpKey(sourceIp), now, ct);
    }

    public Task RecordBlockedAsync(string username, IPAddress? sourceIp, CancellationToken ct)
        => EventAsync("authentication_rate_limited", AccountKey(username), IpKey(sourceIp), DateTimeOffset.UtcNow, ct);

    private Task EventAsync(string type, string account, string ip, DateTimeOffset now, CancellationToken ct) =>
        store.AddEventAsync(new AuthenticationSecurityEvent { Id = Guid.NewGuid(), EventType = type, AccountKey = account, SourceIp = ip, CreatedAt = now }, ct);

    private static string AccountKey(string username)
    {
        var normalized = (username ?? string.Empty).Trim().ToUpperInvariant();
        return normalized[..Math.Min(normalized.Length, 128)];
    }
    private static string IpKey(IPAddress? ip) => ip?.MapToIPv6().ToString() ?? "unknown";
    private static DateTimeOffset? Max(DateTimeOffset? existing, DateTimeOffset candidate) => existing is null || existing < candidate ? candidate : existing;
    private static TimeSpan PenaltyFor(int failures) => failures switch
    {
        < 5 => TimeSpan.Zero, 5 => TimeSpan.FromSeconds(2), 6 => TimeSpan.FromSeconds(5),
        7 => TimeSpan.FromSeconds(15), 8 => TimeSpan.FromSeconds(30), 9 => TimeSpan.FromMinutes(1),
        10 => TimeSpan.FromMinutes(5), 11 => TimeSpan.FromMinutes(10), 12 => TimeSpan.FromMinutes(15),
        13 => TimeSpan.FromMinutes(20), 14 => TimeSpan.FromMinutes(30), _ => TimeSpan.FromHours(1)
    };

    private sealed class TransientFailureState(DateTimeOffset now)
    {
        public int FailureCount { get; private set; }
        public DateTimeOffset WindowStartedAt { get; private set; } = now;
        public DateTimeOffset? BlockedUntil { get; private set; }
        public void Record(DateTimeOffset now, int windowMinutes, TimeSpan penalty)
        {
            lock (this)
            {
                if (WindowStartedAt < now.AddMinutes(-windowMinutes)) { WindowStartedAt = now; FailureCount = 0; BlockedUntil = null; }
                FailureCount++;
                if (penalty > TimeSpan.Zero) BlockedUntil = Max(BlockedUntil, now.Add(penalty));
            }
        }
    }
}
