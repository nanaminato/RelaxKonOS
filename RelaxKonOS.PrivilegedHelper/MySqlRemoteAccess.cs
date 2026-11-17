using System.Net.NetworkInformation;
using System.Text;
using RelaxKonOS.ManagedRuntimes.MySql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private static string MyRemoteDrop(MySqlBootstrapInstance plan) => MyUnit(plan) + ".d/50-remote.conf";
    private static string MyRemoteRules(Guid id) => $"{MyRoot}/remote-{id:N}.nft";
    private static string MyRemoteAccount(MySqlRemotePolicy policy) => $"'{policy.User}'@'{policy.SourceAddress}'";
    private static string MyRemoteName(MySqlRemotePolicy policy) => MySqlRemotePlan.FirewallTable(policy.InstanceId);

    private static void MyValidateCertificate(MySqlRemotePolicy policy, PostgreSqlTlsMaterial? tls) =>
        PgRemoteCertificate(new(policy.InstanceId, 1, true, ServerName: policy.ServerName,
            CertificateFingerprint: policy.CertificateFingerprint), tls);

    private static async Task MyRemotePreflight(MyLedger ledger, MySqlRemoteChange request,
        PostgreSqlTlsMaterial? tls, MySqlSecrets? secrets)
    {
        if (ledger.Instance != request.InstanceId || (request.Policy is null) == (ledger.RemoteAccess is null))
            throw new PgFailure("database.remote_request_invalid");
        if (!File.Exists("/usr/sbin/nft")) throw new PgFailure("database.firewall_unavailable");
        await MyVerifyPackages(ledger.Version);
        var plan = MySqlBootstrapPolicy.Plan(request.InstanceId);
        if (request.Policy is { } policy)
        {
            MyValidateCertificate(policy, tls);
            if (secrets is not { PreviousPassword: null } || !MySqlAccountCommand.Password(secrets.Password)
                || !NetworkInterface.GetAllNetworkInterfaces().SelectMany(i => i.GetIPProperties().UnicastAddresses)
                    .Any(a => a.Address.ToString() == policy.ListenAddress))
                throw new PgFailure("database.remote_request_invalid");
            await MyVerifyConnection(plan, ledger.Version);
            var account = ledger.Accounts?.SingleOrDefault(a => a.Database == policy.Database && a.User == policy.User
                && a.Host == "localhost" && a.Grant == policy.Grant) ?? throw new PgFailure("database.resource_unmanaged");
            var local = new MySqlAccountCommand(plan.Id, account.Database, account.User, account.Host);
            await MyVerifyAccountOwner(plan, local, account.OwnershipId);
            await MyVerifyGrant(plan, local, account.Grant);
            if (!await MyLogin(plan, local, secrets.Password)) throw new PgFailure("database.credential_invalid");
            if (await MyRemoteOwner(plan, policy) != "absent") throw new PgFailure("database.external_installation");
            var existing = await MyRemoteTable(policy);
            if (existing.Code == 0 || Directory.Exists(MySqlRemotePlan.MaterialDirectory(plan.Id))
                || File.Exists(MyRemoteRules(plan.Id)))
                throw new PgFailure("database.external_installation");
        }
        else
        {
            if (tls is not null || secrets is not null || ledger.RemoteOwner is null)
                throw new PgFailure("database.remote_request_invalid");
            await MyVerifyRemoteUnit(plan, ledger.RemoteAccess!, ledger.RemoteOwner.Value);
        }
    }

    private static Task<PgCommandResult> MyRemoteTable(MySqlRemotePolicy policy) =>
        PgRun("/usr/sbin/nft", ["-s", "-y", "-nn", "list", "table", "inet", MyRemoteName(policy)], allowFailure: true);

    private static async Task MyVerifyRemoteTable(MySqlRemotePolicy policy, Guid owner, bool? expected = null)
    {
        var table = await MyRemoteTable(policy);
        if (table.Code != 0 && expected is null) return;
        if (table.Code != 0 || !new[] { false, true }.Any(open => (expected is null || expected == open)
            && table.Output.TrimEnd() == MySqlRemotePlan.FirewallSnapshot(policy, owner, open)))
            throw new PgFailure("database.firewall_drift");
    }

    private static async Task MyApplyRemoteTable(MySqlRemotePolicy policy, Guid owner, bool open)
    {
        await MyVerifyRemoteTable(policy, owner);
        await PgRun("/usr/sbin/nft", ["-f", "-"], input: MySqlRemotePlan.Firewall(policy, open, owner));
        await MyVerifyRemoteTable(policy, owner, open);
    }

    private static async Task MyVerifyRemoteUnit(MySqlBootstrapInstance plan, MySqlRemotePolicy policy, Guid owner)
    {
        if (!MyOwnedText(MyRemoteRules(plan.Id), MySqlRemotePlan.Firewall(policy, false, owner))
            || !MyOwnedText(MyRemoteDrop(plan), MySqlRemotePlan.Unit(policy)))
            throw new PgFailure("database.configuration_drift");
        await MyVerifyUnit(plan, MyRemoteDrop(plan));
    }

    private static async Task<string> MyRemoteOwner(MySqlBootstrapInstance plan, MySqlRemotePolicy policy)
    {
        var value = await MySqlQuery(plan,
            $"SELECT COALESCE(JSON_UNQUOTE(JSON_EXTRACT(ATTRIBUTE,'$.rk_remote')),'external') FROM information_schema.user_attributes WHERE USER='{policy.User}' AND HOST='{policy.SourceAddress}';");
        return value.Length == 0 ? "absent" : value;
    }

    private static async Task MyProbeRemote(MySqlBootstrapInstance plan, MySqlRemotePolicy policy, Guid owner, bool open)
    {
        await MyVerifyRemoteUnit(plan, policy, owner);
        await MyVerifyProcess(plan);
        var proof = await MySqlQuery(plan, "SELECT CURRENT_USER(), @@datadir, @@skip_networking, @@bind_address, @@require_secure_transport, @@skip_name_resolve, plugin FROM mysql.user WHERE user='root' AND host='localhost';");
        if (proof != $"root@localhost\t{plan.DataDirectory}/\t0\t{policy.ListenAddress}\t1\t1\tauth_socket")
            throw new PgFailure("database.connection_unverified");
        await MyVerifyRemoteTable(policy, owner, open);
    }

    private static async Task<MyLedger> MyContinueRemote(MyLedger ledger, PostgreSqlTlsMaterial? tls, MySqlSecrets? secrets)
    {
        var pending = ledger.Pending!;
        var request = pending.Request.Remote!;
        var policy = request.Policy ?? ledger.RemoteAccess ?? throw new PgFailure("database.remote_request_invalid");
        var plan = MySqlBootstrapPolicy.Plan(request.InstanceId);
        var name = MyRemoteAccount(policy);
        var drop = MyRemoteDrop(plan);
        var material = MySqlRemotePlan.MaterialDirectory(plan.Id);
        var ownership = request.Policy is not null ? pending.Id : ledger.RemoteOwner ?? throw new PgFailure("database.remote_request_invalid");
        await MyVerifyPackages(ledger.Version);

        if (request.Policy is not null && pending.Phase == "Prepared")
        {
            MyValidateCertificate(policy, tls);
            if (secrets is not { PreviousPassword: null } || !MySqlAccountCommand.Password(secrets.Password))
                throw new PgFailure("database.credential_invalid");
            await MyVerifyConnection(plan, ledger.Version);
            var witness = pending.Id.ToString("N");
            if (await MyRemoteOwner(plan, policy) == "absent")
                await MySqlQuery(plan, $"CREATE USER {name} IDENTIFIED WITH caching_sha2_password BY '{secrets.Password}' REQUIRE SSL ATTRIBUTE '{{\"rk_remote\":\"{witness}\"}}';");
            if (await MyRemoteOwner(plan, policy) != witness) throw new PgFailure("database.account_drift");
            await MySqlQuery(plan, $"GRANT {MyPermissions(policy.Grant)} ON `{policy.Database}`.* TO {name};");
            var remote = new MySqlAccountCommand(plan.Id, policy.Database, policy.User, policy.SourceAddress);
            await MyVerifyGrant(plan, remote, policy.Grant);
            if (await MySqlQuery(plan, $"SELECT ssl_type FROM mysql.user WHERE user='{policy.User}' AND host='{policy.SourceAddress}';") != "ANY")
                throw new PgFailure("database.tls_invalid");
            ledger = MyPhase(ledger, "RemoteAccountVerified");
        }

        if (request.Policy is not null)
        {
            MyValidateCertificate(policy, tls);
            await MyApplyRemoteTable(policy, ownership, false);
            await MyStopOwnedUnit(plan.ServiceName);
            PgNoLinks(material);
            Directory.CreateDirectory(material, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            PgAtomic(material + "/server.pem", Encoding.UTF8.GetBytes(tls!.CertificatePem));
            PgAtomic(material + "/server.key", Encoding.UTF8.GetBytes(tls.PrivateKeyPem));
            await PgRun("/usr/bin/chown", [MyUser + ":" + MyUser, material, material + "/server.pem", material + "/server.key"]);
            PgAtomic(MyRemoteRules(plan.Id), Encoding.UTF8.GetBytes(MySqlRemotePlan.Firewall(policy, false, ownership)));
            PgNoLinks(drop);
            Directory.CreateDirectory(Path.GetDirectoryName(drop)!);
            if (File.Exists(drop) && !MyOwnedText(drop, MySqlRemotePlan.Unit(policy)))
                throw new PgFailure("database.configuration_drift");
            if (!File.Exists(drop))
                PgAtomic(drop, Encoding.UTF8.GetBytes(MySqlRemotePlan.Unit(policy)));
            LinuxDirectorySync.Flush("/etc/systemd/system");
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            await MyVerifyRemoteUnit(plan, policy, ownership);
            try
            {
                await PgRun("/usr/bin/systemctl", ["start", plan.ServiceName]);
                await MyWaitSocket(plan);
                await MyProbeRemote(plan, policy, ownership, false);
                if (await MyRemoteOwner(plan, policy) != pending.Id.ToString("N"))
                    throw new PgFailure("database.account_drift");
                await MyVerifyGrant(plan, new(plan.Id, policy.Database, policy.User, policy.SourceAddress), policy.Grant);
                if (await MySqlQuery(plan, $"SELECT ssl_type FROM mysql.user WHERE user='{policy.User}' AND host='{policy.SourceAddress}';") != "ANY")
                    throw new PgFailure("database.tls_invalid");
                await MyApplyRemoteTable(policy, ownership, true);
            }
            catch
            {
                await MyStopOwnedUnit(plan.ServiceName);
                throw;
            }
            return ledger with { RemoteAccess = policy, RemoteOwner = pending.Id };
        }

        await MyApplyRemoteTable(policy, ownership, false);
        await MyStopOwnedUnit(plan.ServiceName);
        if (MyOwnedText(drop, MySqlRemotePlan.Unit(policy)))
        {
            File.Delete(drop);
            LinuxDirectorySync.Flush(Path.GetDirectoryName(drop)!);
            if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(drop)!).Any())
            {
                Directory.Delete(Path.GetDirectoryName(drop)!);
                LinuxDirectorySync.Flush("/etc/systemd/system");
            }
        }
        await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
        await MyVerifyUnit(plan);
        await PgRun("/usr/bin/systemctl", ["start", plan.ServiceName]);
        await MyWaitSocket(plan);
        await MyVerifyConnection(plan, ledger.Version);
        var owner = await MyRemoteOwner(plan, policy);
        if (owner != "absent")
        {
            if (owner != ledger.RemoteOwner?.ToString("N")) throw new PgFailure("database.account_drift");
            await MySqlQuery(plan, $"DROP USER {name};");
        }
        PgDeleteOwnedTree(material, Path.GetDirectoryName(plan.DataDirectory)!);
        await MyVerifyRemoteTable(policy, ownership, false);
        await PgRun("/usr/sbin/nft", ["delete", "table", "inet", MyRemoteName(policy)]);
        PgNoLinks(MyRemoteRules(plan.Id));
        File.Delete(MyRemoteRules(plan.Id));
        LinuxDirectorySync.Flush(MyRoot);
        return ledger with { RemoteAccess = null, RemoteOwner = null };
    }
}
