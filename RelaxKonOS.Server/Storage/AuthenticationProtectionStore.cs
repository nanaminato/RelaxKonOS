using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Storage;

public sealed class InMemoryAuthenticationProtectionStore : IAuthenticationProtectionStore
{
    private readonly ConcurrentDictionary<string, AccountFailureState> _accounts = new(StringComparer.Ordinal);
    public Task<AccountFailureState?> FindAccountAsync(string key, CancellationToken ct) => Task.FromResult(_accounts.TryGetValue(key, out var value) ? value : null);
    public Task SaveAccountAsync(AccountFailureState state, CancellationToken ct) { _accounts[state.AccountKey] = state; return Task.CompletedTask; }
    public Task AddEventAsync(AuthenticationSecurityEvent entry, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SqliteAuthenticationProtectionStore(RelaxKonOSDbContext db) : IAuthenticationProtectionStore
{
    private static long _lastAnonymousSecond;
    public Task<AccountFailureState?> FindAccountAsync(string key, CancellationToken ct) => key.StartsWith("unknown:", StringComparison.Ordinal) ? Task.FromResult<AccountFailureState?>(null) : db.AccountFailureStates.FindAsync([key], ct).AsTask();
    public async Task SaveAccountAsync(AccountFailureState state, CancellationToken ct)
    {
        if (state.AccountKey.StartsWith("unknown:", StringComparison.Ordinal)) return;
        if (await db.AccountFailureStates.FindAsync([state.AccountKey], ct) is null) db.AccountFailureStates.Add(state);
        else db.AccountFailureStates.Update(state);
        await db.SaveChangesAsync(ct);
    }
    public async Task AddEventAsync(AuthenticationSecurityEvent entry, CancellationToken ct)
    {
        // Bounded failure audit: at most one anonymous event per second on this host.
        if (entry.AccountKey?.StartsWith("unknown:", StringComparison.Ordinal) == true
            && Interlocked.Exchange(ref _lastAnonymousSecond, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;
        db.AuthenticationSecurityEvents.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
