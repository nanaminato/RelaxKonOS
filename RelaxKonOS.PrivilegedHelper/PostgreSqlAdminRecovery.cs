using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ManagedRuntimes.PostgreSql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private static string PgAdminMask(PostgreSqlInitialization plan) => "/etc/systemd/system/" + plan.ServiceName;
    private static bool PgOwnMask(PostgreSqlInitialization plan) => new FileInfo(PgAdminMask(plan)).LinkTarget == "/dev/null";
    private static async Task PgAdminPreflight(PgLedger ledger, PostgreSqlAdminRecoveryCommand command)
    {
        if (command.InstanceId == Guid.Empty || ledger.Instance != command.InstanceId || command.ExpectedRevision != ledger.Revision)
            throw new PgFailure("database.plan_changed");
        await PgVerifyPackage(ledger);
        await PgVerifyConfiguration(ledger);
        var plan = PostgreSqlInitializationPlanner.Create(command.InstanceId, ledger.Port, PgMajor(ledger));
        if (File.ReadAllText(plan.ConfigurationDirectory + "/pg_hba.conf") != plan.HbaConfiguration
            || File.ReadAllText(plan.ConfigurationDirectory + "/start.conf").Split('\n').Where(x => !x.TrimStart().StartsWith('#')).Any(x => x.Trim() is not ("" or "manual")))
            throw new PgFailure("database.configuration_drift");
        if ((await PgRun("/usr/bin/stat", ["-c", "%U", plan.DataDirectory])).Output.Trim() != "postgres"
            || File.ReadAllText(plan.DataDirectory + "/PG_VERSION").Trim() != PgMajor(ledger).ToString()) throw new PgFailure("database.instance_invalid");
    }
    private static async Task PgMaskAdmin(PgLedger ledger, PostgreSqlInitialization plan)
    {
        var path = PgAdminMask(plan);
        if (!PgOwnMask(plan))
        {
            PgNoLinks(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new PgFailure("database.service_identity_changed");
            File.CreateSymbolicLink(path, "/dev/null");
            LinuxDirectorySync.Flush(Path.GetDirectoryName(path)!);
        }
        await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
        await PgRun("/usr/bin/systemctl", ["stop", plan.ServiceName]);
        if ((await PgRun("/usr/bin/systemctl", ["show", "--value", "-p", "MainPID", plan.ServiceName])).Output.Trim() != "0")
            throw new PgFailure("database.stop_unconfirmed");
    }
    private static async Task PgStopPrivate(PostgreSqlInitialization plan)
    {
        // pg_ctl verifies the data directory's postmaster identity; never remove pid/lock files.
        if (File.Exists(plan.DataDirectory + "/postmaster.pid"))
        {
            var options = File.ReadAllText(plan.DataDirectory + "/postmaster.opts");
            if (!options.Contains("listen_addresses=", StringComparison.Ordinal) || !options.Contains("unix_socket_permissions=0700", StringComparison.Ordinal))
                throw new PgFailure("database.service_identity_changed");
            await PgRun("/usr/sbin/runuser", ["-u", "postgres", "--", PgBin(plan.Major, "pg_ctl"), "-D", plan.DataDirectory, "-m", "fast", "-w", "stop"]);
        }
    }
    private static async Task<PgLedger> PgContinueAdminRecovery(PgLedger ledger)
    {
        var plan = PostgreSqlInitializationPlanner.Create(ledger.Instance!.Value, ledger.Port, PgMajor(ledger));
        await PgVerifyPackage(ledger);
        await PgVerifyConfiguration(ledger, allowAdminMask: true);
        // Mask is persistent across Helper/container/host restart. No trust HBA is ever installed.
        await PgMaskAdmin(ledger, plan);
        try
        {
            await PgStopPrivate(plan);
            if (ledger.Pending!.Phase == "Verified") ledger = PgPhase(ledger, "AdminAuthenticationVerified");
            if (ledger.Pending!.Phase == "Prepared") ledger = PgPhase(ledger, "AdminIsolated");
            if (ledger.Pending!.Phase is "AdminIsolated" or "AdminRepairDispatched")
            {
                ledger = PgPhase(ledger, "AdminRepairDispatched");
                // Fixed desired-state repair is idempotent. Only the built-in role OID is accepted.
                const string sql = "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_authid WHERE oid=10 AND rolname='postgres') THEN RAISE EXCEPTION 'administrator identity changed'; END IF; END $$;\n"
                    + "ALTER ROLE postgres WITH LOGIN SUPERUSER CREATEDB CREATEROLE INHERIT REPLICATION BYPASSRLS CONNECTION LIMIT -1 PASSWORD NULL VALID UNTIL 'infinity';\n";
                await PgRun("/usr/sbin/runuser", ["-u", "postgres", "--", PgBin(ledger, "postgres"), "--single", "-D", plan.DataDirectory,
                    "-c", "config_file=" + plan.ConfigurationDirectory + "/postgresql.conf", "-c", "exit_on_error=on", "postgres"], input: sql);
                ledger = PgPhase(ledger, "AdminRepaired");
            }
            if (ledger.Pending!.Phase == "AdminRepaired")
            {
                await PgRun("/usr/bin/install", ["-d", "-m", "700", "-o", "postgres", "-g", "postgres", plan.SocketDirectory]);
                await PgRun("/usr/sbin/runuser", ["-u", "postgres", "--", PgBin(ledger, "pg_ctl"), "-D", plan.DataDirectory,
                    "-l", plan.DataDirectory + "/rk-admin-recovery.log", "-o", "-c config_file=" + plan.ConfigurationDirectory + "/postgresql.conf -c listen_addresses='' -c unix_socket_permissions=0700", "-w", "start"]);
                try
                {
                    var proof = await PgScalar(ledger, "postgres", "SELECT current_user||'|'||current_setting('listen_addresses')||'|'||current_setting('data_directory')||'|'||(SELECT oid FROM pg_roles WHERE rolname=current_user)||'|'||(SELECT rolsuper AND rolcanlogin AND rolpassword IS NULL FROM pg_authid WHERE oid=10);");
                    if (proof != "postgres||" + plan.DataDirectory + "|10|true") throw new PgFailure("database.admin_recovery_unverified");
                    if (await PgScalar(ledger, "postgres", "SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL OR auth_method='trust';") != "0")
                        throw new PgFailure("database.admin_recovery_unverified");
                    foreach (var database in ledger.Databases) await PgVerifyDatabase(ledger, database);
                    foreach (var account in ledger.Accounts) await PgVerifyAccount(ledger, account);
                    ledger = PgPhase(ledger, "AdminAuthenticationVerified");
                }
                finally { await PgStopPrivate(plan); }
            }
            if (ledger.Pending!.Phase == "AdminAuthenticationVerified")
            {
                await PgVerifyConfiguration(ledger, allowAdminMask: true);
                if (File.Exists(plan.DataDirectory + "/postmaster.pid")) throw new PgFailure("database.stop_unconfirmed");
                // No temporary authentication files exist. Only remove this transaction's exact mask.
                if (!PgOwnMask(plan)) throw new PgFailure("database.service_identity_changed");
                File.Delete(PgAdminMask(plan)); LinuxDirectorySync.Flush("/etc/systemd/system");
                await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
                ledger = PgPhase(ledger, "Verified");
            }
            return ledger;
        }
        catch
        {
            // Never reopen normal service on failure. Any surviving private server has no TCP listener.
            await PgMaskAdmin(ledger, plan);
            await PgStopPrivate(plan);
            throw;
        }
    }
}
