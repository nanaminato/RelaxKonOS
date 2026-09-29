using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.BackupRecovery;

namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>Envelope containing a data key encrypted under the operator-managed recovery key.</summary>
public sealed record BackupDataKeyEnvelope(string KeyId, string NonceBase64, string CiphertextBase64, string TagBase64);

/// <summary>Key domain used exclusively for backup data-key envelopes.</summary>
public interface IBackupRecoveryKeyProvider
{
    BackupRecoveryAvailabilityDto Availability { get; }
    BackupDataKeyEnvelope Wrap(Guid backupId, ReadOnlySpan<byte> dataKey);
    byte[] Unwrap(Guid backupId, BackupDataKeyEnvelope envelope);
}

/// <summary>
/// AES-256-GCM envelope implementation backed by an operator-provided recovery key. It is
/// deliberately fail-closed: Data Protection keys and application secrets are not substituted,
/// since neither is an asserted cross-install recovery domain.
/// </summary>
public sealed class ConfigurationBackupRecoveryKeyProvider : IBackupRecoveryKeyProvider
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private readonly byte[]? key;
    private readonly string? keyId;

    public ConfigurationBackupRecoveryKeyProvider(BackupRecoveryOptions options)
    {
        if (!ValidKeyId(options.KeyId) || string.IsNullOrWhiteSpace(options.KeyEncryptionKeyBase64)) return;
        try
        {
            var decoded = Convert.FromBase64String(options.KeyEncryptionKeyBase64);
            if (decoded.Length != KeyBytes) { CryptographicOperations.ZeroMemory(decoded); return; }
            key = decoded;
            keyId = options.KeyId;
        }
        catch (FormatException) { }
    }

    public BackupRecoveryAvailabilityDto Availability => new(key is not null, keyId);

    public BackupDataKeyEnvelope Wrap(Guid backupId, ReadOnlySpan<byte> dataKey)
    {
        var activeKey = RequireKey();
        if (backupId == Guid.Empty || dataKey.Length != KeyBytes)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[dataKey.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var cipher = new AesGcm(activeKey, TagBytes);
            cipher.Encrypt(nonce, dataKey, ciphertext, tag, AdditionalData(backupId));
            return new(keyId!, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public byte[] Unwrap(Guid backupId, BackupDataKeyEnvelope envelope)
    {
        var activeKey = RequireKey();
        if (backupId == Guid.Empty || !string.Equals(keyId, envelope.KeyId, StringComparison.Ordinal))
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.RecoveryKeyUnknown);
        try
        {
            var nonce = Convert.FromBase64String(envelope.NonceBase64);
            var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
            var tag = Convert.FromBase64String(envelope.TagBase64);
            if (nonce.Length != NonceBytes || ciphertext.Length != KeyBytes || tag.Length != TagBytes)
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            var plaintext = new byte[KeyBytes];
            using var cipher = new AesGcm(activeKey, TagBytes);
            try { cipher.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalData(backupId)); return plaintext; }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
            }
        }
        catch (FormatException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt); }
    }

    private byte[] RequireKey() => key ?? throw new BackupRecoveryException(BackupRecoveryProblemCodes.RecoveryKeyUnavailable);
    private static byte[] AdditionalData(Guid backupId) => Encoding.UTF8.GetBytes($"RelaxKonOS.BackupRecovery.Envelope.v1:{backupId:D}");
    private static bool ValidKeyId(string? value) => value is { Length: >= 1 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed class BackupRecoveryException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
