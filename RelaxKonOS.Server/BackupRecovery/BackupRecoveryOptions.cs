namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>
/// Operator configuration for the recovery key domain. The key is supplied by a deployment secret
/// source (for example a platform secret injection), never generated into the backup directory.
/// Keeping the same key id and material available to a replacement installation is the explicit
/// cross-install recovery path; without it the feature remains unavailable.
/// </summary>
public sealed class BackupRecoveryOptions
{
    public string RootDirectory { get; set; } = "data/backup-recovery";
    public long MaximumObjectBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>Total ciphertext budget below <see cref="RootDirectory"/>. This is enforced before an object is committed.</summary>
    public long MaximumStoredBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public string KeyId { get; set; } = "operator-managed-v1";
    public string? KeyEncryptionKeyBase64 { get; set; }
}
