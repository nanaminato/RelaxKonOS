using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using Microsoft.Extensions.Configuration;
using RelaxKonOS.Server.Startup;

internal static class ModulePackageChecks
{
    internal static void WriteFixture(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "relaxkon", "official", "https://modules.example/",
            ["example"], new Dictionary<string, string> { ["fixture-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }, 3600);
        byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ModulePackageJson.Options);
        string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var content = Encoding.UTF8.GetBytes("package fixture; not executable code");
        var manifest = new ModulePackageManifest(1, "server-module", "example", "fixture-release", "1.0.0", "relaxkon", "official", rid,
            "1.0.0", "2.0.0", "server/" + rid + "/Fixture.dll", "Fixture.Module", "fixture-build", new string('a', 40), [], [],
            [new("server/" + rid + "/Fixture.dll", content.Length, Hash(content))]);
        var manifestBytes = Json(manifest);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using (var entry = zip.CreateEntry("manifest.json").Open()) entry.Write(manifestBytes);
            using (var entry = zip.CreateEntry(manifest.EntryAssembly).Open()) entry.Write(content);
        }
        var archive = buffer.ToArray();
        var now = DateTimeOffset.UtcNow;
        var catalog = new ModulePackageCatalog(1, "official", 1, now.AddMinutes(-1), now.AddMinutes(30),
            [new(manifest, Hash(manifestBytes), Hash(archive), archive.Length, "https://modules.example/fixture.rkmodule")], [], []);
        var payload = Json(catalog);
        var signature = key.SignData(Encoding.UTF8.GetBytes("RelaxKonOS.ServerModuleCatalog.v1\n").Concat(payload).ToArray(),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllBytes(Path.Combine(directory, "catalog.json"), Json(new SignedModulePackageCatalog(1, "fixture-key",
            Convert.ToBase64String(payload), Convert.ToBase64String(signature))));
        File.WriteAllBytes(Path.Combine(directory, "candidate.rkmodule"), archive);
        File.WriteAllBytes(Path.Combine(directory, "appsettings.host.json"), Json(new { ModuleManagement = new { Authority = "managed" },
            ModulePackages = new { Sources = new Dictionary<string, ModulePackageTrustPolicy> { ["official"] = policy } } }));
        using var store = new ModuleManagementStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ModuleManagement:Authority"] = "managed" }).Build(), directory);
        Console.WriteLine(JsonSerializer.Serialize(new { store.ServiceId, revision = ModulePackageCatalogStore.InitialRevision, manifest.ReleaseId }, ModulePackageJson.Options));
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var context = new ModulePackageContext("package-test-service", "linux-x64", "1.0.0", false);
        var policy = new ModulePackageTrustPolicy(1, "official", 1, true, "relaxkon", "official", "https://modules.example/",
            ["firewall"], new Dictionary<string, string> { ["key-1"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }, 3600, null);
        var empty = ModulePackageTrustHistory.Empty(policy, context);
        var bytes = Encoding.UTF8.GetBytes("candidate bytes, never executed");
        string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
        var manifest = new ModulePackageManifest(1, "server-module", "firewall", "firewall-1-linux-x64", "1.0.0", "relaxkon",
            "official", "linux-x64", "1.0.0", "2.0.0", "server/linux-x64/Firewall.dll", "RelaxKon.Firewall.Module", "build-1",
            new string('a', 40), [], ["firewall"], [new("server/linux-x64/Firewall.dll", bytes.Length, Hash(bytes))]);
        byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ModulePackageJson.Options);
        byte[] Archive(ModulePackageManifest m, Action<ZipArchive>? alter = null, byte[]? manifestBytes = null, byte[]? fileBytes = null)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var stream = zip.CreateEntry("manifest.json").Open()) stream.Write(manifestBytes ?? Json(m));
                using (var stream = zip.CreateEntry(m.EntryAssembly).Open()) stream.Write(fileBytes ?? bytes);
                alter?.Invoke(zip);
            }
            return output.ToArray();
        }
        var archive = Archive(manifest);
        ModulePackageRelease Release(ModulePackageManifest m, byte[] zip, byte[]? metadata = null) =>
            new(m, Hash(metadata ?? Json(m)), Hash(zip), zip.Length, "https://modules.example/releases/firewall.rkmodule");
        var release = Release(manifest, archive);
        var catalog = new ModulePackageCatalog(1, "official", 1, now.AddMinutes(-1), now.AddMinutes(20), [release], [], []);
        byte[] Sign(ModulePackageCatalog value, ECDsa? signingKey = null, string keyId = "key-1")
        {
            var payload = Json(value);
            var signed = Encoding.UTF8.GetBytes("RelaxKonOS.ServerModuleCatalog.v1\n").Concat(payload).ToArray();
            var signature = (signingKey ?? key).SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Json(new SignedModulePackageCatalog(1, keyId, Convert.ToBase64String(payload), Convert.ToBase64String(signature)));
        }
        VerifiedModulePackageCatalog Verify(ModulePackageCatalog value, ModulePackageTrustPolicy? p = null,
            ModulePackageContext? c = null, ModulePackageTrustHistory? h = null, ECDsa? k = null, string keyId = "key-1") =>
            ModulePackageVerifier.VerifyCatalog(Sign(value, k, keyId), p ?? policy, c ?? context, h ?? empty, now);
        void Reject(Action action, string code, string name)
        {
            try { action(); } catch (ModulePackageException e) when (e.ProblemCode == "module-package-" + code)
            { check(true, "module package " + name); return; }
            throw new Exception("module package rejection absent: " + name);
        }
        async Task RejectArchive(byte[] zip, ModulePackageCatalog value, string code, string name)
        {
            try { await ModulePackageVerifier.VerifyCandidateAsync(new MemoryStream(zip), Sign(value), policy, context, empty,
                manifest.ReleaseId, now); }
            catch (ModulePackageException e) when (e.ProblemCode == "module-package-" + code)
            { check(true, "module package " + name); return; }
            throw new Exception("module package archive rejection absent: " + name);
        }
        var accepted = Verify(catalog);
        check(accepted.NextHistory.Sequence == 1 && empty.Sequence == 0 && empty.KeyHashes.Count == 0,
            "module package verification returns isolated next history without mutating source");
        var candidate = await ModulePackageVerifier.VerifyCandidateAsync(new MemoryStream(archive), Sign(catalog), policy, context, empty,
            manifest.ReleaseId, now);
        check(candidate.ReadOnly && candidate.ArchiveSha256 == release.ArchiveSha256 && candidate.Manifest.ReleaseId == manifest.ReleaseId,
            "module package exact signed candidate validates without executing code");
        check(Verify(catalog, h: accepted.NextHistory).PayloadSha256 == accepted.PayloadSha256,
            "module package same sequence exact payload remains verifiable");
        var badEnvelope = ModulePackageJson.Read<SignedModulePackageCatalog>(Sign(catalog)) with { Signature = Convert.ToBase64String(new byte[64]) };
        Reject(() => ModulePackageVerifier.VerifyCatalog(Json(badEnvelope), policy, context, empty, now), "signature-invalid", "forged signature rejected");
        Reject(() => Verify(catalog, p: policy with { Enabled = false }), "source-invalid", "disabled source rejected");
        Reject(() => Verify(catalog, p: policy with { AuthorizedModuleIds = ["git"] }), "release-unauthorized", "host module authorization enforced");
        Reject(() => Verify(catalog with { ExpiresAt = now }), "catalog-expired", "expired catalog rejected");
        Reject(() => Verify(catalog with { IssuedAt = now.AddSeconds(1) }), "catalog-expired", "future catalog rejected");
        Reject(() => Verify(catalog with { ExpiresAt = now.AddDays(1) }), "catalog-expired", "source validity bound enforced");
        Reject(() => Verify(catalog with { Sequence = 0 }), "catalog-replay", "zero catalog sequence rejected");
        Reject(() => Verify(catalog with { ExpiresAt = now.AddMinutes(19) }, h: accepted.NextHistory), "catalog-replay", "same sequence changed payload rejected");
        var newer = Verify(catalog with { Sequence = 2 }, h: accepted.NextHistory);
        Reject(() => Verify(catalog, h: newer.NextHistory), "catalog-replay", "older signed catalog rejected");
        Reject(() => Verify(catalog, p: policy with { MaximumValiditySeconds = 3500 }, h: accepted.NextHistory), "policy-replay", "same policy revision changed authorization rejected");
        var changed = release with { ArchiveSha256 = new string('b', 64) };
        Reject(() => Verify(catalog with { Sequence = 2, Releases = [changed] }, h: accepted.NextHistory), "release-equivocation", "release identity immutable");
        var renamed = release with { Manifest = manifest with { ReleaseId = "another-release" } };
        Reject(() => Verify(catalog with { Sequence = 2, Releases = [renamed] }, h: accepted.NextHistory), "version-equivocation", "module version rid immutable across release ids");
        var revoked = Verify(catalog with { Sequence = 2, RevokedReleaseIds = [manifest.ReleaseId], RevokedDigests = [release.ArchiveSha256] }, h: accepted.NextHistory);
        var revokedAgain = Verify(catalog with { Sequence = 3 }, h: revoked.NextHistory);
        check(revokedAgain.NextHistory.RevokedReleaseIds.Contains(manifest.ReleaseId)
            && revokedAgain.NextHistory.RevokedDigests.Contains(release.ArchiveSha256), "module package revocations persist when later catalog omits them");
        await RejectArchive(archive, catalog with { RevokedReleaseIds = [manifest.ReleaseId] }, "release-revoked", "revoked release candidate rejected");
        await RejectArchive(archive, catalog with { RevokedDigests = [release.ArchiveSha256] }, "release-revoked", "revoked archive candidate rejected");
        var rotatedPolicy = policy with { Revision = 2, PublicKeysSpki = new Dictionary<string, string> { ["key-2"] = Convert.ToBase64String(nextKey.ExportSubjectPublicKeyInfo()) } };
        var rotated = Verify(catalog with { Sequence = 2 }, rotatedPolicy, h: accepted.NextHistory, k: nextKey, keyId: "key-2");
        check(rotated.NextHistory.RetiredKeyIds.Contains("key-1"), "module package removed trust key permanently retired");
        Reject(() => Verify(catalog with { Sequence = 3 }, policy with { Revision = 3 }, h: rotated.NextHistory), "key-invalid", "retired key cannot be reintroduced");
        Reject(() => Verify(catalog, policy with { Revision = 2, PublicKeysSpki = new Dictionary<string, string> { ["key-1"] = Convert.ToBase64String(nextKey.ExportSubjectPublicKeyInfo()) } },
            h: accepted.NextHistory, k: nextKey), "key-id-reused", "key id cannot change key material");
        Reject(() => Verify(catalog, h: accepted.NextHistory with { ServiceId = "another-host" }), "history-identity-invalid", "trust history bound to server identity");
        var developmentPolicy = policy with { Purpose = "development", DevelopmentServiceId = context.ServiceId };
        Reject(() => Verify(catalog, developmentPolicy), "development-source-forbidden", "development source forbidden on production host");
        var developmentCatalog = catalog with { Releases = [release with { Manifest = manifest with { Purpose = "development" } }] };
        check(Verify(developmentCatalog, developmentPolicy, context with { DevelopmentInstance = true },
            ModulePackageTrustHistory.Empty(developmentPolicy, context)).Catalog.Releases[0].Manifest.Purpose == "development",
            "module package development source requires explicit bound development instance");
        Reject(() => Verify(catalog with { Releases = [release with { DownloadUri = "https://attacker.example/module" }] }), "download-origin-invalid", "signed release cannot escape configured origin");
        await RejectArchive(archive[..^1], catalog, "archive-size", "truncated archive rejected before zip parsing");
        var corrupted = archive.ToArray(); corrupted[10] ^= 1;
        await RejectArchive(corrupted, catalog, "archive-digest", "archive mutation rejected");
        var changedFile = Archive(manifest, fileBytes: new byte[bytes.Length]);
        await RejectArchive(changedFile, catalog with { Releases = [Release(manifest, changedFile)] }, "file-digest", "per file digest enforced after signed archive digest");
        var unexpected = Archive(manifest, zip => zip.CreateEntry("unlisted.dll"));
        await RejectArchive(unexpected, catalog with { Releases = [Release(manifest, unexpected)] }, "archive-file-set", "undeclared files rejected");
        var duplicate = Archive(manifest, zip => zip.CreateEntry(manifest.EntryAssembly.ToUpperInvariant()));
        await RejectArchive(duplicate, catalog with { Releases = [Release(manifest, duplicate)] }, "archive-file-set", "duplicate extra entry rejected");
        var linked = Archive(manifest, zip => { });
        using (var memory = new MemoryStream())
        {
            memory.Write(linked); memory.Position = 0;
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true))
                zip.GetEntry(manifest.EntryAssembly)!.ExternalAttributes = unchecked((int)0xa1ff0000);
            linked = memory.ToArray();
        }
        await RejectArchive(linked, catalog with { Releases = [Release(manifest, linked)] }, "archive-entry-invalid", "symbolic links rejected");
        var duplicateJson = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Json(manifest)).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal));
        var duplicateManifest = Archive(manifest, manifestBytes: duplicateJson);
        await RejectArchive(duplicateManifest, catalog with { Releases = [Release(manifest, duplicateManifest, duplicateJson)] }, "metadata-invalid", "duplicate manifest json properties rejected");
        Reject(() => Verify(catalog with { Releases = [release with { Manifest = manifest with { EntryAssembly = "../evil.dll" } }] }), "entry-assembly-invalid", "entry assembly traversal rejected");
        Reject(() => Verify(catalog with { Releases = [release with { Manifest = manifest with { Version = "01.0.0" } }] }), "version-invalid", "noncanonical versions rejected");
        Reject(() => Verify(catalog with { Releases = [release with { Manifest = manifest with { ModuleId = "firewall\n" } }] }), "manifest-invalid", "module identity trailing newline rejected");
        Reject(() => Verify(catalog with { Releases = [release with { ArchiveSha256 = release.ArchiveSha256 + "\n" }] }), "archive-metadata-invalid", "digest trailing newline rejected");
        Reject(() => Verify(catalog with { Releases = [release with { Manifest = manifest with { ModuleId = "relaxkonos.core" } }] }), "manifest-invalid", "Core cannot be represented as an installable module candidate");
        Reject(() => Verify(catalog with { Releases = [release with { Manifest = manifest with { Files = [manifest.Files[0], manifest.Files[0] with { Path = manifest.EntryAssembly.ToUpperInvariant() }] } }] }), "file-invalid", "case ambiguous file names rejected");
        foreach (var (changedContext, expectedCode) in new[] {
            (context with { Rid = "win-x64" }, "rid-mismatch"), (context with { HostApiVersion = "2.0.0" }, "host-api-incompatible") })
        {
            try { await ModulePackageVerifier.VerifyCandidateAsync(new MemoryStream(archive), Sign(catalog), policy, changedContext, empty, manifest.ReleaseId, now); }
            catch (ModulePackageException e) when (e.ProblemCode == "module-package-" + expectedCode)
            { check(true, "module package " + expectedCode + " rejected"); continue; }
            throw new Exception("candidate compatibility gate absent");
        }
        var root = Path.Combine(Path.GetTempPath(), "module-package-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string serviceId;
            using (var store = new ModuleManagementStore(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ModuleManagement:Authority"] = "managed" }).Build(), root))
                serviceId = store.ServiceId;
            var nativeManifest = manifest with { Rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                EntryAssembly = "server/" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + "/Firewall.dll",
                Files = [manifest.Files[0] with { Path = "server/" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + "/Firewall.dll" }] };
            var nativeArchive = Archive(nativeManifest);
            var nativeCatalog = catalog with { Releases = [Release(nativeManifest, nativeArchive)] };
            await File.WriteAllBytesAsync(Path.Combine(root, "appsettings.host.json"), Json(new { ModuleManagement = new { Authority = "managed" },
                ModulePackages = new { Sources = new Dictionary<string, ModulePackageTrustPolicy> { [policy.SourceId] = policy } } }));
            var archivePath = Path.Combine(root, "candidate.rkmodule");
            var catalogPath = Path.Combine(root, "catalog.json");
            await File.WriteAllBytesAsync(archivePath, nativeArchive);
            await File.WriteAllBytesAsync(catalogPath, Sign(nativeCatalog));
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));
            var output = new StringWriter();
            var originalOutput = Console.Out;
            int exit;
            try
            {
                Console.SetOut(output);
                exit = await ModulePackageMaintenanceCommand.RunAsync(["modules", "verify-package", "--content-root", root,
                    "--source-id", policy.SourceId, "--catalog", catalogPath, "--archive", archivePath, "--release-id", nativeManifest.ReleaseId]);
            }
            finally { Console.SetOut(originalOutput); }
            using var result = JsonDocument.Parse(output.ToString());
            check(exit == 0 && result.RootElement.GetProperty("readOnly").GetBoolean()
                && !result.RootElement.GetProperty("trustHistoryCommitted").GetBoolean()
                && !result.RootElement.GetProperty("installed").GetBoolean(), "module package real maintenance command reports inspection without acceptance or installation");
            check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before.Count
                && before.All(p => Hash(File.ReadAllBytes(p.Key)) == p.Value)
                && ModuleManagementStore.ReadRecoveryReport(root).ServiceId == serviceId,
                "module package maintenance leaves state archive configuration and trust history unchanged");
            var nativeContext = context with { ServiceId = serviceId, Rid = nativeManifest.Rid };
            var catalogPathState = Path.Combine(root, "data", "module-management", "package-catalogs.json");
            var markerPath = Path.Combine(root, "data", "module-management", "package-catalogs.initialized");
            using (var liveHost = new ModuleManagementStore(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ModuleManagement:Authority"] = "managed" }).Build(), root))
                Reject(() => { using var conflict = new ModulePackageCatalogStore(root, serviceId); }, "host-lock-unavailable", "catalog acceptance excludes a running Host");
            Reject(() => { using var conflict = new ModulePackageCatalogStore(root, "another-host"); }, "service-mismatch", "catalog acceptance checks persisted server identity after locking");
            check(ModulePackageCatalogStore.Read(root, serviceId).Revision == ModulePackageCatalogStore.InitialRevision
                && !File.Exists(catalogPathState), "module package uninitialized catalog inspection creates no state");
            ModulePackageCatalogStore.Receipt receipt;
            using (var store = new ModulePackageCatalogStore(root, serviceId))
            {
                Reject(() => store.Accept(Sign(nativeCatalog), policy, nativeContext, "stale", "test-actor", "accept-1", now),
                    "revision-conflict", "catalog acceptance requires exact revision");
                Reject(() => store.Accept(Json(badEnvelope), policy, nativeContext, ModulePackageCatalogStore.InitialRevision, "test-actor", "bad-signature", now),
                    "signature-invalid", "catalog invalid signature cannot initialize history");
                check(!File.Exists(catalogPathState) && !File.Exists(markerPath), "module package failed catalog prechecks leave no durable state");
                receipt = store.Accept(Sign(nativeCatalog), policy, nativeContext, ModulePackageCatalogStore.InitialRevision, "test-actor", "accept-1", now);
                check(receipt.Sequence == 1 && File.Exists(catalogPathState) && File.ReadAllText(markerPath) == serviceId,
                    "module package catalog acceptance commits history catalog receipt and bootstrap marker");
                // Replay the exact envelope, not a fresh ECDSA signature over the same payload.
                var saved = ModulePackageCatalogStore.Read(root, serviceId);
                var exactEnvelope = Encoding.UTF8.GetBytes(saved.Sources[policy.SourceId].Envelope);
                var stateHash = Hash(File.ReadAllBytes(catalogPathState));
                var replay = store.Accept(exactEnvelope, policy, nativeContext, ModulePackageCatalogStore.InitialRevision, "test-actor", "accept-1", now.AddDays(2));
                check(replay == receipt && Hash(File.ReadAllBytes(catalogPathState)) == stateHash,
                    "module package catalog exact idempotent replay returns historical receipt without writes even after expiry");
                Reject(() => store.Accept(Sign(nativeCatalog with { Sequence = 2 }), policy, nativeContext, receipt.Revision, "test-actor", "accept-1", now),
                    "idempotency-conflict", "catalog idempotency key cannot change target");
                Reject(() => store.Accept(Sign(nativeCatalog with { Sequence = 2 }), policy, nativeContext, ModulePackageCatalogStore.InitialRevision, "other-actor", "accept-1", now),
                    "revision-conflict", "catalog actors cannot bypass stale revision");
            }
            var durable = ModulePackageCatalogStore.Read(root, serviceId);
            check(durable.Receipts.Single() == receipt && durable.Sources[policy.SourceId].History.Sequence == 1,
                "module package catalog history and receipt survive a fresh store read");
            var current = ModulePackageCatalogStore.ReadCurrentCatalog(root, policy, nativeContext, now);
            check(current.Catalog.Sequence == 1 && current.Catalog.Releases.Single().Manifest.Rid == nativeContext.Rid,
                "module package accepted directory revalidates signature current policy and exact saved history");
            Reject(() => ModulePackageCatalogStore.ReadCurrentCatalog(root, policy, nativeContext, now.AddDays(1)),
                "catalog-expired", "persisted catalog is not usable after expiry");
            Reject(() => ModulePackageCatalogStore.ReadCurrentCatalog(root, policy with { Revision = 2 }, nativeContext, now),
                "source-refresh-required", "changed deployment policy requires durable source refresh before use");
            var mirrorPolicy = policy with { SourceId = "mirror", ArtifactOrigin = "https://mirror.example/" };
            var mirrorRelease = nativeCatalog.Releases[0] with { DownloadUri = "https://mirror.example/firewall.rkmodule" };
            var mirrorCatalog = nativeCatalog with { SourceId = "mirror", Releases = [mirrorRelease] };
            using (var store = new ModulePackageCatalogStore(root, serviceId))
            {
                Reject(() => store.Accept(Sign(mirrorCatalog with { Releases = [mirrorRelease with { ArchiveSha256 = new string('c', 64) }] }),
                    mirrorPolicy, nativeContext, receipt.Revision, "test-actor", "mirror-conflict", now),
                    "cross-source-equivocation", "catalog sources cannot redefine existing module version rid bytes");
                var mirrorReceipt = store.Accept(Sign(mirrorCatalog), mirrorPolicy, nativeContext, receipt.Revision, "test-actor", "mirror-same", now);
                check(ModulePackageCatalogStore.Read(root, serviceId).Sources.Count == 2,
                    "module package separate source can advertise identical immutable bytes from its own origin");
                var revokedReceipt = store.Accept(Sign(nativeCatalog with { Sequence = 2, RevokedReleaseIds = [nativeManifest.ReleaseId] }),
                    policy, nativeContext, mirrorReceipt.Revision, "test-actor", "revoke", now);
                store.Accept(Sign(nativeCatalog with { Sequence = 3 }), policy, nativeContext, revokedReceipt.Revision, "test-actor", "omit-revoke", now);
                check(ModulePackageCatalogStore.Read(root, serviceId).Sources[policy.SourceId].History.RevokedReleaseIds.Contains(nativeManifest.ReleaseId),
                    "module package durable revocation survives subsequent catalog omission");
                check(ModulePackageCatalogStore.ReadCurrentCatalog(root, mirrorPolicy, nativeContext, now).NextHistory.RevokedDigests.Contains(mirrorRelease.ArchiveSha256),
                    "module package accepted mirror cannot resurrect another accepted source revoked bytes");
                var stateBytes = File.ReadAllBytes(catalogPathState);
                File.AppendAllText(catalogPathState, " ");
                Reject(() => store.Accept(Sign(nativeCatalog with { Sequence = 4 }), policy, nativeContext,
                    ModulePackageCatalogStore.Read(root, serviceId).Revision, "test-actor", "external-edit", now),
                    "revision-conflict", "catalog acceptance detects out of band file edits before writing");
                File.WriteAllBytes(catalogPathState, stateBytes);
                var boundary = store.Accept(Sign(nativeCatalog with { Sequence = 4,
                    RevokedDigests = Enumerable.Range(1, 9999).Select(i => i.ToString("x64")).ToArray() }), policy, nativeContext,
                    ModulePackageCatalogStore.Read(root, serviceId).Revision, "test-actor", "revocation-boundary", now);
                check(ModulePackageCatalogStore.ReadCurrentCatalog(root, policy, nativeContext, now).NextHistory.RevokedDigests.Length == 10000,
                    "module package aggregate revocation capacity permits the exact supported boundary");
                var boundaryHash = Hash(File.ReadAllBytes(catalogPathState));
                Reject(() => store.Accept(Sign(mirrorCatalog with { Sequence = 2,
                    RevokedDigests = Enumerable.Range(10000, 3).Select(i => i.ToString("x64")).ToArray() }), mirrorPolicy, nativeContext,
                    boundary.Revision, "test-actor", "revocation-overflow", now), "history-capacity", "catalog aggregate revocation overflow rejected before persistence");
                check(Hash(File.ReadAllBytes(catalogPathState)) == boundaryHash,
                    "module package aggregate overflow leaves prior usable directory and receipt intact");
            }
            if (!OperatingSystem.IsWindows())
                check(File.GetUnixFileMode(markerPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                    && File.GetUnixFileMode(catalogPathState) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "module package catalog state and marker are private on Unix");
            var preservedState = File.ReadAllBytes(catalogPathState);
            File.Delete(catalogPathState);
            Reject(() => ModulePackageCatalogStore.Read(root, serviceId), "state-recovery-required", "missing catalog state cannot reset trust after bootstrap marker");
            File.WriteAllBytes(catalogPathState, preservedState);
            var markerBytes = File.ReadAllBytes(markerPath);
            File.Delete(markerPath);
            Reject(() => ModulePackageCatalogStore.Read(root, serviceId), "state-recovery-required", "missing initialization marker cannot silently adopt catalog state");
            File.WriteAllBytes(markerPath, markerBytes);

            using (var liveHost = new ModuleManagementStore(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ModuleManagement:Authority"] = "managed" }).Build(), root))
            {
                using var online = new ModulePackageCatalogStore(root, serviceId, takeHostLock: false);
                var onlineRevision = ModulePackageCatalogStore.Read(root, serviceId).Revision;
                var onlineReceipt = online.Accept(Sign(nativeCatalog with { Sequence = 5 }), policy, nativeContext,
                    onlineRevision, "online-actor", "online-refresh", now);
                check(onlineReceipt.Sequence == 5
                    && ModulePackageCatalogStore.Read(root, serviceId).Sources[policy.SourceId].History.Sequence == 5,
                    "catalog acceptance can commit while a Host holds the host lock through the catalog lock");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
