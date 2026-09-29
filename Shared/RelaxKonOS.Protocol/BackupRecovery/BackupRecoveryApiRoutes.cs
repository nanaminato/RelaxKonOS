using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.BackupRecovery;

public static class BackupRecoveryApiRoutes
{
    public const string Root = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/backup-recovery";
    public const string AvailabilityPattern = "/availability";
    public const string CreateDefinitionPattern = "/applications/{applicationId:guid}/definition-backups";
    public const string DefinitionBackupRequestPattern = "/applications/{applicationId:guid}/definition-backup-request";
    public const string ApplicationBackupsPattern = "/applications/{applicationId:guid}/backups";
    public const string BackupPattern = "/backups/{backupId:guid}";
    public const string PreflightPattern = "/backups/{backupId:guid}/restore-preflight";
    public const string RestoreDefinitionPattern = "/backups/{backupId:guid}/restore-definition";
    public static string DefinitionBackup(Guid applicationId) => Root + "/applications/" + applicationId.ToString("D") + "/definition-backups";
    public static string DefinitionBackupRequest(Guid applicationId) => Root + "/applications/" + applicationId.ToString("D") + "/definition-backup-request";
    public static string Backup(Guid backupId) => Root + "/backups/" + backupId.ToString("D");
    public static string RestorePreflight(Guid backupId) => Root + "/backups/" + backupId.ToString("D") + "/restore-preflight";
    public static string RestoreDefinition(Guid backupId) => Root + "/backups/" + backupId.ToString("D") + "/restore-definition";
}
