using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.BackupRecovery;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.EventAlerts;

namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>Schema-versioned, secret-free backup payload for one application definition.</summary>
internal sealed record ApplicationDefinitionBackupPayload(int SchemaVersion, ApplicationDto Application, ApplicationRevisionDto[] Revisions);

/// <summary>
/// Backs up the catalog transaction for an application. Secret bodies and volume contents are not
/// part of this object: configuration records carry only secret-version references, and a future
/// volume/database adapter must produce its own consistency-qualified manifest object.
/// </summary>
internal sealed class ApplicationDefinitionBackupService(
    ApplicationDeploymentCatalogStore catalog,
    IBackupRecoveryKeyProvider keys,
    BackupRecoveryManifestStore manifests,
    BackupRecoveryObjectStore objects,
    ApplicationDeploymentManager applications,
    IOperationalEventPublisher events,
    ILogger<ApplicationDefinitionBackupService> logger,
    BackupRecoveryOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BackupManifestDto> CreateAsync(Guid applicationId, string actor, string idempotencyKey, CancellationToken cancellationToken)
    {
        var availability = keys.Availability;
        if (!availability.Available || availability.KeyId is null)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.RecoveryKeyUnavailable);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 512 || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        // An idempotency key is only meaningful in the scope of the authenticated caller and the
        // exact immutable request.  Do not let one principal observe or replay another principal's
        // operation by choosing the same client-generated key.
        var reference = IdempotencyReference(applicationId, actor, idempotencyKey);
        var created = manifests.Create(applicationId, null, availability.KeyId, reference, out var wasCreated);
        if (!wasCreated) return created.Manifest;
        try
        {
            var running = manifests.MarkRunning(created.Manifest.BackupId);
            var (application, revisions) = catalog.ReadBackupSnapshot(applicationId);
            var current = application.CurrentRevisionId is { } currentId ? revisions.FirstOrDefault(x => x.Id == currentId) : null;
            var payload = new ApplicationDefinitionBackupPayload(1,
                ApplicationDeploymentMapper.Describe(application, current, null, engineAvailable: false, ownedByUs: true),
                [.. revisions.Select(revision => ApplicationDeploymentMapper.Revision(revision, application.CurrentRevisionId))]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            await using var input = new MemoryStream(bytes, writable: false);
            var encrypted = await objects.WriteAsync(running.Manifest.BackupId, "definition", input, cancellationToken);
            var verified = await objects.ReadAndVerifyAsync(running.Manifest.BackupId, "definition", encrypted, cancellationToken);
            try
            {
                if (!verified.AsSpan().SequenceEqual(bytes)) throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(verified); }
            var backupObject = new BackupObjectDto(BackupObjectKind.ApplicationDefinition, application.Id.ToString("D"),
                "catalog-transaction", encrypted.PlaintextLength, encrypted.PlaintextSha256);
            var verifiedManifest = manifests.MarkVerified(running.Manifest.BackupId, [backupObject], [encrypted]).Manifest;
            // Verification is the durability boundary. Retention cleanup is deliberately after it:
            // a cleanup failure cannot relabel a good backup as failed or delete its object.
            try
            {
                var retired = manifests.ApplyVerifiedRetention(options.MaximumVerifiedBackupsPerApplication);
                foreach (var retiredBackupId in retired)
                    await objects.DeleteBackupAsync(retiredBackupId, CancellationToken.None);
            }
            catch (BackupRecoveryException error)
            {
                logger.LogWarning("Backup retention cleanup could not complete after verified backup {BackupId}: {ProblemCode}.",
                    verifiedManifest.BackupId, error.ProblemCode);
            }
            PublishTerminalSignal(verifiedManifest, BackupRecoveryProblemCodes.BackupNotVerified, isRecovery: true);
            return verifiedManifest;
        }
        catch (BackupRecoveryException error)
        {
            try { manifests.MarkFailed(created.Manifest.BackupId, error.ProblemCode); } catch (BackupRecoveryException) { }
            try { await objects.DeleteBackupAsync(created.Manifest.BackupId, CancellationToken.None); } catch (BackupRecoveryException) { }
            PublishTerminalSignal(created.Manifest, error.ProblemCode, isRecovery: false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // HTTP cancellation is not permission to leave a possibly written object behind.
            // The caller still observes cancellation, while the durable record remains explicit.
            try { manifests.MarkFailed(created.Manifest.BackupId, BackupRecoveryProblemCodes.BackupInterrupted); } catch (BackupRecoveryException) { }
            try { await objects.DeleteBackupAsync(created.Manifest.BackupId, CancellationToken.None); } catch (BackupRecoveryException) { }
            PublishTerminalSignal(created.Manifest, BackupRecoveryProblemCodes.BackupInterrupted, isRecovery: false);
            throw;
        }
    }

    public async Task<BackupRestorePreflightDto> PreflightAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(backupId, cancellationToken);
        var blockers = Blockers(payload);
        return new(backupId, payload.Application.Id, payload.Application.Name + "-restored", true, blockers.Count == 0, blockers);
    }

    public async Task<ApplicationDto> RestoreDefinitionAsync(Guid backupId, RestoreApplicationDefinitionRequest request,
        string actor, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) throw new BackupRecoveryException(BackupRecoveryProblemCodes.RestoreConfirmationRequired);
        var payload = await ReadPayloadAsync(backupId, cancellationToken);
        if (Blockers(payload).Count != 0) throw new BackupRecoveryException(BackupRecoveryProblemCodes.RestoreNotAdmissible);
        // Port/site bindings are installation-local runtime resources. A definition restore only
        // recreates portable intent and starts stopped; it never claims the old listener or route.
        var source = payload.Application;
        var restored = new CreateApplicationRequest(request.NewApplicationName, source.SourceKind, source.WorkloadKind,
            source.ReadinessLevel, source.HealthCheckPath, source.ContainerPort, null, source.BindAddress, source.Limits,
            [], [.. source.Configuration.Where(x => !x.IsSecret)], null);
        return await applications.CreateAsync(restored, actor, cancellationToken);
    }

    private async Task<ApplicationDefinitionBackupPayload> ReadPayloadAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var entry = manifests.Get(backupId) ?? throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupNotFound);
        if (entry.Manifest.State != BackupRecoveryOperationState.Verified)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupNotVerified);
        var definitionIndex = entry.Manifest.Objects.ToList().FindIndex(x => x.Kind == BackupObjectKind.ApplicationDefinition);
        if (definitionIndex < 0 || definitionIndex >= entry.EncryptedObjects.Length)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        var bytes = await objects.ReadAndVerifyAsync(backupId, "definition", entry.EncryptedObjects[definitionIndex], cancellationToken);
        try
        {
            var payload = JsonSerializer.Deserialize<ApplicationDefinitionBackupPayload>(bytes, Json);
            if (payload is null || payload.SchemaVersion != 1 || payload.Application.Id != entry.Manifest.ApplicationId)
                throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
            return payload;
        }
        catch (JsonException) { throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static List<string> Blockers(ApplicationDefinitionBackupPayload payload)
    {
        var blockers = new List<string>();
        if (payload.Application.Configuration.Any(x => x.IsSecret)) blockers.Add(BackupRecoveryProblemCodes.SecretRebindRequired);
        if (payload.Application.Volumes.Count > 0) blockers.Add(BackupRecoveryProblemCodes.VolumeAdapterUnavailable);
        return blockers;
    }

    public BackupManifestDto? Get(Guid backupId) => manifests.Get(backupId)?.Manifest;
    public BackupManifestDto? Find(Guid applicationId, string actor, string idempotencyKey) =>
        manifests.Find(applicationId, IdempotencyReference(applicationId, actor, idempotencyKey))?.Manifest;
    public BackupManifestDto[] List(Guid applicationId) => manifests.Read()
        .Where(entry => entry.Manifest.ApplicationId == applicationId).Select(entry => entry.Manifest).ToArray();

    private static string IdempotencyReference(Guid applicationId, string actor, string idempotencyKey)
    {
        if (applicationId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || actor.Length > 512
            || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new BackupRecoveryException(BackupRecoveryProblemCodes.BackupCorrupt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"definition-backup:v1:{actor}:{applicationId:D}:{idempotencyKey}")));
    }

    /// <summary>Alert projection is observability only; its outage never changes the durable backup result.</summary>
    private void PublishTerminalSignal(BackupManifestDto manifest, string problemCode, bool isRecovery)
    {
        try
        {
            events.PublishAsync(new OperationalEventSignal(
                $"backup-definition-terminal:{manifest.BackupId:D}:{manifest.State}",
                "backup.definition_failed", manifest.ApplicationId, Guid.NewGuid(), problemCode,
                manifest.OperationId, IsRecovery: isRecovery)).GetAwaiter().GetResult();
        }
        catch
        {
            logger.LogWarning("Event Alert Center did not record terminal definition backup {BackupId}.", manifest.BackupId);
        }
    }
}
