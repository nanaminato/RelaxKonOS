using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.BackupRecovery;

namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>Metadata required to reopen one encrypted object. It is safe to persist in a manifest.</summary>
public sealed record EncryptedBackupObject(
    BackupDataKeyEnvelope DataKeyEnvelope,
    long PlaintextLength,
    string PlaintextSha256);

/// <summary>
/// Server-owned encrypted object store. Callers cannot select a host path: a backup id maps to one
/// fixed file below <see cref="BackupRecoveryOptions.RootDirectory"/>. The file is only visible after
/// its bytes, authenticated tag and atomic rename have all completed.
/// </summary>
public sealed class BackupRecoveryObjectStore
{
    private static readonly byte[] Magic = "RKBR1"u8.ToArray();
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private readonly string root;
    private readonly long maximumObjectBytes;
    private readonly long maximumStoredBytes;
    private readonly IBackupRecoveryKeyProvider keys;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public BackupRecoveryObjectStore(IHostEnvironment environment, BackupRecoveryOptions options, IBackupRecoveryKeyProvider keys)
    {
        root = Path.Combine(environment.ContentRootPath, options.RootDirectory);
        maximumObjectBytes = Math.Max(1, options.MaximumObjectBytes);
        maximumStoredBytes = Math.Max(maximumObjectBytes, options.MaximumStoredBytes);
        this.keys = keys;
    }

    public async Task<EncryptedBackupObject> WriteAsync(Guid backupId, string objectId, Stream plaintext, CancellationToken cancellationToken)
    {
        if (backupId == Guid.Empty || !ValidObjectId(objectId)) throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        _ = keys.Availability.Available ? 0 : throw new BackupRecoveryException(BackupRecoveryProblemCodes.RecoveryKeyUnavailable);
        var staging = Path.Combine(root, "staging", backupId.ToString("N"), objectId + ".tmp");
        var destination = ObjectPath(backupId, objectId);
        byte[]? dataKey = null;
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            dataKey = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var buffer = await ReadBoundedAsync(plaintext, cancellationToken);
            try
            {
                EnsureCapacity(checked(buffer.Length + Magic.Length + NonceBytes + TagBytes));
                var ciphertext = new byte[buffer.Length];
                var tag = new byte[TagBytes];
                try
                {
                    using var cipher = new AesGcm(dataKey, TagBytes);
                    cipher.Encrypt(nonce, buffer, ciphertext, tag, AdditionalData(backupId));
                    await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await output.WriteAsync(Magic, cancellationToken);
                        await output.WriteAsync(nonce, cancellationToken);
                        await output.WriteAsync(tag, cancellationToken);
                        await output.WriteAsync(ciphertext, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(staging, destination, overwrite: false);
                    return new(keys.Wrap(backupId, dataKey), buffer.Length, Convert.ToHexString(SHA256.HashData(buffer)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                    CryptographicOperations.ZeroMemory(tag);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        catch (BackupRecoveryException) { throw; }
        catch (IOException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
        catch (UnauthorizedAccessException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            TryDelete(staging);
            writeGate.Release();
        }
    }

    /// <summary>Removes an object directory only after its manifest has failed; callers never provide a path.</summary>
    public void DeleteUnverifiedBackup(Guid backupId)
    {
        if (backupId == Guid.Empty) return;
        var directory = Path.Combine(root, "objects", backupId.ToString("N"));
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
        catch (UnauthorizedAccessException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
    }

    public async Task<byte[]> ReadAndVerifyAsync(Guid backupId, string objectId, EncryptedBackupObject metadata, CancellationToken cancellationToken)
    {
        if (backupId == Guid.Empty || !ValidObjectId(objectId) || metadata.PlaintextLength is < 0 or > long.MaxValue || !ValidHash(metadata.PlaintextSha256))
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        var path = ObjectPath(backupId, objectId);
        byte[]? dataKey = null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length < Magic.Length + NonceBytes + TagBytes || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            var nonce = bytes.AsSpan(Magic.Length, NonceBytes);
            var tag = bytes.AsSpan(Magic.Length + NonceBytes, TagBytes);
            var ciphertext = bytes.AsSpan(Magic.Length + NonceBytes + TagBytes);
            if (ciphertext.Length != metadata.PlaintextLength || ciphertext.Length > maximumObjectBytes)
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            dataKey = keys.Unwrap(backupId, metadata.DataKeyEnvelope);
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var cipher = new AesGcm(dataKey, TagBytes);
                cipher.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalData(backupId));
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(metadata.PlaintextSha256), SHA256.HashData(plaintext)))
                    throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
                return plaintext;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            }
        }
        catch (FileNotFoundException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupNotFound); }
        catch (DirectoryNotFoundException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupNotFound); }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                if (buffer.Length > maximumObjectBytes - read)
                    throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            return buffer.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(chunk); }
    }

    private void EnsureCapacity(long incomingBytes)
    {
        try
        {
            var objects = Path.Combine(root, "objects");
            var current = Directory.Exists(objects)
                ? Directory.EnumerateFiles(objects, "*.rkbr", SearchOption.AllDirectories).Aggregate(0L,
                    (total, file) => checked(total + new FileInfo(file).Length))
                : 0L;
            var projected = checked(current + incomingBytes);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            if (projected > maximumStoredBytes || drive.AvailableFreeSpace < incomingBytes)
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.StorageLimitExceeded);
        }
        catch (BackupRecoveryException) { throw; }
        catch (IOException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
        catch (UnauthorizedAccessException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
    }

    private string ObjectPath(Guid backupId, string objectId) => Path.Combine(root, "objects", backupId.ToString("N"), objectId + ".rkbr");
    private static byte[] AdditionalData(Guid backupId) => Encoding.UTF8.GetBytes($"RelaxKonOS.BackupRecovery.Object.v1:{backupId:D}");
    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool ValidObjectId(string? value) => value is { Length: >= 1 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
