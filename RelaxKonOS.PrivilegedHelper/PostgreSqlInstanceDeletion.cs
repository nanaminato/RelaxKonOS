using RelaxKonOS.ManagedRuntimes.PostgreSql;

public static partial class PrivilegedOperationExecutor
{
    private static async Task<PgLedger> PgContinueInstanceDeletion(PgLedger ledger)
    {
        var plan = PostgreSqlInitializationPlanner.Create(ledger.LedgerInstance!.Value, ledger.Port, PgMajor(ledger));
        if (ledger.Pending!.Phase == "Prepared")
        {
            await PgVerifyConfiguration(ledger, allowAdminMask: true);
            await PgMaskAdmin(ledger, plan);
            await PgStopPrivate(plan);
            ledger = PgPhase(ledger, "InstanceDeletionIsolated");
        }
        if (ledger.Pending!.Phase == "InstanceDeletionIsolated")
        {
            await PgMaskAdmin(ledger, plan);
            PgDeleteOwnedTree(plan.DataDirectory, Path.GetDirectoryName(plan.DataDirectory)!);
            PgDeleteOwnedTree(plan.ConfigurationDirectory, $"/etc/postgresql/{plan.Major}");
            PgDeleteOwnedTree(plan.SocketDirectory, "/run/postgresql");
            PgDeleteOwnedTree(PgBackupRoot(ledger), PgRoot + "/backups");
            ledger = PgPhase(ledger, "InstanceDeletionCleaned");
        }
        if (ledger.Pending!.Phase == "InstanceDeletionCleaned")
        {
            var mask = PgAdminMask(plan);
            if (PgOwnMask(plan))
            {
                File.Delete(mask);
                RelaxKonOS.Protocol.Privileged.LinuxDirectorySync.Flush(Path.GetDirectoryName(mask)!);
            }
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            ledger = PgPhase(ledger with
            {
                DataDeleted = true, Databases = [], Accounts = [], Backups = [], BackupSchedule = null,
                ConfigurationHash = null
            }, "Verified");
        }
        return ledger;
    }
}
