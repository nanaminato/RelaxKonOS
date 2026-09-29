namespace RelaxKonOS.Protocol.BackupRecovery;

/// <summary>Stable, non-sensitive backup and recovery failure identifiers.</summary>
public static class BackupRecoveryProblemCodes
{
    public const string RecoveryKeyUnavailable = "backup-recovery.key_unavailable";
    public const string RecoveryKeyUnknown = "backup-recovery.key_unknown";
    public const string BackupCorrupt = "backup-recovery.backup_corrupt";
    public const string BackupNotFound = "backup-recovery.backup_not_found";
    public const string StoreUnavailable = "backup-recovery.store_unavailable";
    public const string StorageLimitExceeded = "backup-recovery.storage_limit_exceeded";
    public const string BackupNotVerified = "backup-recovery.backup_not_verified";
    public const string SecretRebindRequired = "backup-recovery.secret_rebind_required";
    public const string VolumeAdapterUnavailable = "backup-recovery.volume_adapter_unavailable";
    public const string DatabaseAdapterUnavailable = "backup-recovery.database_adapter_unavailable";
    public const string RestoreConfirmationRequired = "backup-recovery.restore_confirmation_required";
    public const string RestoreNotAdmissible = "backup-recovery.restore_not_admissible";
}
