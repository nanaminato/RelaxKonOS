using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.BackupRecovery;

namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>Private persisted counterpart of a public manifest projection.</summary>
internal sealed record BackupManifestEntry(BackupManifestDto Manifest, string IdempotencyReference, EncryptedBackupObject[] EncryptedObjects);

/// <summary>
/// Durable, fail-closed manifest ledger. A backup is never exposed as verified until its encrypted
/// object metadata has been persisted and can be reopened by the object store.
/// </summary>
internal sealed class BackupRecoveryManifestStore
{
    private readonly object gate = new();
    private readonly string path;
    private Ledger ledger = new([]);
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public BackupRecoveryManifestStore(IHostEnvironment environment, BackupRecoveryOptions options)
    {
        path = Path.Combine(environment.ContentRootPath, options.RootDirectory, "manifests.json");
        try
        {
            if (!File.Exists(path)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (ledger.Entries is null || ledger.Entries.Select(x => x.Manifest.BackupId).Distinct().Count() != ledger.Entries.Length
                || ledger.Entries.Select(x => x.Manifest.OperationId).Distinct().Count() != ledger.Entries.Length
                || ledger.Entries.Any(entry => !Valid(entry))) throw new JsonException();
        }
        catch { unavailable = true; }
    }

    public BackupManifestEntry[] Read()
    {
        lock (gate) { EnsureAvailable(); return [.. ledger.Entries.OrderByDescending(x => x.Manifest.CreatedAt)]; }
    }

    public BackupManifestEntry? Get(Guid backupId) => Read().FirstOrDefault(x => x.Manifest.BackupId == backupId);

    public BackupManifestEntry? Find(Guid applicationId, string idempotencyReference) => Read()
        .FirstOrDefault(x => x.Manifest.ApplicationId == applicationId && x.IdempotencyReference == idempotencyReference);

    public BackupManifestEntry Create(Guid applicationId, Guid? revisionId, string keyId, string idempotencyReference, out bool created)
    {
        if (applicationId == Guid.Empty || !ValidKeyId(keyId) || !ValidReference(idempotencyReference))
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        lock (gate)
        {
            EnsureAvailable();
            var existing = ledger.Entries.FirstOrDefault(x => x.IdempotencyReference == idempotencyReference);
            if (existing is not null)
            {
                if (existing.Manifest.ApplicationId != applicationId || existing.Manifest.RevisionId != revisionId)
                    throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
                created = false;
                return existing;
            }
            var now = DateTimeOffset.UtcNow;
            var manifest = new BackupManifestDto(Guid.NewGuid(), Guid.NewGuid(), applicationId, revisionId,
                BackupRecoveryOperationState.Queued, keyId, "AES-256-GCM", [], now);
            var entry = new BackupManifestEntry(manifest, idempotencyReference, []);
            Commit(new([.. ledger.Entries, entry]));
            created = true;
            return entry;
        }
    }

    public BackupManifestEntry Update(Guid backupId, Func<BackupManifestEntry, BackupManifestEntry> update)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = ledger.Entries.SingleOrDefault(x => x.Manifest.BackupId == backupId)
                ?? throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupNotFound);
            var after = update(before);
            if (after.Manifest.BackupId != before.Manifest.BackupId || after.Manifest.OperationId != before.Manifest.OperationId || after.IdempotencyReference != before.IdempotencyReference
                || after.Manifest.ApplicationId != before.Manifest.ApplicationId || !Valid(after))
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            Commit(new([.. ledger.Entries.Select(x => x.Manifest.BackupId == backupId ? after : x)]));
            return after;
        }
    }

    public BackupManifestEntry MarkRunning(Guid backupId) => Update(backupId, entry =>
    {
        if (entry.Manifest.State != BackupRecoveryOperationState.Queued) throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        return entry with { Manifest = entry.Manifest with { State = BackupRecoveryOperationState.Running } };
    });

    public BackupManifestEntry MarkVerified(Guid backupId, IReadOnlyList<BackupObjectDto> objects, IReadOnlyList<EncryptedBackupObject> encrypted)
        => Update(backupId, entry =>
        {
            if (entry.Manifest.State != BackupRecoveryOperationState.Running || objects.Count == 0 || objects.Count != encrypted.Count)
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            return entry with
            {
                Manifest = entry.Manifest with { State = BackupRecoveryOperationState.Verified, Objects = [.. objects], VerifiedAt = DateTimeOffset.UtcNow },
                EncryptedObjects = [.. encrypted],
            };
        });

    public BackupManifestEntry MarkFailed(Guid backupId, string problemCode) => Update(backupId, entry =>
    {
        if (entry.Manifest.State is BackupRecoveryOperationState.Verified or BackupRecoveryOperationState.Failed)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        return entry with { Manifest = entry.Manifest with { State = BackupRecoveryOperationState.Failed, ProblemCode = problemCode } };
    });

    public void MarkInterruptedAtStartup()
    {
        lock (gate)
        {
            EnsureAvailable();
            var changed = ledger.Entries.Select(entry => entry.Manifest.State is BackupRecoveryOperationState.Queued or BackupRecoveryOperationState.Running
                ? entry with { Manifest = entry.Manifest with { State = BackupRecoveryOperationState.Interrupted, ProblemCode = BackupRecoveryProblemCodes.BackupInterrupted } }
                : entry).ToArray();
            if (!changed.SequenceEqual(ledger.Entries)) Commit(new(changed));
        }
    }

    /// <summary>
    /// Drops only the oldest verified manifests for each application. The caller deletes their
    /// now-unreferenced encrypted directories afterwards; if that deletion is interrupted, the
    /// object-store startup reconciliation removes the orphan without reviving the manifest.
    /// </summary>
    public Guid[] ApplyVerifiedRetention(int maximumPerApplication)
    {
        if (maximumPerApplication < 1) maximumPerApplication = 1;
        lock (gate)
        {
            EnsureAvailable();
            var retired = ledger.Entries
                .Where(entry => entry.Manifest.State == BackupRecoveryOperationState.Verified)
                .GroupBy(entry => entry.Manifest.ApplicationId)
                .SelectMany(group => group.OrderByDescending(entry => entry.Manifest.VerifiedAt)
                    .ThenByDescending(entry => entry.Manifest.CreatedAt).Skip(maximumPerApplication))
                .Select(entry => entry.Manifest.BackupId)
                .ToHashSet();
            if (retired.Count == 0) return [];
            Commit(new([.. ledger.Entries.Where(entry => !retired.Contains(entry.Manifest.BackupId))]));
            return [.. retired];
        }
    }

    /// <summary>Verified IDs are the only encrypted object directories that may survive startup reconciliation.</summary>
    public Guid[] VerifiedBackupIds()
    {
        lock (gate)
        {
            EnsureAvailable();
            return [.. ledger.Entries.Where(entry => entry.Manifest.State == BackupRecoveryOperationState.Verified)
                .Select(entry => entry.Manifest.BackupId)];
        }
    }

    private void Commit(Ledger next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            ledger = next;
        }
        catch (IOException) { unavailable = true; throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
        catch (UnauthorizedAccessException) { unavailable = true; throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable); }
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new BackupRecoveryException(BackupRecoveryProblemCodes.StoreUnavailable);
    }

    private static bool Valid(BackupManifestEntry entry)
    {
        var manifest = entry.Manifest;
        return manifest.BackupId != Guid.Empty && manifest.OperationId != Guid.Empty && manifest.ApplicationId != Guid.Empty
            && ValidKeyId(manifest.KeyId) && manifest.EncryptionAlgorithm == "AES-256-GCM"
            && manifest.CreatedAt != default && Enum.IsDefined(manifest.State)
            && ValidReference(entry.IdempotencyReference) && manifest.Objects is not null && entry.EncryptedObjects is not null
            && (manifest.State != BackupRecoveryOperationState.Verified || manifest.Objects.Count > 0 && manifest.Objects.Count == entry.EncryptedObjects.Length)
            && manifest.Objects.All(x => x.Reference is { Length: > 0 and <= 256 } && x.ConsistencyMethod is { Length: > 0 and <= 128 }
                && x.Length >= 0 && x.Sha256 is { Length: 64 } && x.Sha256.All(Uri.IsHexDigit));
    }

    private static bool ValidKeyId(string? value) => value is { Length: >= 1 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool ValidReference(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private sealed record Ledger(BackupManifestEntry[] Entries);
}
