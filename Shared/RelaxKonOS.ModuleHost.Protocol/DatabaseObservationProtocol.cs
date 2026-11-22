namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>Private read-only observation available to the trusted database module.</summary>
public static class DatabaseObservationProtocol
{
    public const string ModuleId = "relaxkonos.database";
    public const string PostgreSqlState = "/internal/database/postgresql/state";
    public const string PostgreSqlMaintenancePlan = "/internal/database/postgresql/maintenance-plan";
    public const string PostgreSqlMigrationPlan = "/internal/database/postgresql/migration-plan";
    public const string MySqlState = "/internal/database/mysql/state";
}
