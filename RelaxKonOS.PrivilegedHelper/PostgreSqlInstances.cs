using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private static string PgLedgerPath(Guid? instance) =>
        instance is { } id ? $"{PgRoot}/instance-{id:N}.json" : PgRoot + "/state.json";

    private static PgLedger[] PgReadAdditional() =>
        Directory.EnumerateFiles(PgRoot, "instance-*.json")
            .Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path)["instance-".Length..], "N", out var id)
                && id != Guid.Empty ? PgRead(id) : throw new PgFailure("database.state_invalid"))
            .ToArray();

    private static Guid? PgTarget(PostgreSqlInstallationRequest? request) =>
        request?.Command?.InstanceId ?? request?.AccountCommand?.InstanceId ?? request?.AdminRecovery?.InstanceId
        ?? request?.RemoteAccess?.InstanceId ?? request?.Maintenance?.InstanceId;

    private static IReadOnlyList<PostgreSqlInstanceSummary> PgInstanceSummaries(PgLedger primary, PgLedger[] additional) =>
        additional.Prepend(primary).Where(l => l.Instance is not null || l.LedgerInstance is not null)
            .Select(l => new PostgreSqlInstanceSummary(l.Instance ?? l.LedgerInstance!.Value,
                l.Pending?.Request.Command is { Action: PostgreSqlAction.Initialize } init ? init.Port : l.Port)).ToArray();

    private static PgLedger PgSelectLedger(PgLedger primary, PgLedger[] additional, PrivilegedOperationRequest request, Guid operation)
    {
        var ledgers = additional.Prepend(primary).ToArray();
        var intent = request.PostgreSql;
        if (request.PostgreSqlInstance is { } selected)
        {
            if (intent is not null || request.Operation != PrivilegedOperationKind.PostgreSqlQuery)
                throw new PgFailure("database.request_invalid");
            return ledgers.SingleOrDefault(l => (l.Instance ?? l.LedgerInstance) == selected)
                ?? throw new PgFailure("database.instance_invalid");
        }

        var original = intent?.RecoverOperationId ?? operation;
        var existing = ledgers.SingleOrDefault(l => l.Pending?.Id == original || l.Receipts.Any(r => r.Id == original));
        var target = PgTarget(intent);
        if (existing is not null)
        {
            if (target is not null && (existing.Instance ?? existing.LedgerInstance ?? PgTarget(existing.Pending?.Request)) is { } owner && target != owner)
                throw new PgFailure("database.operation_conflict");
            return existing;
        }
        if (target is null || primary.Instance == target)
            return primary;

        var registered = additional.SingleOrDefault(l => l.LedgerInstance == target);
        if (registered is not null)
            return registered;
        if (intent?.Command is not { Action: PostgreSqlAction.Initialize } || primary.Instance is null)
            return primary;
        if (additional.Length != 0 || primary.PackageRemoved || primary.DataDeleted || primary.RemoteAccess is not null)
            throw new PgFailure("database.maintenance_invalid");

        return new(primary.Revision, primary.Package, primary.BinaryHash, primary.ServiceHash,
            null, intent.Command.Port, null, null, [], [], [], LedgerInstance: target);
    }

    private static void PgCheckInstanceMutation(PgLedger primary, PgLedger[] additional, PgLedger ledger,
        PostgreSqlInstallationRequest request)
    {
        if (additional.Prepend(primary).Any(l => l.LedgerInstance != ledger.LedgerInstance && l.Pending is not null))
            throw new PgFailure("database.recovery_required");
        if (additional.Length != 0 &&
            (request.Maintenance?.Action is PostgreSqlMaintenanceAction.Update or PostgreSqlMaintenanceAction.Repair
                or PostgreSqlMaintenanceAction.Reinstall or PostgreSqlMaintenanceAction.UninstallKeepData
                or PostgreSqlMaintenanceAction.UninstallDeleteData or PostgreSqlMaintenanceAction.MigrateMajor))
            throw new PgFailure("database.maintenance_invalid");
        if (ledger.LedgerInstance is not null && (request.RemoteAccess is not null ||
            request.Maintenance?.Action is PostgreSqlMaintenanceAction.ConfigureBackupSchedule or PostgreSqlMaintenanceAction.ScheduledBackup))
            throw new PgFailure("database.maintenance_invalid");
    }
}
