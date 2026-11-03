using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Git;

/// <summary>Persistence port. Every normal read/write closes its context before executing Git.</summary>
public sealed class HostGitRepositoryStorage(IDbContextFactory<RelaxKonOSDbContext> factory) : IGitRepositoryStorage
{
    private const string CredentialAppId = "relaxkonos.git.internal";
    // Edits do no network/CLI work. One fixed gate preserves cross-instance serialization without
    // retaining an unbounded static dictionary of per-user locks across module generations.
    private static readonly SemaphoreSlim CredentialWrites = new(1, 1);

    private static GitRepositoryRecord Map(GitRepository value) => new()
    { Id = value.Id, UserId = value.UserId, Name = value.Name, Path = value.Path, CreatedAt = value.CreatedAt };

    public async Task<IReadOnlyList<GitRepositoryRecord>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return (await db.Set<GitRepository>().AsNoTracking().Where(value => value.UserId == userId)
            .OrderBy(value => value.Name).ToListAsync(cancellationToken)).Select(Map).ToArray();
    }

    public async Task<GitRepositoryRecord?> FindAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var value = await db.Set<GitRepository>().AsNoTracking().FirstOrDefaultAsync(value => value.Id == id && value.UserId == userId, cancellationToken);
        return value is null ? null : Map(value);
    }

    public async Task AddAsync(GitRepositoryRecord repository, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Add(new GitRepository { Id = repository.Id, UserId = repository.UserId, Name = repository.Name,
            Path = repository.Path, CreatedAt = repository.CreatedAt });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var value = await db.Set<GitRepository>().FirstOrDefaultAsync(value => value.Id == id && value.UserId == userId, cancellationToken);
        if (value is null) return false;
        db.Remove(value);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static IQueryable<AppSetting> CredentialSettings(RelaxKonOSDbContext db, Guid userId) => db.Set<AppSetting>().Where(value =>
        value.UserId == userId && value.Scope == AppSettingsScope.User && value.ScopeId == userId
        && value.AppId == CredentialAppId && value.Key.StartsWith("accounts-"));

    public async Task<IReadOnlyList<GitCredentialSetting>> CredentialsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await CredentialSettings(db, userId).AsNoTracking().Select(value => new GitCredentialSetting(value.Key, value.ValueJson)).ToArrayAsync(cancellationToken);
    }

    public async Task<GitCredentialSetting?> FindCredentialAsync(Guid userId, string key, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await CredentialSettings(db, userId).AsNoTracking().Where(value => value.Key == key)
            .Select(value => new GitCredentialSetting(value.Key, value.ValueJson)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IGitCredentialUpdate> BeginCredentialUpdateAsync(Guid userId, string? key, CancellationToken cancellationToken)
    {
        await CredentialWrites.WaitAsync(cancellationToken);
        RelaxKonOSDbContext? db = null;
        try
        {
            db = await factory.CreateDbContextAsync(cancellationToken);
            var query = CredentialSettings(db, userId);
            if (key is not null) query = query.Where(value => value.Key == key);
            var entries = await query.ToListAsync(cancellationToken);
            return new CredentialUpdate(db, userId, entries);
        }
        catch
        {
            try { if (db is not null) await db.DisposeAsync(); }
            finally { CredentialWrites.Release(); }
            throw;
        }
    }

    private sealed class CredentialUpdate(RelaxKonOSDbContext db, Guid userId, List<AppSetting> entries) : IGitCredentialUpdate
    {
        private RelaxKonOSDbContext? _db = db;
        public IReadOnlyList<GitCredentialSetting> Settings { get; private set; } = entries.Select(value => new GitCredentialSetting(value.Key, value.ValueJson)).ToArray();

        public async Task SaveAsync(string key, string? valueJson, CancellationToken cancellationToken)
        {
            var context = _db ?? throw new ObjectDisposedException(nameof(CredentialUpdate));
            if (!key.StartsWith("accounts-", StringComparison.Ordinal)) throw new ArgumentException("Invalid Git credential key.");
            var value = entries.SingleOrDefault(value => value.Key == key);
            if (valueJson is null)
            {
                if (value is not null) { context.Remove(value); entries.Remove(value); }
            }
            else if (value is null)
            {
                value = new AppSetting { UserId = userId, Scope = AppSettingsScope.User, ScopeId = userId,
                    AppId = CredentialAppId, Key = key, ValueJson = valueJson, SchemaVersion = 2,
                    Revision = 1, UpdatedAt = DateTimeOffset.UtcNow };
                context.Add(value); entries.Add(value);
            }
            else { value.ValueJson = valueJson; value.SchemaVersion = 2; value.Revision++; value.UpdatedAt = DateTimeOffset.UtcNow; }
            await context.SaveChangesAsync(cancellationToken);
            Settings = entries.Select(value => new GitCredentialSetting(value.Key, value.ValueJson)).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            var context = Interlocked.Exchange(ref _db, null);
            if (context is null) return;
            Settings = []; entries.Clear();
            try { await context.DisposeAsync(); }
            finally { CredentialWrites.Release(); }
        }
    }
}
