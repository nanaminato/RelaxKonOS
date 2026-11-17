using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;
namespace RelaxKonOS.Protocol.Databases;
[JsonConverter(typeof(JsonStringEnumConverter<PostgreSqlMaintenanceAction>))]
public enum PostgreSqlMaintenanceAction { Update, Repair, Backup, Restore, UninstallKeepData, UninstallDeleteData, Reinstall, ConfigureBackupSchedule, ScheduledBackup, DeleteBackup }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlMaintenanceCommand(PostgreSqlMaintenanceAction Action, Guid InstanceId, long ExpectedRevision,
    ComponentPackageTarget? Target = null, Guid? BackupId = null, string? DeleteConfirmation = null, PostgreSqlBackupScheduleRequest? Schedule = null, Guid? ScheduleId = null, DateTimeOffset? DueAt = null);
public sealed record PostgreSqlBackup(Guid Id, string Version, DateTimeOffset CreatedAt, string ManifestHash, bool RestoreVerified, bool Automatic = false);
public static class PostgreSqlMaintenanceValidation
{
    public static bool Valid(PostgreSqlMaintenanceCommand c) => Enum.IsDefined(c.Action) && c.InstanceId != Guid.Empty && (c.Action==PostgreSqlMaintenanceAction.ScheduledBackup?c.ExpectedRevision==0:c.ExpectedRevision>0)
        && (c.Action is PostgreSqlMaintenanceAction.Update or PostgreSqlMaintenanceAction.Repair or PostgreSqlMaintenanceAction.Reinstall ? c.Target is not null : c.Target is null)
        && (c.Action is PostgreSqlMaintenanceAction.Restore or PostgreSqlMaintenanceAction.DeleteBackup ? c.BackupId is {} id && id!=Guid.Empty : c.BackupId is null)
        && (c.Action == PostgreSqlMaintenanceAction.UninstallDeleteData ? c.DeleteConfirmation==c.InstanceId.ToString("N") : c.DeleteConfirmation is null)
        && (c.Action==PostgreSqlMaintenanceAction.ConfigureBackupSchedule ? c.Schedule is {} policy && policy.IntervalHours is >=1 and <=168 && policy.KeepLast is >=1 and <=6 : c.Schedule is null)
        && (c.Action==PostgreSqlMaintenanceAction.ScheduledBackup ? c.ScheduleId is {} scheduleId && scheduleId!=Guid.Empty && c.DueAt is {} due && due.Offset==TimeSpan.Zero : c.ScheduleId is null && c.DueAt is null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlBackupScheduleRequest(bool Enabled, int IntervalHours, int KeepLast, bool AllowDowntime);
public sealed record PostgreSqlBackupSchedule(Guid Id, bool Enabled, int IntervalHours, int KeepLast, bool AllowDowntime,
    string Version, DateTimeOffset NextDueAt, Guid? LastOperationId=null, DateTimeOffset? LastAttemptAt=null, string? LastOutcome=null);
public static class PostgreSqlBackupSchedulePolicy
{
    public static PostgreSqlInstallationRequest Request(Guid instance,PostgreSqlBackupSchedule policy)=>new(true,
        Maintenance:new(PostgreSqlMaintenanceAction.ScheduledBackup,instance,0,ScheduleId:policy.Id,DueAt:policy.NextDueAt));
    public static string Key(PostgreSqlBackupSchedule policy)=>$"pg-backup:{policy.Id:N}:{policy.NextDueAt.UtcTicks}";
    public static IReadOnlyList<PostgreSqlBackup> Obsolete(IReadOnlyList<PostgreSqlBackup> backups,Guid current,int keep)
    {
        if(keep is <1 or >6||!backups.Any(x=>x.Id==current&&x.Automatic&&x.RestoreVerified))throw new InvalidOperationException("database.backup_unverified");
        // The just-verified recovery point must survive even when the host clock moved backwards.
        return backups.Where(x=>x.Automatic&&x.RestoreVerified).OrderByDescending(x=>x.Id==current)
            .ThenByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Skip(keep).ToArray();
    }
    // Coalesce missed intervals; never launch a backlog after a restart or wall-clock jump.
    public static DateTimeOffset NextDue(DateTimeOffset now,int intervalHours)=>now.ToUniversalTime().AddHours(intervalHours);
}
