exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
edit('RelaxKonOS.Server/Program.cs','// `dotnet run` normally', '''if (args.FirstOrDefault() == "auth") { Environment.ExitCode = await AuthMaintenanceCommand.RunAsync(args); return; }

// `dotnet run` normally''')
edit('RelaxKonOS.Server/Program.cs','if (storageProvider == "sqlite")\n{','''using var identityHostLock = storageProvider == "sqlite"
    ? AcquireIdentityHostLock(Path.Combine(builder.Environment.ContentRootPath, storageOpts.DatabasePath)) : null;
if (storageProvider == "sqlite")
{''')
with open('RelaxKonOS.Server/Program.cs','a',encoding='utf-8') as f: f.write('''
static FileStream AcquireIdentityHostLock(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    return AuthMaintenanceCommand.AcquireLock(path);
}
''')
p='RelaxKonOS.Server/Identity/LoginProtectionService.cs'
edit(p,'    private static readonly ConcurrentDictionary<string, TransientFailureState> IpFailures', '''    private static readonly SemaphoreSlim Mutation = new(1, 1);
    private static readonly byte[] FingerprintKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private const int MaximumBuckets = 10000;
    private static readonly ConcurrentDictionary<string, TransientFailureState> IpFailures''')
edit(p,'        var now = DateTimeOffset.UtcNow;','        Cleanup();\n        var now = DateTimeOffset.UtcNow;')
edit(p,'    private static string AccountKey(string username)\n    {\n        var normalized = (username ?? string.Empty).Trim().ToUpperInvariant();\n        return normalized[..Math.Min(normalized.Length, 128)];\n    }','''    internal static string AccountKey(string identifier)
    {
        if (Guid.TryParseExact(identifier, "D", out var id)) return id.ToString("D");
        return "unknown:" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(FingerprintKey,
            System.Text.Encoding.UTF8.GetBytes(identifier ?? "")));
    }
    private static void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var buckets in new[] { IpFailures, AccountIpFailures })
        {
            foreach (var entry in buckets)
                if (entry.Value.WindowStartedAt < cutoff) buckets.TryRemove(entry.Key, out _);
        }
    }''')
# Serialize repository read-modify-write across scoped instances, including transient penalty calculation.
for name, nextname in [('RecordFailureAsync','RecordSuccessAsync'),('RecordSuccessAsync','RecordBlockedAsync')]:
 s=Path(p).read_text(); a=s.index('    public async Task '+name); b=s.index('    public ',a+10); chunk=s[a:b]; opening=chunk.index('    {')+5; closing=chunk.rfind('    }'); chunk=chunk[:opening]+'\n        await Mutation.WaitAsync(ct);\n        try\n        {'+chunk[opening:closing]+'        }\n        finally { Mutation.Release(); }\n'+chunk[closing:]; s=s[:a]+chunk+s[b:]; Path(p).write_text(s,encoding='utf-8')
edit(p,'var pair = AccountIpFailures.GetOrAdd(key + "|" + ipKey,','var pair = AccountIpFailures.GetOrAdd(AccountIpFailures.Count >= MaximumBuckets ? "overflow" : key + "|" + ipKey,')
edit(p,'var ip = IpFailures.GetOrAdd(ipKey,','var ip = IpFailures.GetOrAdd(IpFailures.Count >= MaximumBuckets ? "overflow" : ipKey,')
edit(p,'var ip = IpFailures.TryGetValue(ipKey,','var ip = IpFailures.TryGetValue(IpFailures.Count >= MaximumBuckets && !IpFailures.ContainsKey(ipKey) ? "overflow" : ipKey,')
# Unknown identifiers stay in bounded transient buckets instead of filling SQLite.
edit('RelaxKonOS.Server/Storage/AuthenticationProtectionStore.cs','public Task<AccountFailureState?> FindAccountAsync(string key, CancellationToken ct) => db.AccountFailureStates.FindAsync([key], ct).AsTask();','public Task<AccountFailureState?> FindAccountAsync(string key, CancellationToken ct) => key.StartsWith("unknown:", StringComparison.Ordinal) ? Task.FromResult<AccountFailureState?>(null) : db.AccountFailureStates.FindAsync([key], ct).AsTask();')
edit('RelaxKonOS.Server/Storage/AuthenticationProtectionStore.cs','        if (await db.AccountFailureStates.FindAsync','        if (state.AccountKey.StartsWith("unknown:", StringComparison.Ordinal)) return;\n        if (await db.AccountFailureStates.FindAsync')
edit('RelaxKonOS.Server/Storage/AuthenticationProtectionStore.cs','        db.AuthenticationSecurityEvents.Add(entry);','''        // Bounded failure audit: at most one anonymous event per second on this host.
        if (entry.AccountKey?.StartsWith("unknown:", StringComparison.Ordinal) == true
            && Interlocked.Exchange(ref _lastAnonymousSecond, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;
        db.AuthenticationSecurityEvents.Add(entry);''')
edit('RelaxKonOS.Server/Storage/AuthenticationProtectionStore.cs','public sealed class SqliteAuthenticationProtectionStore(RelaxKonOSDbContext db) : IAuthenticationProtectionStore\n{','public sealed class SqliteAuthenticationProtectionStore(RelaxKonOSDbContext db) : IAuthenticationProtectionStore\n{\n    private static long _lastAnonymousSecond;')
