using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Storage;

public interface IAliasCredentialRepository
{
    bool IsPersistent { get; }
    AliasCredential? Find(Guid userId);
    AliasCredential? FindAlias(string alias);
    bool Commit(AliasCredential credential, long expectedRevision, User user, long expectedSecurityVersion, AuthenticationSecurityEvent audit);
}

public sealed class SqliteAliasCredentialRepository(RelaxKonOSDbContext db) : IAliasCredentialRepository
{
    public bool IsPersistent => true;
    public AliasCredential? Find(Guid userId) => db.Set<AliasCredential>().AsNoTracking().SingleOrDefault(x => x.UserId == userId);
    public AliasCredential? FindAlias(string alias) => db.Set<AliasCredential>().AsNoTracking().SingleOrDefault(x => x.Alias == alias);

    public bool Commit(AliasCredential credential, long expectedRevision, User user, long expectedSecurityVersion, AuthenticationSecurityEvent audit)
    {
        using var transaction = db.Database.BeginTransaction();
        try
        {
            db.ChangeTracker.Clear();
            var current = db.Set<AliasCredential>().SingleOrDefault(x => x.UserId == user.Id);
            var currentUser = db.Users.Single(x => x.Id == user.Id);
            if ((current?.Revision ?? 0) != expectedRevision || currentUser.SecurityVersion != expectedSecurityVersion) return false;
            credential.Revision = checked(expectedRevision + 1);
            if (current is null) db.Add(credential);
            else db.Entry(current).CurrentValues.SetValues(credential);
            currentUser.SecurityVersion = user.SecurityVersion;
            currentUser.IdentityReviewRequired = user.IdentityReviewRequired;
            audit.Revision = credential.Revision;
            db.AuthenticationSecurityEvents.Add(audit);
            if (audit.EventType == "AccountLoginRecovered")
                db.AccountFailureStates.Where(x => x.AccountKey == "user:" + user.Id.ToString("D")).ExecuteDelete();
            db.SaveChanges();
            transaction.Commit();
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { return false; }
        finally { db.ChangeTracker.Clear(); }
    }
}

/// <summary>Memory development hosts cannot persist lockout policy or security versions.</summary>
public sealed class InMemoryAliasCredentialRepository : IAliasCredentialRepository
{
    public bool IsPersistent => false;
    public AliasCredential? Find(Guid userId) => null;
    public AliasCredential? FindAlias(string alias) => null;
    public bool Commit(AliasCredential credential, long expectedRevision, User user, long expectedSecurityVersion, AuthenticationSecurityEvent audit)
        => throw new InvalidOperationException("Alias management requires persistent storage.");
}
