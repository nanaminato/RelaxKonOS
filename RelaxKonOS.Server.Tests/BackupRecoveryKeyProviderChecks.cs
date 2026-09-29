using System.Security.Cryptography;
using RelaxKonOS.Protocol.BackupRecovery;
using RelaxKonOS.Server.BackupRecovery;

internal static class BackupRecoveryKeyProviderChecks
{
    public static async Task RunAsync(string root)
    {
        var unavailable = new ConfigurationBackupRecoveryKeyProvider(new BackupRecoveryOptions());
        TestAssert.Assert(!unavailable.Availability.Available, "Backup recovery was available without an operator recovery key.");
        try { unavailable.Wrap(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32)); throw new Exception("Missing recovery key was accepted."); }
        catch (BackupRecoveryException error) { TestAssert.Assert(error.ProblemCode == BackupRecoveryProblemCodes.RecoveryKeyUnavailable, "Missing recovery key returned the wrong problem."); }

        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new ConfigurationBackupRecoveryKeyProvider(new BackupRecoveryOptions
        {
            KeyId = "test-recovery-v1",
            KeyEncryptionKeyBase64 = Convert.ToBase64String(key),
        });
        var backupId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var envelope = provider.Wrap(backupId, dataKey);
            TestAssert.Assert(envelope.KeyId == "test-recovery-v1" && envelope.CiphertextBase64 != Convert.ToBase64String(dataKey),
                "Backup data key was not enveloped.");
            var unwrapped = provider.Unwrap(backupId, envelope);
            try { TestAssert.Assert(CryptographicOperations.FixedTimeEquals(dataKey, unwrapped), "Backup data key did not round-trip."); }
            finally { CryptographicOperations.ZeroMemory(unwrapped); }
            try { provider.Unwrap(Guid.NewGuid(), envelope); throw new Exception("Envelope was accepted for a different backup."); }
            catch (BackupRecoveryException error) { TestAssert.Assert(error.ProblemCode == BackupRecoveryProblemCodes.BackupCorrupt, "Envelope binding failure returned the wrong problem."); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(dataKey);
        }

        var storageKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new BackupRecoveryOptions
            {
                RootDirectory = "backup-recovery-test",
                KeyId = "storage-test-v1",
                KeyEncryptionKeyBase64 = Convert.ToBase64String(storageKey),
                MaximumObjectBytes = 1024,
            };
            var storageProvider = new ConfigurationBackupRecoveryKeyProvider(options);
            var store = new BackupRecoveryObjectStore(new TestHostEnvironment(root), options, storageProvider);
            var storedBackupId = Guid.NewGuid();
            var expected = "application definition backup"u8.ToArray();
            var metadata = await store.WriteAsync(storedBackupId, "definition", new MemoryStream(expected, writable: false), CancellationToken.None);
            var actual = await store.ReadAndVerifyAsync(storedBackupId, "definition", metadata, CancellationToken.None);
            try { TestAssert.Assert(actual.SequenceEqual(expected), "Encrypted backup object did not round-trip."); }
            finally { CryptographicOperations.ZeroMemory(actual); }

            var path = Path.Combine(root, options.RootDirectory, "objects", storedBackupId.ToString("N"), "definition.rkbr");
            var tampered = await File.ReadAllBytesAsync(path);
            tampered[^1] ^= 1;
            await File.WriteAllBytesAsync(path, tampered);
            try { await store.ReadAndVerifyAsync(storedBackupId, "definition", metadata, CancellationToken.None); throw new Exception("Tampered backup object was accepted."); }
            catch (BackupRecoveryException error) { TestAssert.Assert(error.ProblemCode == BackupRecoveryProblemCodes.BackupCorrupt, "Tampered object returned the wrong problem."); }
        }
        finally { CryptographicOperations.ZeroMemory(storageKey); }

        var manifestKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new BackupRecoveryOptions
            {
                RootDirectory = "backup-recovery-manifest-test",
                KeyId = "manifest-test-v1",
                KeyEncryptionKeyBase64 = Convert.ToBase64String(manifestKey),
            };
            var environment = new TestHostEnvironment(root);
            var manifests = new BackupRecoveryManifestStore(environment, options);
            const string reference = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var first = manifests.Create(Guid.NewGuid(), null, options.KeyId, reference, out var created);
            TestAssert.Assert(created && first.Manifest.State == BackupRecoveryOperationState.Queued,
                "Backup manifest was not durably queued.");
            var repeat = manifests.Create(first.Manifest.ApplicationId, null, options.KeyId, reference, out created);
            TestAssert.Assert(!created && repeat.Manifest.BackupId == first.Manifest.BackupId,
                "Same backup request did not return its idempotent manifest.");
            TestAssert.Assert(manifests.Find(first.Manifest.ApplicationId, reference)?.Manifest.BackupId == first.Manifest.BackupId,
                "Persisted backup request could not be reconciled by its idempotency reference.");
            manifests.MarkInterruptedAtStartup();
            var restarted = new BackupRecoveryManifestStore(environment, options).Get(first.Manifest.BackupId);
            TestAssert.Assert(restarted?.Manifest.State == BackupRecoveryOperationState.Interrupted
                && restarted.Manifest.ProblemCode == "backup-recovery.interrupted",
                "Queued backup was not marked interrupted after restart.");
            var reopened = new BackupRecoveryManifestStore(environment, options);
            TestAssert.Assert(reopened.Find(first.Manifest.ApplicationId, reference)?.Manifest.BackupId == first.Manifest.BackupId,
                "Idempotency reconciliation was not retained across a restart.");
        }
        finally { CryptographicOperations.ZeroMemory(manifestKey); }
    }
}
