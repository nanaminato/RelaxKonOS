namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>Private read-only observation available to the trusted database module.</summary>
public static class DatabaseObservationProtocol
{
    public const string HostApiMinimum = "2.6.0";
    public const string ModuleId = "relaxkonos.database";
    public const string PostgreSqlState = "/internal/database/postgresql/state";
    public const string PostgreSqlMaintenancePlan = "/internal/database/postgresql/maintenance-plan";
    public const string PostgreSqlMigrationPlan = "/internal/database/postgresql/migration-plan";
    public const string MySqlState = "/internal/database/mysql/state";
    public const string PostgreSqlBackupSlot = "/internal/database/postgresql/backup-slot";
}

public enum DatabaseObservationKind { PostgreSqlState, PostgreSqlMaintenancePlan, PostgreSqlMigrationPlan, MySqlState }

public sealed record PostgreSqlBackupSlot(Guid InstanceId, Guid ScheduleId, DateTimeOffset DueAt);
