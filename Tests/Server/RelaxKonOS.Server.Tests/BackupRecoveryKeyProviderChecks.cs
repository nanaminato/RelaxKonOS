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
                && restarted.Manifest.ProblemCode == BackupRecoveryProblemCodes.BackupInterrupted,
                "Queued backup was not marked interrupted after restart.");
            var reopened = new BackupRecoveryManifestStore(environment, options);
            TestAssert.Assert(reopened.Find(first.Manifest.ApplicationId, reference)?.Manifest.BackupId == first.Manifest.BackupId,
                "Idempotency reconciliation was not retained across a restart.");
        }
        finally { CryptographicOperations.ZeroMemory(manifestKey); }

        var lifecycleKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new BackupRecoveryOptions
            {
                RootDirectory = "backup-recovery-lifecycle-test",
                KeyId = "lifecycle-test-v1",
                KeyEncryptionKeyBase64 = Convert.ToBase64String(lifecycleKey),
                MaximumObjectBytes = 1024,
                MaximumStoredBytes = 4096,
                MaximumVerifiedBackupsPerApplication = 2,
            };
            var environment = new TestHostEnvironment(root);
            var manifests = new BackupRecoveryManifestStore(environment, options);
            var store = new BackupRecoveryObjectStore(environment, options, new ConfigurationBackupRecoveryKeyProvider(options));
            var application = Guid.NewGuid();
            var first = await CreateVerifiedAsync(manifests, store, application, options, "1");
            var second = await CreateVerifiedAsync(manifests, store, application, options, "2");
            var third = await CreateVerifiedAsync(manifests, store, application, options, "3");
            var anotherApplication = await CreateVerifiedAsync(manifests, store, Guid.NewGuid(), options, "4");

            var retired = manifests.ApplyVerifiedRetention(options.MaximumVerifiedBackupsPerApplication);
            TestAssert.Assert(retired.Length == 1 && retired.Contains(first.Manifest.BackupId),
                "Verified backup retention did not retire only the oldest backup for its application.");
            var retained = new BackupRecoveryManifestStore(environment, options).Read();
            TestAssert.Assert(retained.Length == 3 && retained.Any(entry => entry.Manifest.BackupId == second.Manifest.BackupId)
                && retained.Any(entry => entry.Manifest.BackupId == third.Manifest.BackupId)
                && retained.Any(entry => entry.Manifest.BackupId == anotherApplication.Manifest.BackupId),
                "Verified backup retention did not persist the per-application manifest policy.");

            var staging = Path.Combine(root, options.RootDirectory, "staging", "interrupted");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "definition.tmp"), "incomplete");
            await store.ReconcileAtStartupAsync(manifests.VerifiedBackupIds(), CancellationToken.None);
            var retiredPath = Path.Combine(root, options.RootDirectory, "objects", first.Manifest.BackupId.ToString("N"), "definition.rkbr");
            TestAssert.Assert(!File.Exists(retiredPath) && !Directory.Exists(Path.Combine(root, options.RootDirectory, "staging")),
                "Startup reconciliation retained an expired or interrupted backup artifact.");
            TestAssert.Assert(File.Exists(Path.Combine(root, options.RootDirectory, "objects", second.Manifest.BackupId.ToString("N"), "definition.rkbr")),
                "Startup reconciliation removed an object still referenced by a verified manifest.");
        }
        finally { CryptographicOperations.ZeroMemory(lifecycleKey); }

        var quotaKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new BackupRecoveryOptions
            {
                RootDirectory = "backup-recovery-quota-test",
                KeyId = "quota-test-v1",
                KeyEncryptionKeyBase64 = Convert.ToBase64String(quotaKey),
                MaximumObjectBytes = 1024,
                MaximumStoredBytes = 32,
            };
            var store = new BackupRecoveryObjectStore(new TestHostEnvironment(root), options,
                new ConfigurationBackupRecoveryKeyProvider(options));
            var quotaBackupId = Guid.NewGuid();
            try
            {
                await store.WriteAsync(quotaBackupId, "definition", new MemoryStream(new byte[32], writable: false), CancellationToken.None);
                throw new Exception("A backup exceeding the configured ciphertext quota was accepted.");
            }
            catch (BackupRecoveryException error)
            {
                TestAssert.Assert(error.ProblemCode == BackupRecoveryProblemCodes.StorageLimitExceeded,
                    "Configured ciphertext quota returned the wrong problem.");
            }
            var objectPath = Path.Combine(root, options.RootDirectory, "objects", quotaBackupId.ToString("N"), "definition.rkbr");
            TestAssert.Assert(!File.Exists(objectPath), "A rejected over-quota backup left an encrypted object behind.");
        }
        finally { CryptographicOperations.ZeroMemory(quotaKey); }
    }

    private static async Task<BackupManifestEntry> CreateVerifiedAsync(BackupRecoveryManifestStore manifests,
        BackupRecoveryObjectStore store, Guid applicationId, BackupRecoveryOptions options, string requestSuffix)
    {
        var reference = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestSuffix)));
        var entry = manifests.Create(applicationId, null, options.KeyId, reference, out var created);
        TestAssert.Assert(created, "Fixture backup request unexpectedly reused an idempotency record.");
        var running = manifests.MarkRunning(entry.Manifest.BackupId);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("backup-" + requestSuffix);
        var encrypted = await store.WriteAsync(running.Manifest.BackupId, "definition", new MemoryStream(plaintext, writable: false), CancellationToken.None);
        return manifests.MarkVerified(running.Manifest.BackupId,
            [new BackupObjectDto(BackupObjectKind.ApplicationDefinition, applicationId.ToString("D"), "fixture", encrypted.PlaintextLength, encrypted.PlaintextSha256)],
            [encrypted]);
    }
}
