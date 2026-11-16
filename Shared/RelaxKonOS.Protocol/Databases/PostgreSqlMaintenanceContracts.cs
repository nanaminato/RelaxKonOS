using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;
namespace RelaxKonOS.Protocol.Databases;
[JsonConverter(typeof(JsonStringEnumConverter<PostgreSqlMaintenanceAction>))]
public enum PostgreSqlMaintenanceAction { Update, Repair, Backup, Restore, UninstallKeepData, UninstallDeleteData, Reinstall }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlMaintenanceCommand(PostgreSqlMaintenanceAction Action, Guid InstanceId, long ExpectedRevision,
    ComponentPackageTarget? Target = null, Guid? BackupId = null, string? DeleteConfirmation = null);
public sealed record PostgreSqlBackup(Guid Id, string Version, DateTimeOffset CreatedAt, string ManifestHash, bool RestoreVerified);
public static class PostgreSqlMaintenanceValidation
{
    public static bool Valid(PostgreSqlMaintenanceCommand c) => Enum.IsDefined(c.Action) && c.InstanceId != Guid.Empty && c.ExpectedRevision > 0
        && (c.Action is PostgreSqlMaintenanceAction.Update or PostgreSqlMaintenanceAction.Repair or PostgreSqlMaintenanceAction.Reinstall ? c.Target is not null : c.Target is null)
        && (c.Action == PostgreSqlMaintenanceAction.Restore ? c.BackupId is {} id && id!=Guid.Empty : c.BackupId is null)
        && (c.Action == PostgreSqlMaintenanceAction.UninstallDeleteData ? c.DeleteConfirmation==c.InstanceId.ToString("N") : c.DeleteConfirmation is null);
}
