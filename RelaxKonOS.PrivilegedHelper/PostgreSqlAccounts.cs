using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.ManagedRuntimes.PostgreSql;

public static partial class PrivilegedOperationExecutor
{
    private static string PgId(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string PgLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string PgOwner(string database) => "rk_owner_" + database;
    private const string PgWitnessSchema = "CREATE SCHEMA IF NOT EXISTS rk_db_control AUTHORIZATION postgres; REVOKE ALL ON SCHEMA rk_db_control FROM PUBLIC; "
        + "CREATE TABLE IF NOT EXISTS rk_db_control.operations(id uuid PRIMARY KEY, identity text NOT NULL); REVOKE ALL ON rk_db_control.operations FROM PUBLIC; ";
    private static async Task<PgCommandResult> PgSql(PgLedger ledger, string database, string sql, bool allowFailure = false)
    {
        var plan = PostgreSqlInitializationPlanner.Create(ledger.Instance!.Value, ledger.Port);
        return await PgRun("/usr/sbin/runuser", ["-u", "postgres", "--", "/usr/lib/postgresql/16/bin/psql", "-XqAtw", "-v", "ON_ERROR_STOP=1",
            "-h", plan.SocketDirectory, "-p", ledger.Port.ToString(CultureInfo.InvariantCulture), "-d", database], allowFailure: allowFailure,
            input: "SET log_statement='none'; SET log_min_error_statement='panic'; SET log_min_messages='panic'; SET statement_timeout='30s'; SET lock_timeout='10s';\n" + sql);
    }
    private static async Task<string> PgScalar(PgLedger ledger, string database, string sql) => (await PgSql(ledger, database, sql)).Output.Trim();
    private static async Task PgAccountPreflight(PgLedger ledger, PostgreSqlAccountCommand command, PostgreSqlSecrets? secrets)
    {
        if (command.Permission == PostgreSqlPermission.Migration && ledger.RemoteAccess?.Account == command.Account)
            throw new PgFailure("database.remote_request_invalid");
        if (!PostgreSqlAccountValidation.Valid(command) || command.InstanceId != ledger.Instance || command.ExpectedRevision != ledger.Revision)
            throw new PgFailure("database.plan_changed");
        await PgVerifyPackage(ledger); await PgVerifyConnection(ledger);
        if (ledger.Databases.Count >= 32 && command.Action == PostgreSqlAccountAction.CreateDatabase
            || ledger.Accounts.Count >= 64 && command.Action == PostgreSqlAccountAction.CreateAccount) throw new PgFailure("database.state_capacity");
        if (command.Action == PostgreSqlAccountAction.CreateDatabase)
        {
            if (secrets is not null || ledger.Databases.Any(x => x.Name == command.Database)
                || await PgScalar(ledger, "postgres", $"SELECT count(*) FROM pg_database WHERE datname={PgLiteral(command.Database)};") != "0"
                || await PgScalar(ledger, "postgres", $"SELECT count(*) FROM pg_roles WHERE rolname={PgLiteral(PgOwner(command.Database))};") != "0")
                throw new PgFailure("database.resource_exists");
            return;
        }
        var database = ledger.Databases.SingleOrDefault(x => x.Name == command.Database) ?? throw new PgFailure("database.resource_unmanaged");
        await PgVerifyDatabase(ledger, database);
        if (await PgScalar(ledger, "postgres", $"SELECT count(*) FROM pg_database d WHERE d.datallowconn AND d.datname<>{PgLiteral(command.Database)} AND EXISTS (SELECT 1 FROM aclexplode(coalesce(d.datacl,acldefault('d',d.datdba))) a WHERE a.grantee=0 AND a.privilege_type='CONNECT');") != "0")
            throw new PgFailure("database.grant_drift");
        if (secrets is null || !PostgreSqlAccountValidation.Password(secrets.Password)) throw new PgFailure("database.credential_unavailable");
        if (command.Action == PostgreSqlAccountAction.CreateAccount)
        {
            if (ledger.Accounts.Any(x => x.Name == command.Account) || await PgScalar(ledger, "postgres", $"SELECT count(*) FROM pg_roles WHERE rolname={PgLiteral(command.Account!)};") != "0")
                throw new PgFailure("database.resource_exists");
        }
        else
        {
            var account = ledger.Accounts.SingleOrDefault(x => x.Name == command.Account && x.Database == command.Database) ?? throw new PgFailure("database.resource_unmanaged");
            await PgVerifyAccount(ledger, account);
            if (command.Action == PostgreSqlAccountAction.ChangePassword && (secrets.PreviousPassword is null || secrets.Password == secrets.PreviousPassword))
                throw new PgFailure("database.credential_unchanged");
        }
    }
    private static async Task PgVerifyDatabase(PgLedger ledger, PostgreSqlDatabase database, Func<string,string,Task<string>>? query=null)
    {
        query??=(databaseName,sql)=>PgScalar(ledger,databaseName,sql);
        var actual = await query("postgres", $"SELECT d.oid||'|'||d.datdba||'|'||r.rolname FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba WHERE d.datname={PgLiteral(database.Name)};");
        if (actual != $"{database.Oid}|{database.OwnerOid}|{database.Owner}") throw new PgFailure("database.resource_identity_changed");
    }
    private static async Task PgVerifyAccount(PgLedger ledger, PostgreSqlAccount account, Func<string,string,Task<string>>? query=null)
    {
        query??=(databaseName,sql)=>PgScalar(ledger,databaseName,sql);
        var actual = await query("postgres", $"SELECT oid||'|'||(rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND NOT rolinherit)::text FROM pg_roles WHERE rolname={PgLiteral(account.Name)};");
        if (actual != account.Oid + "|true") throw new PgFailure("database.resource_identity_changed");
        var owner = ledger.Databases.Single(x => x.Name == account.Database).Owner;
        var memberships = await query("postgres", $"SELECT coalesce(string_agg(r.rolname,',' ORDER BY r.rolname),'') FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid WHERE m.member={account.Oid};");
        if (memberships != (account.Permission == PostgreSqlPermission.Migration ? owner : "")) throw new PgFailure("database.grant_drift");
        if (await query("postgres", $"SELECT count(*) FROM pg_database WHERE datallowconn AND datname<>{PgLiteral(account.Database)} AND has_database_privilege({PgLiteral(account.Name)},oid,'CONNECT');") != "0")
            throw new PgFailure("database.grant_drift");
        if (account.Permission != PostgreSqlPermission.Migration)
        {
            var role = PgLiteral(account.Name);
            var forbidden = account.Permission == PostgreSqlPermission.ReadOnly ? "INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER" : "TRUNCATE,REFERENCES,TRIGGER";
            if (await query(account.Database, $"SELECT (has_database_privilege({role},current_database(),'CREATE,TEMPORARY') OR has_schema_privilege({role},'app','CREATE') OR has_schema_privilege({role},'public','CREATE') OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='app' AND c.relkind IN ('r','p') AND (NOT has_table_privilege({role},c.oid,'SELECT') OR has_table_privilege({role},c.oid,'{forbidden}'))))::text;") != "false")
                throw new PgFailure("database.grant_drift");
        }
    }
    private static string PgWitness(PgPending pending) => $"INSERT INTO rk_db_control.operations VALUES ('{pending.Id}', '{pending.Identity}');";
    private static async Task<bool> PgHasWitness(PgLedger ledger, string database, PgPending pending) =>
        await PgScalar(ledger, database, $"SELECT count(*) FROM rk_db_control.operations WHERE id='{pending.Id}' AND identity='{pending.Identity}';") == "1";
    private static string PgScram(string password, Guid reference)
    {
        var salt = SHA256.HashData(reference.ToByteArray())[..16];
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 4096, HashAlgorithmName.SHA256, 32);
        var client = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Client Key"));
        var stored = SHA256.HashData(client);
        var server = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Server Key"));
        var result = $"SCRAM-SHA-256$4096:{Convert.ToBase64String(salt)}${Convert.ToBase64String(stored)}:{Convert.ToBase64String(server)}";
        CryptographicOperations.ZeroMemory(salted); CryptographicOperations.ZeroMemory(client); CryptographicOperations.ZeroMemory(server);
        return result;
    }
    private static string PgGrantSql(PostgreSqlDatabase database, string account, PostgreSqlPermission permission)
    {
        var role = PgId(account); var owner = PgId(database.Owner);
        var sql = $"REVOKE {owner} FROM {role}; REVOKE ALL ON DATABASE {PgId(database.Name)} FROM {role}; GRANT CONNECT ON DATABASE {PgId(database.Name)} TO {role}; "
            + $"REVOKE ALL ON SCHEMA public,app FROM {role}; REVOKE ALL ON ALL TABLES IN SCHEMA app FROM {role}; REVOKE ALL ON ALL SEQUENCES IN SCHEMA app FROM {role}; REVOKE ALL ON ALL FUNCTIONS IN SCHEMA app FROM {role}; "
            + $"ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA app REVOKE ALL ON TABLES FROM {role}; ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA app REVOKE ALL ON SEQUENCES FROM {role}; ";
        if (permission == PostgreSqlPermission.Migration) return sql + $"GRANT {owner} TO {role} WITH INHERIT FALSE, SET TRUE;";
        sql += $"GRANT USAGE ON SCHEMA app TO {role}; ";
        var tables = permission == PostgreSqlPermission.ReadOnly ? "SELECT" : "SELECT,INSERT,UPDATE,DELETE";
        sql += $"GRANT {tables} ON ALL TABLES IN SCHEMA app TO {role}; ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA app GRANT {tables} ON TABLES TO {role}; ";
        if (permission == PostgreSqlPermission.ReadWrite) sql += $"GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA app TO {role}; ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA app GRANT USAGE,SELECT ON SEQUENCES TO {role};";
        return sql;
    }
    private static async Task<PgLedger> PgContinueAccount(PgLedger ledger, PostgreSqlAccountCommand command, PostgreSqlSecrets? secrets)
    {
        await PgVerifyPackage(ledger); await PgVerifyConnection(ledger);
        var pending = ledger.Pending!;
        if (!PostgreSqlAccountValidation.Valid(command)) throw new PgFailure("database.request_invalid");
        if (command.Action == PostgreSqlAccountAction.CreateDatabase)
        {
            var owner = PgOwner(command.Database); var quotedOwner = PgId(owner); var db = PgId(command.Database);
            if (ledger.Pending!.Phase == "Prepared")
            {
                await PgSql(ledger, "postgres", PgWitnessSchema);
                ledger = PgPhase(ledger, "OwnerDispatched");
                await PgSql(ledger, "postgres", "BEGIN; " + $"CREATE ROLE {quotedOwner} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS; "
                    + $"COMMENT ON ROLE {quotedOwner} IS 'RelaxKonOS:{pending.Id}'; " + PgWitness(pending) + " COMMIT;");
            }
            if (ledger.Pending!.Phase == "OwnerDispatched")
            {
                if (!await PgHasWitness(ledger, "postgres", pending)) throw new PgFailure("database.accounts_recovery_required");
                ledger = PgPhase(ledger, "OwnerReady");
            }
            if (ledger.Pending!.Phase == "OwnerReady")
            {
                ledger = PgPhase(ledger, "DatabaseDispatched");
                await PgSql(ledger, "postgres", $"CREATE DATABASE {db} OWNER {quotedOwner} TEMPLATE template0 ENCODING 'UTF8' ALLOW_CONNECTIONS false;");
            }
            if (ledger.Pending!.Phase == "DatabaseDispatched")
            {
                var identity = (await PgScalar(ledger, "postgres", $"SELECT d.oid||'|'||r.oid FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba WHERE d.datname={PgLiteral(command.Database)} AND r.rolname={PgLiteral(owner)} AND NOT d.datallowconn AND shobj_description(r.oid,'pg_authid')='RelaxKonOS:{pending.Id}';")).Split('|');
                if (identity.Length != 2 || !uint.TryParse(identity[0], out var oid) || !uint.TryParse(identity[1], out var ownerOid)) throw new PgFailure("database.accounts_recovery_required");
                ledger = ledger with { Databases = [.. ledger.Databases, new(command.Database, oid, owner, ownerOid)] };
                ledger = PgPhase(ledger, "DatabaseCreated");
            }
            if (ledger.Pending!.Phase == "DatabaseCreated")
            {
                await PgVerifyDatabase(ledger, ledger.Databases.Single(x => x.Name == command.Database));
                await PgSql(ledger, "postgres", $"BEGIN; REVOKE ALL ON DATABASE {db} FROM PUBLIC; REVOKE CONNECT,TEMPORARY ON DATABASE postgres,template1 FROM PUBLIC; ALTER DATABASE {db} ALLOW_CONNECTIONS true; COMMIT;");
                await PgSql(ledger, command.Database, "BEGIN; " + PgWitnessSchema + $"CREATE SCHEMA IF NOT EXISTS app AUTHORIZATION {quotedOwner}; REVOKE ALL ON SCHEMA public,app FROM PUBLIC; "
                    + $"ALTER DEFAULT PRIVILEGES FOR ROLE {quotedOwner} REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC; ALTER DEFAULT PRIVILEGES FOR ROLE {quotedOwner} REVOKE USAGE ON TYPES FROM PUBLIC; COMMIT;");
                ledger = PgPhase(ledger, "Verified");
            }
            return ledger;
        }
        var database = ledger.Databases.SingleOrDefault(x => x.Name == command.Database) ?? throw new PgFailure("database.resource_unmanaged");
        await PgVerifyDatabase(ledger, database);
        if (secrets is null || !PostgreSqlAccountValidation.Password(secrets.Password)) throw new PgFailure("database.credential_unavailable");
        var existing = ledger.Accounts.SingleOrDefault(x => x.Name == command.Account && x.Database == command.Database);
        var role = PgId(command.Account!);
        var reference = command.CredentialReference ?? existing?.CredentialReference ?? throw new PgFailure("database.credential_unavailable");
        var verifier = PgScram(secrets.Password, reference);
        if (ledger.Pending!.Phase == "Prepared")
        {
            ledger = PgPhase(ledger, "AccountDispatched");
            var sql = "BEGIN; ";
            if (command.Action == PostgreSqlAccountAction.CreateAccount)
                sql += $"CREATE ROLE {role} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{verifier}'; ";
            else if (command.Action == PostgreSqlAccountAction.ChangePassword) sql += $"ALTER ROLE {role} PASSWORD '{verifier}'; ";
            if (command.Action != PostgreSqlAccountAction.ChangePassword) sql += PgGrantSql(database, command.Account!, command.Permission!.Value);
            sql += PgWitness(pending) + " COMMIT;";
            await PgSql(ledger, command.Database, sql);
        }
        if (ledger.Pending!.Phase == "AccountDispatched")
        {
            if (!await PgHasWitness(ledger, command.Database, pending)) throw new PgFailure("database.accounts_recovery_required");
            ledger = PgPhase(ledger, "AccountCommitted");
        }
        if (ledger.Pending!.Phase == "AccountCommitted")
        {
            var oidText = await PgScalar(ledger, "postgres", $"SELECT oid FROM pg_roles WHERE rolname={PgLiteral(command.Account!)};");
            if (!uint.TryParse(oidText, out var oid) || existing is not null && existing.Oid != oid) throw new PgFailure("database.resource_identity_changed");
            var next = new PostgreSqlAccount(command.Database, command.Account!, oid, command.Permission ?? existing!.Permission, reference,
                command.Action == PostgreSqlAccountAction.ChangePassword ? existing!.CredentialRevision + 1 : existing?.CredentialRevision ?? 1,
                command.Action == PostgreSqlAccountAction.ChangePassword || existing?.ApplicationsNeedUpdate == true);
            await PgVerifyAccount(ledger, next);
            if (!await PgPasswordConnection(ledger, next, secrets.Password)) throw new PgFailure("database.account_verification_failed");
            if (command.Action == PostgreSqlAccountAction.ChangePassword)
            {
                if (secrets.PreviousPassword is null || secrets.PreviousPassword == secrets.Password
                    || await PgPasswordConnection(ledger, next, secrets.PreviousPassword)) throw new PgFailure("database.old_password_not_rejected");
                if (await PgScalar(ledger, "postgres", $"SELECT rolpassword FROM pg_authid WHERE oid={oid};") != verifier) throw new PgFailure("database.credential_identity_changed");
            }
            ledger = ledger with { Accounts = [.. ledger.Accounts.Where(x => x.Name != next.Name), next] };
            ledger = PgPhase(ledger, "Verified");
        }
        return ledger;
    }
    private static async Task<bool> PgPasswordConnection(PgLedger ledger, PostgreSqlAccount account, string password)
    {
        var dir = "/run/relaxkonos-postgresql-secrets"; PgNoLinks(dir);
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = dir + "/" + Guid.NewGuid().ToString("N");
        try
        {
            PgAtomic(file, Encoding.UTF8.GetBytes($"127.0.0.1:{ledger.Port}:{account.Database}:{account.Name}:{password.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal)}\n"));
            var result = await PgRun("/usr/lib/postgresql/16/bin/psql", ["-XqAtw", "-v", "ON_ERROR_STOP=1", "-h", "127.0.0.1", "-p", ledger.Port.ToString(CultureInfo.InvariantCulture), "-U", account.Name, "-d", account.Database],
                allowFailure: true, input: "SELECT current_user||'|'||current_database();", passfile: file);
            return result.Code == 0 && result.Output.Trim() == account.Name + "|" + account.Database;
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
