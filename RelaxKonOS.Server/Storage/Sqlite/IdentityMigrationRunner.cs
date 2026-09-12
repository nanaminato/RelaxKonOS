using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Storage.Sqlite;

public sealed record IdentityMigrationEntry(Guid UserId, string Username, string OldPlatform, string OldIdentity,
    Guid? WorkspaceId, PlatformUserInfo? Identity, string? Problem);

/// <summary>Raw reads deliberately precede EF queries against the new User schema.</summary>
public static class IdentityMigrationRunner
{
    public static IReadOnlyList<IdentityMigrationEntry> Preflight(DbConnection connection, IIdentityProvider provider)
    {
        if (connection.State != ConnectionState.Open) connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='users'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return [];
        var entries = new List<IdentityMigrationEntry>();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT u.Id,u.Username,u.Platform,u.PlatformIdentity,w.Id FROM users u LEFT JOIN workspaces w ON w.UserId=u.Id";
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                var lookup = provider.Lookup(reader.GetString(1));
                var oldIdentity = reader.GetString(3);
                var mismatch = lookup.Identity is { } info && (oldIdentity.StartsWith("S-1-", StringComparison.Ordinal) || uint.TryParse(oldIdentity, out _))
                    && oldIdentity != info.Uid;
                entries.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), oldIdentity,
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), lookup.Identity,
                    mismatch ? "identity-mismatch" : lookup.Status == IdentityLookupStatus.Found ? null : "identity-" + lookup.Status.ToString().ToLowerInvariant()));
            }
        }
        var duplicates = entries.Where(x => x.Identity is not null).GroupBy(x => (x.Identity!.Platform, x.Identity.Uid))
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.UserId)).ToHashSet();
        return entries.Select(x => duplicates.Contains(x.UserId) ? x with { Problem = "duplicate-canonical-identity" } : x).ToArray();
    }

    public static string HostBinding()
    {
        string machine;
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machine = key?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("Host identity unavailable.");
        }
        else machine = File.ReadAllText("/etc/machine-id").Trim();
        if (string.IsNullOrWhiteSpace(machine)) throw new InvalidOperationException("Host identity unavailable.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine)));
    }

    public static void Migrate(RelaxKonOSDbContext db, IIdentityProvider provider)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='identity_schema_migrations'";
        if (Convert.ToInt32(check.ExecuteScalar()) != 0)
        {
            check.CommandText = "SELECT HostBinding FROM identity_schema_migrations WHERE Version=1";
            if (check.ExecuteScalar() is string binding)
            {
                if (binding != HostBinding()) throw new InvalidOperationException("Identity database belongs to another host. Restore requires operator verification.");
                ValidateSchema(connection);
                return;
            }
        }
        var report = Preflight(connection, provider);
        if (report.Any(x => x.Problem is not null))
            throw new InvalidOperationException("Identity preflight failed. Run auth preflight --database <absolute path>; resolve reported ownership conflicts before upgrading.");
        using var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS identity_schema_migrations (Version INTEGER PRIMARY KEY, HostBinding TEXT NOT NULL, AppliedAt TEXT NOT NULL)");
        AddColumn(db, "users", "SecurityVersion", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "users", "IdentityReviewRequired", "INTEGER NOT NULL DEFAULT 0");
        foreach (var entry in report)
        {
            var identity = entry.Identity!;
            db.Database.ExecuteSqlInterpolated($"UPDATE users SET Username={identity.Username}, Platform={identity.Platform.ToString()}, PlatformIdentity={identity.Uid} WHERE Id={entry.UserId}");
        }
        db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_users_Username_Platform");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_users_Platform_PlatformIdentity ON users(Platform,PlatformIdentity)");
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS user_login_credentials (
                UserId TEXT NOT NULL PRIMARY KEY REFERENCES users(Id) ON DELETE RESTRICT,
                Alias TEXT NULL, PasswordHash TEXT NULL, SystemLoginEnabled INTEGER NOT NULL DEFAULT 1,
                Revision INTEGER NOT NULL CHECK(Revision > 0), CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, PasswordChangedAt TEXT NULL,
                CONSTRAINT CK_alias_pair CHECK((Alias IS NULL AND PasswordHash IS NULL) OR (Alias IS NOT NULL AND PasswordHash IS NOT NULL)),
                CONSTRAINT CK_login_method CHECK(SystemLoginEnabled=1 OR (Alias IS NOT NULL AND PasswordHash IS NOT NULL)),
                CONSTRAINT CK_alias_format CHECK(Alias IS NULL OR (length(Alias) BETWEEN 3 AND 32 AND Alias NOT GLOB '*[^a-z0-9._-]*' AND substr(Alias,1,1) GLOB '[a-z]')));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_user_login_credentials_Alias ON user_login_credentials(Alias);
            """);
        // Existing authentication tables are created by the host-global migration before this runner.
        foreach (var column in new[] { "CanonicalUserId", "SessionId", "AuthenticationMethod", "ReasonCode", "CorrelationId" })
            AddColumn(db, "authentication_security_events", column, "TEXT NULL");
        AddColumn(db, "authentication_security_events", "Revision", "INTEGER NULL");
        AddColumn(db, "authentication_security_events", "ActorKind", "TEXT NOT NULL DEFAULT 'User'");
        db.Database.ExecuteSqlInterpolated($"INSERT INTO identity_schema_migrations(Version,HostBinding,AppliedAt) VALUES(1,{HostBinding()},{DateTimeOffset.UtcNow})");
        ValidateSchema(connection);
        tx.Commit();
    }

    private static void AddColumn(RelaxKonOSDbContext db, string table, string column, string type)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info({table})";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (reader.GetString(1) == column) return;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}"; // All identifiers/types are migration constants.
        command.ExecuteNonQuery();
    }

    private static void ValidateSchema(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SecurityVersion,IdentityReviewRequired FROM users LIMIT 0";
        using (command.ExecuteReader()) { }
        command.CommandText = "SELECT UserId,Alias,PasswordHash,SystemLoginEnabled,Revision,CreatedAt,UpdatedAt,PasswordChangedAt FROM user_login_credentials LIMIT 0";
        using (command.ExecuteReader()) { }
        command.CommandText = "PRAGMA foreign_key_check(user_login_credentials)";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException("Identity credential foreign key validation failed.");
    }
}
