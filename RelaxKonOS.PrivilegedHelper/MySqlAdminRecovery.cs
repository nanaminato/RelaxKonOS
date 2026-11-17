using System.Text;
using RelaxKonOS.ManagedRuntimes.MySql;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    // Startup SQL only restores the existing local administrator's native socket authentication.
    private const string MyAdminSql =
        "ALTER USER 'root'@'localhost' IDENTIFIED WITH auth_socket PASSWORD EXPIRE NEVER ACCOUNT UNLOCK;\n";

    private static string MyAdminGuardDirectory(MySqlBootstrapInstance plan) => MyUnit(plan) + ".d";
    private static string MyAdminGuard(MySqlBootstrapInstance plan) => MyAdminGuardDirectory(plan) + "/90-recovery.conf";
    private static string MyAdminGuardText(MySqlBootstrapInstance plan) =>
        "[Unit]\nConditionPathExists=!" + MyAdminGuard(plan) + "\n";

    private static async Task MyAdminPreflight(MyLedger ledger, Guid instance)
    {
        if (!ledger.Installed || ledger.Instance != instance)
            throw new PgFailure("database.instance_invalid");
        var plan = MySqlBootstrapPolicy.Plan(instance);
        await MyVerifyPackages();
        await MyVerifyUnit(plan);
        PgNoLinks(MyAdminGuardDirectory(plan));
        if (Directory.Exists(MyAdminGuardDirectory(plan)))
            throw new PgFailure("database.service_identity_changed");
        await MyVerifyAdminData(plan);
        if (await MyRunning(plan)) await MyVerifyProcess(plan);
    }

    private static async Task MyVerifyAdminData(MySqlBootstrapInstance plan)
    {
        PgNoLinks(plan.DataDirectory);
        PgNoLinks(plan.DataDirectory + "/auto.cnf");
        var owner = await PgRun("/usr/bin/stat", ["-c", "%U:%G:%a", plan.DataDirectory]);
        if (owner.Output.Trim() != MyUser + ":" + MyUser + ":700" || !File.Exists(plan.DataDirectory + "/auto.cnf"))
            throw new PgFailure("database.instance_invalid");
    }

    private static bool MyOwnedText(string path, string expected)
    {
        PgNoLinks(path);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length != Encoding.UTF8.GetByteCount(expected) || File.ReadAllText(path) != expected)
            throw new PgFailure("database.configuration_drift");
        return true;
    }

    private static async Task MyFenceNormalService(MySqlBootstrapInstance plan)
    {
        var guard = MyAdminGuard(plan);
        var expected = MyAdminGuardText(plan);
        if (!MyOwnedText(guard, expected))
        {
            await MyVerifyUnit(plan);
            if (Directory.Exists(MyAdminGuardDirectory(plan)) &&
                Directory.EnumerateFileSystemEntries(MyAdminGuardDirectory(plan)).Any())
                throw new PgFailure("database.service_identity_changed");
            Directory.CreateDirectory(MyAdminGuardDirectory(plan));
            PgAtomic(guard, Encoding.UTF8.GetBytes(expected));
            LinuxDirectorySync.Flush(Path.GetDirectoryName(MyAdminGuardDirectory(plan))!);
        }
        await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
        await MyVerifyUnit(plan, guard);
        await MyStopOwnedUnit(plan.ServiceName);
    }

    private static string MyPrivateUnit(MySqlBootstrapInstance plan) => "/run/systemd/system/" + plan.ServiceName;

    private static MySqlBootstrapInstance MyRecoveryPlan(MySqlBootstrapInstance normal, Guid operation)
    {
        var runtime = "relaxkonos-mysql-recovery-" + operation.ToString("N");
        return normal with { ServiceName = runtime + ".service", SocketDirectory = "/run/" + runtime };
    }

    private static string MyRecoveryUnitText(MySqlBootstrapInstance normal, MySqlBootstrapInstance repair, string? initFile)
    {
        var runtime = Path.GetFileName(repair.SocketDirectory);
        var options = normal.UnitText.Replace(normal.SocketDirectory, repair.SocketDirectory, StringComparison.Ordinal)
            .Replace("RuntimeDirectory=" + Path.GetFileName(normal.SocketDirectory), "RuntimeDirectory=" + runtime, StringComparison.Ordinal)
            .TrimEnd('\n') + " --persisted-globals-load=OFF";
        if (initFile is not null) options += " --init-file=" + initFile;
        return options + "\nPrivateNetwork=yes\nRestrictAddressFamilies=AF_UNIX\n";
    }

    private static async Task MyStopOwnedUnit(string unit)
    {
        await PgRun("/usr/bin/systemctl", ["stop", unit]);
        if ((await PgRun("/usr/bin/systemctl", ["show", "-p", "MainPID", "--value", unit])).Output.Trim() != "0")
            throw new PgFailure("database.stop_unconfirmed");
    }

    private static async Task MyCleanPrivate(MySqlBootstrapInstance repair, string text, string? initFile)
    {
        var path = MyPrivateUnit(repair);
        if (MyOwnedText(path, text))
        {
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            await MyVerifyUnit(repair, unitPath: path, unitText: text);
            await MyStopOwnedUnit(repair.ServiceName);
            File.Delete(path);
        }
        if (initFile is not null && MyOwnedText(initFile, MyAdminSql)) File.Delete(initFile);
        PgNoLinks(repair.SocketDirectory);
        if (Directory.Exists(repair.SocketDirectory)) throw new PgFailure("database.admin_recovery_unverified");
        await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
    }

    private static async Task MyRunPrivateRecovery(MySqlBootstrapInstance normal, MySqlBootstrapInstance repair, string? initFile)
    {
        var text = MyRecoveryUnitText(normal, repair, initFile);
        var path = MyPrivateUnit(repair);
        var unitExists = MyOwnedText(path, text);
        if (initFile is not null) MyOwnedText(initFile, MyAdminSql);
        try
        {
            if (initFile is not null)
            {
                if (!MyOwnedText(initFile, MyAdminSql)) PgAtomic(initFile, Encoding.UTF8.GetBytes(MyAdminSql));
                File.SetUnixFileMode(initFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            if (!unitExists) PgAtomic(path, Encoding.UTF8.GetBytes(text));
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            await MyVerifyUnit(repair, unitPath: path, unitText: text);
            await PgRun("/usr/bin/systemctl", ["start", repair.ServiceName]);
            await MyWaitSocket(repair);
            await MyVerifyProcess(repair);
            var expected = $"root@localhost\t{MySqlBootstrapPolicy.Version}\t{normal.DataDirectory}/\t1\tauth_socket\tN\tN";
            var actual = await MySqlQuery(repair, "SELECT CURRENT_USER(), VERSION(), @@datadir, @@skip_networking, plugin, account_locked, password_expired FROM mysql.user WHERE user='root' AND host='localhost';");
            if (expected != actual || await MySqlQuery(repair, "SELECT IFNULL(@@init_file,'');") != (initFile ?? ""))
                throw new PgFailure("database.admin_recovery_unverified");
            var denied = await PgRun("/usr/sbin/runuser", ["-u", MyUser, "--", "/usr/bin/mysql", "--no-defaults",
                "--no-login-paths", "--protocol=SOCKET", "--socket=" + repair.SocketDirectory + "/mysql.sock",
                "--user=root", "--connect-timeout=2"], allowFailure: true, input: "SELECT 1;");
            if (denied.Code == 0 || !(denied.Error.StartsWith("ERROR 1698 ", StringComparison.Ordinal) ||
                denied.Error.StartsWith("ERROR 1045 ", StringComparison.Ordinal)))
                throw new PgFailure("database.admin_recovery_unverified");
        }
        finally { await MyCleanPrivate(repair, text, initFile); }
    }

    private static async Task<MyLedger> MyContinueAdmin(MyLedger ledger)
    {
        var intent = ledger.Pending!;
        var instance = intent.Request.AdministratorInstanceId!.Value;
        if (ledger.Instance != instance) throw new PgFailure("database.instance_invalid");
        var normal = MySqlBootstrapPolicy.Plan(instance);
        var repair = MyRecoveryPlan(normal, intent.Id);
        var initFile = repair.SocketDirectory + ".sql";
        await MyVerifyPackages();
        await MyVerifyAdminData(normal);

        if (intent.Phase == "Prepared")
        {
            await MyAdminPreflight(ledger, instance);
            ledger = MyPhase(ledger, "AdminIsolationPrepared");
        }
        if (ledger.Pending!.Phase is "AdminIsolationPrepared" or "AdminIsolated" or "AdminAuthenticationRestored")
            await MyFenceNormalService(normal);

        if (ledger.Pending!.Phase is "AdminIsolationPrepared" or "AdminIsolated")
        {
            ledger = MyPhase(ledger, "AdminIsolated");
            await MyRunPrivateRecovery(normal, repair, initFile);
            ledger = MyPhase(ledger, "AdminAuthenticationRestored");
        }
        if (ledger.Pending!.Phase == "AdminAuthenticationRestored")
        {
            // Validate a fresh process under ordinary authentication without an init file.
            await MyRunPrivateRecovery(normal, repair, null);
            ledger = MyPhase(ledger, "AdminRecoveryCleaned");
        }
        if (ledger.Pending!.Phase != "AdminRecoveryCleaned") throw new PgFailure("database.state_invalid");

        var guard = MyAdminGuard(normal);
        if (MyOwnedText(guard, MyAdminGuardText(normal)))
        {
            await MyVerifyUnit(normal, guard);
            await MyStopOwnedUnit(normal.ServiceName);
            File.Delete(guard);
            LinuxDirectorySync.Flush(MyAdminGuardDirectory(normal));
        }
        if (Directory.Exists(MyAdminGuardDirectory(normal)) && !Directory.EnumerateFileSystemEntries(MyAdminGuardDirectory(normal)).Any())
        {
            Directory.Delete(MyAdminGuardDirectory(normal));
            LinuxDirectorySync.Flush(Path.GetDirectoryName(MyAdminGuardDirectory(normal))!);
        }
        await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
        await MyVerifyUnit(normal);
        return ledger;
    }
}
