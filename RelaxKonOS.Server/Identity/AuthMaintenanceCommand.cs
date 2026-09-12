using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Identity;

public static class AuthMaintenanceCommand
{
    public static FileStream AcquireLock(string databasePath)
        => new(Path.GetFullPath(databasePath) + ".identity.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[1] is not ("preflight" or "recover")) throw new InvalidOperationException("Use auth preflight or auth recover.");
            var command = args[1];
            var allowed = command == "preflight" ? new[] { "--database" } : new[] { "--database", "--user-id", "--enable-system-login", "--remove-alias" };
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var index = 2; index < args.Length; index++)
            {
                var key = args[index];
                if (!allowed.Contains(key) || options.ContainsKey(key)) throw new InvalidOperationException("Unknown or duplicate maintenance option.");
                options[key] = key is "--database" or "--user-id" && index + 1 < args.Length ? args[++index] : null;
            }
            if (!options.TryGetValue("--database", out var path) || path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new InvalidOperationException("An existing absolute --database path is required.");
            path = Path.GetFullPath(path);
            IIdentityProvider provider = OperatingSystem.IsWindows() ? new WindowsLogonProvider()
                : OperatingSystem.IsLinux() ? new LinuxPamProvider() : throw new InvalidOperationException("Unsupported host.");
            if (command == "preflight")
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
                var report = IdentityMigrationRunner.Preflight(connection, provider);
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return report.Any(x => x.Problem is not null) ? 2 : 0;
            }
            if (!IsAdministrator()) throw new InvalidOperationException("Recovery requires root or an elevated local Administrator.");
            if (!options.ContainsKey("--enable-system-login") || !options.ContainsKey("--remove-alias")
                || !options.TryGetValue("--user-id", out var rawId) || !Guid.TryParse(rawId, out var id))
                throw new InvalidOperationException("Recovery requires --user-id <GUID> --enable-system-login --remove-alias.");
            using var maintenanceLock = AcquireLock(path);
            using var db = new RelaxKonOSDbContext(new DbContextOptionsBuilder<RelaxKonOSDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString()).Options);
            // Recovery does not upgrade an old database or guess ownership.
            await db.Database.OpenConnectionAsync();
            using (var check = db.Database.GetDbConnection().CreateCommand())
            {
                check.CommandText = "SELECT HostBinding FROM identity_schema_migrations WHERE Version=1";
                if (check.ExecuteScalar() as string != IdentityMigrationRunner.HostBinding()) throw new InvalidOperationException("Database host binding mismatch.");
            }
            var user = db.Users.AsNoTracking().Single(x => x.Id == id);
            var identity = provider.LookupIdentity(user.PlatformIdentity).Identity;
            var name = provider.Lookup(user.Username).Identity;
            if (identity is null || name?.Uid != identity.Uid || identity.Platform != user.Platform)
                throw new InvalidOperationException("Repair and verify the OS account before recovery.");
            Console.WriteLine($"Database: {path}\nUser: {user.Id:D}\nSystem account: {identity.Username}\nUID/SID: {identity.Uid}\nType the user GUID to restore system login and remove the alias:");
            if (Console.ReadLine() != user.Id.ToString("D")) throw new InvalidOperationException("Recovery cancelled.");
            var repository = new SqliteAliasCredentialRepository(db);
            var credential = repository.Find(id) ?? new AliasCredential { UserId = id, CreatedAt = DateTimeOffset.UtcNow };
            var version = user.SecurityVersion;
            user.SecurityVersion = checked(version + 1); user.IdentityReviewRequired = false;
            credential.Alias = null; credential.PasswordHash = null; credential.PasswordChangedAt = null;
            credential.SystemLoginEnabled = true; credential.UpdatedAt = DateTimeOffset.UtcNow;
            // Repository clears this canonical cooldown in the same transaction as recovery and audit.
            if (!repository.Commit(credential, credential.Revision, user, version, new AuthenticationSecurityEvent
                { Id = Guid.NewGuid(), CanonicalUserId = id, ActorKind = "LocalRecovery", EventType = "AccountLoginRecovered",
                    ReasonCode = "local-administrator", CreatedAt = DateTimeOffset.UtcNow }))
                throw new InvalidOperationException("Recovery revision conflict.");

            Console.WriteLine("Recovery completed. Start the server and sign in with the current OS password.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is InvalidOperationException ? exception.Message : "Maintenance failed; verify permissions, database schema, host identity, and that the server is stopped.");
            return 1;
        }
    }
    public static bool IsAdministrator()
    {
        if (OperatingSystem.IsLinux()) return geteuid() == 0;
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    [DllImport("libc.so.6")] private static extern uint geteuid();
}
