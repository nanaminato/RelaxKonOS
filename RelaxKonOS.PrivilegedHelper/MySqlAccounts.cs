using RelaxKonOS.ManagedRuntimes.MySql;
using RelaxKonOS.Protocol.Databases;

public static partial class PrivilegedOperationExecutor
{
    private static string MyAccountName(MySqlAccountCommand account) => $"'{account.User}'@'{account.Host}'";
    private static string MyPermissions(MySqlGrant grant) => grant == MySqlGrant.ReadOnly ? "SELECT" : "SELECT, INSERT, UPDATE, DELETE";
    private static MySqlAccount? MyOwnedAccount(MyLedger ledger, MySqlAccountCommand target) =>
        ledger.Accounts?.SingleOrDefault(item => item.User == target.User && item.Host == target.Host);
    private static async Task<bool> MyHasDatabase(MySqlBootstrapInstance plan, string name) =>
        await MySqlQuery(plan, $"SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name='{name}';") == "1";
    private static async Task<bool> MyHasAccount(MySqlBootstrapInstance plan, MySqlAccountCommand target) =>
        await MySqlQuery(plan, $"SELECT COUNT(*) FROM mysql.user WHERE user='{target.User}' AND host='{target.Host}';") == "1";

    private static async Task<bool> MyLogin(MySqlBootstrapInstance plan, MySqlAccountCommand account, string password)
    {
        if (!MySqlAccountCommand.Password(password)) throw new PgFailure("database.credential_invalid");
        var result = await PgRun("/usr/bin/mysql", ["--no-defaults", "--no-login-paths",
            "--protocol=SOCKET", "--socket=" + plan.SocketDirectory + "/mysql.sock",
            "--user=" + account.User, "--batch", "--skip-column-names", "--connect-timeout=2"],
            allowFailure: true, input: "SELECT CURRENT_USER();", mysqlPassword: password);
        if (result.Code == 0 && result.Output.Trim() == account.User + "@" + account.Host) return true;
        if (result.Code != 0 && result.Error.StartsWith("ERROR 1045 ", StringComparison.Ordinal)) return false;
        throw new PgFailure("database.connection_unverified");
    }

    private static void MyVerifySecrets(MySqlAction action, MySqlSecrets? secrets)
    {
        var valid = action switch
        {
            MySqlAction.CreateAccount => secrets is { PreviousPassword: null } && MySqlAccountCommand.Password(secrets.Password),
            MySqlAction.ChangePassword => secrets is not null && MySqlAccountCommand.Password(secrets.Password)
                && MySqlAccountCommand.Password(secrets.PreviousPassword) && secrets.PreviousPassword != secrets.Password,
            _ => secrets is null
        };
        if (!valid) throw new PgFailure("database.credential_invalid");
    }

    private static async Task MyVerifyAccountOwner(MySqlBootstrapInstance plan, MySqlAccountCommand account, Guid operation)
    {
        var owner = await MySqlQuery(plan, $"SELECT JSON_UNQUOTE(JSON_EXTRACT(ATTRIBUTE,'$.rk_operation')) FROM information_schema.user_attributes WHERE USER='{account.User}' AND HOST='{account.Host}';");
        if (owner != operation.ToString("N")) throw new PgFailure("database.account_drift");
    }

    private static async Task MyVerifyGrant(MySqlBootstrapInstance plan, MySqlAccountCommand account, MySqlGrant grant)
    {
        var grants = (await MySqlQuery(plan, $"SHOW GRANTS FOR {MyAccountName(account)};")).Split('\n');
        var suffix = $" TO `{account.User}`@`{account.Host}`";
        if (grants.Length != 2 || !grants.Contains("GRANT USAGE ON *.*" + suffix) ||
            !grants.Contains($"GRANT {MyPermissions(grant)} ON `{account.Database}`.*" + suffix))
            throw new PgFailure("database.account_drift");
        if (await MySqlQuery(plan, $"SELECT plugin FROM mysql.user WHERE user='{account.User}' AND host='{account.Host}';") != "caching_sha2_password")
            throw new PgFailure("database.account_drift");
    }

    private static async Task MyAccountPreflight(MyLedger ledger, MySqlAction action, MySqlAccountCommand account, MySqlSecrets? secrets)
    {
        if (ledger.Instance != account.InstanceId) throw new PgFailure("database.instance_invalid");
        MyVerifySecrets(action, secrets);
        var plan = MySqlBootstrapPolicy.Plan(account.InstanceId);
        await MyVerifyPackages();
        await MyVerifyConnection(plan);
        if (secrets is not null && await MySqlQuery(plan, "SELECT @@global.general_log, @@global.log_bin;") != "0\t0")
            throw new PgFailure("database.configuration_drift");
        if (action == MySqlAction.CreateDatabase)
        {
            if (await MyHasDatabase(plan, account.Database)) throw new PgFailure("database.external_installation");
            return;
        }
        if (ledger.Databases?.Contains(account.Database) != true || !await MyHasDatabase(plan, account.Database))
            throw new PgFailure("database.instance_invalid");
        var current = MyOwnedAccount(ledger, account);
        if (action == MySqlAction.CreateAccount)
        {
            if (current is not null || await MyHasAccount(plan, account)) throw new PgFailure("database.external_installation");
            return;
        }
        if (current is null || current.Database != account.Database)
            throw new PgFailure("database.instance_invalid");
        await MyVerifyAccountOwner(plan, account, current.OwnershipId);
        await MyVerifyGrant(plan, account, current.Grant);
        if (action == MySqlAction.ChangePassword && !await MyLogin(plan, account, secrets!.PreviousPassword!))
            throw new PgFailure("database.credential_invalid");
    }

    private static async Task<MyLedger> MyContinueAccount(MyLedger ledger, MySqlSecrets? secrets)
    {
        var pending = ledger.Pending!;
        var request = pending.Request;
        var account = request.Account!;
        var action = request.Action!.Value;
        MyVerifySecrets(action, secrets);
        var plan = MySqlBootstrapPolicy.Plan(account.InstanceId);
        await MyVerifyPackages();
        await MyVerifyConnection(plan);
        if (secrets is not null && await MySqlQuery(plan, "SELECT @@global.general_log, @@global.log_bin;") != "0\t0")
            throw new PgFailure("database.configuration_drift");
        if (action == MySqlAction.CreateDatabase)
        {
            // CREATE DATABASE has no native ownership witness. A pre-existing name after interruption is ambiguous.
            if (pending.Phase != "DatabaseCreated")
            {
                if (await MyHasDatabase(plan, account.Database)) throw new PgFailure("database.recovery_required");
                await MySqlQuery(plan, $"CREATE DATABASE `{account.Database}` CHARACTER SET utf8mb4;");
                ledger = MyPhase(ledger, "DatabaseCreated");
            }
            if (!await MyHasDatabase(plan, account.Database)) throw new PgFailure("database.account_drift");
            return ledger with { Databases = [.. ledger.Databases ?? [], account.Database] };
        }

        var name = MyAccountName(account);
        var current = MyOwnedAccount(ledger, account);
        if (current is not null) await MyVerifyAccountOwner(plan, account, current.OwnershipId);
        if (action == MySqlAction.ChangePassword)
        {
            await MyVerifyGrant(plan, account, current!.Grant);
            if (!await MyLogin(plan, account, secrets!.Password))
            {
                if (!await MyLogin(plan, account, secrets.PreviousPassword!))
                    throw new PgFailure("database.credential_invalid");
                await MySqlQuery(plan, $"ALTER USER {name} IDENTIFIED WITH caching_sha2_password BY '{secrets.Password}';");
            }
            if (!await MyLogin(plan, account, secrets.Password) || await MyLogin(plan, account, secrets.PreviousPassword!))
                throw new PgFailure("database.connection_unverified");
            return ledger with { Accounts = ledger.Accounts!.Select(item => item == current ? item with
                { CredentialReference = account.CredentialReference!.Value, CredentialRevision = item.CredentialRevision + 1 } : item).ToArray() };
        }

        if (action == MySqlAction.CreateAccount)
        {
            var witness = pending.Id.ToString("N");
            if (!await MyHasAccount(plan, account))
                await MySqlQuery(plan, $"CREATE USER {name} IDENTIFIED WITH caching_sha2_password BY '{secrets!.Password}' ATTRIBUTE '{{\"rk_operation\":\"{witness}\"}}';");
            await MyVerifyAccountOwner(plan, account, pending.Id);
        }
        else if (pending.Phase != "GrantDispatched")
        {
            await MyVerifyGrant(plan, account, current!.Grant);
            ledger = MyPhase(ledger, "GrantDispatched");
        }
        await MySqlQuery(plan, $"REVOKE ALL PRIVILEGES, GRANT OPTION FROM {name}; GRANT {MyPermissions(account.Grant!.Value)} ON `{account.Database}`.* TO {name};");
        await MyVerifyGrant(plan, account, account.Grant.Value);
        if (action == MySqlAction.CreateAccount && !await MyLogin(plan, account, secrets!.Password))
            throw new PgFailure("database.connection_unverified");
        var updated = current is null
            ? new MySqlAccount(account.Database, account.User!, account.Host!, account.Grant.Value, account.CredentialReference!.Value, 1, pending.Id)
            : current with { Grant = account.Grant.Value };
        return ledger with { Accounts = [.. (ledger.Accounts ?? []).Where(item => item != current), updated] };
    }
}
