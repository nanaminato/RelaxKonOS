using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class PreparationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Reject(string code, Fixture fixture, Func<Task<PreparedRuntimeArtifact>>? action = null)
        {
            try { await (action is null ? fixture.PrepareAsync() : action()); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code)
            {
                Check(!Directory.EnumerateFileSystemEntries(fixture.Stage).Any(), "No staging residue after " + code);
                return;
            }
            throw new Exception("Expected preparation rejection " + code);
        }
        using (var f = new Fixture())
        {
            var prepared = await f.PrepareAsync();
            Check(prepared.Authorization.Release.ReleaseId == f.Release.ReleaseId && f.Transport.Calls == 1,
                "Frozen protected release selected, not an arbitrary download URL");
            Check(File.ReadAllBytes(prepared.Archive.EntryPoint).SequenceEqual(f.Binary), "Downloaded and extracted bytes verified");
            Check(Directory.EnumerateFileSystemEntries(f.Stage).Count() == 1, "Downloaded archive removed after extraction");
            RuntimeArchiveExtractor.Discard(prepared.Archive, f.Stage);
            Check(!Directory.EnumerateFileSystemEntries(f.Stage).Any(), "Only owned extracted staging discarded");
        }
        using (var f = new Fixture())
        {
            f.Transport.Bytes = new byte[f.Archive.Length];
            await Reject("archive_digest_mismatch", f);
        }
        using (var f = new Fixture())
        {
            f.Transport.Bytes = new byte[f.Archive.Length + 1];
            await Reject("archive_too_large", f);
        }
        using (var f = new Fixture())
        {
            f.Transport.AfterCopy = () =>
            {
                var disabled = f.Source with { Enabled = false };
                f.Store.Confirm(disabled, 1, disabled.ConfirmationFingerprint, "test-admin", DateTimeOffset.UtcNow);
            };
            await Reject("source_disabled", f);
            Check(f.Transport.Calls == 1, "Trust is rechecked after the actual transfer");
        }
        using (var f = new Fixture())
        {
            f.Transport.AfterCopy = () => f.Accept(f.Manifest with { Revision = 2 });
            await Reject("catalog_revision_conflict", f);
        }
        using (var f = new Fixture())
        {
            await Reject("target_unsupported", f, () => f.Preparer.PrepareAsync(f.Source.SourceId, f.Release.ReleaseId,
                1, 1, f.Release.Target with { Architecture = "other" }, false, f.Stage, default));
            Check(f.Transport.Calls == 0, "Wrong platform rejected before disk/network mutation");
        }
        using (var f = new Fixture(supported: false))
        {
            await Reject("configuration_unsupported", f);
            Check(f.Transport.Calls == 0, "Discovery-only release cannot be prepared for installation");
        }
        using (var f = new Fixture(prerelease: true))
        {
            await Reject("channel_not_allowed", f);
            var prepared = await f.PrepareAsync(allowPrerelease: true);
            Check(prepared.Authorization.Release.Channel == RuntimeChannel.Prerelease, "Explicit prerelease selection reaches preparation");
            RuntimeArchiveExtractor.Discard(prepared.Archive, f.Stage);
        }
        using (var f = new Fixture())
        {
            f.Accept(f.Manifest with { Revision = 2, RevokedReleaseIds = [f.Release.ReleaseId] });
            await Reject("release_untrusted", f, () => f.Preparer.PrepareAsync(f.Source.SourceId, f.Release.ReleaseId,
                2, 1, f.Release.Target, false, f.Stage, default));
            Check(f.Transport.Calls == 0, "Revoked artifact never downloaded");
        }
        using (var f = new Fixture())
        {
            var expired = f.Manifest with { Revision = 2, ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1), IssuedAt = DateTimeOffset.UtcNow.AddHours(-2) };
            f.Accept(expired, DateTimeOffset.UtcNow.AddHours(-2));
            await Reject("catalog_expired", f, () => f.Preparer.PrepareAsync(f.Source.SourceId, f.Release.ReleaseId,
                2, 1, f.Release.Target, false, f.Stage, default));
        }
        using (var f = new Fixture())
        {
            f.Transport.AfterCopy = () => throw new OperationCanceledException();
            try { await f.PrepareAsync(); throw new Exception("Cancellation not propagated"); }
            catch (OperationCanceledException) { Check(!Directory.EnumerateFileSystemEntries(f.Stage).Any(), "Cancelled transfer removes its archive"); }
        }
        using (var f = new Fixture())
        {
            var unowned = Path.Combine(f.Stage, "keep.txt");
            File.WriteAllText(unowned, "caller-data");
            f.Transport.Bytes = new byte[f.Archive.Length];
            try { await f.PrepareAsync(); throw new Exception("Invalid download accepted"); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.archive_digest_mismatch")
            { Check(File.ReadAllText(unowned) == "caller-data" && Directory.EnumerateFileSystemEntries(f.Stage).Count() == 1, "Failure preserves unrelated staging-root files"); }
            try
            {
                RuntimeArchiveExtractor.Discard(new(f.Stage, unowned), f.Stage);
                throw new Exception("Discard accepted root");
            }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.staging_path_invalid")
            { Check(File.Exists(unowned), "Discard cannot recursively remove staging root"); }
        }
        return count;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-preparation-" + Guid.NewGuid().ToString("N"));
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly bool supported;
        internal byte[] Binary { get; } = Encoding.UTF8.GetBytes("example-not-executed-runtime");
        internal byte[] Archive { get; }
        internal string Stage { get; }
        internal RuntimeSource Source { get; }
        internal RuntimeManifest Manifest { get; }
        internal ManifestRelease Release { get; }
        internal RuntimeSourceStore Store { get; }
        internal FakeTransport Transport { get; }
        internal RuntimeArtifactPreparer Preparer { get; }

        internal Fixture(bool supported = true, bool prerelease = false)
        {
            this.supported = supported;
            Stage = Path.Combine(root, "stage"); Directory.CreateDirectory(Stage);
            using (var buffer = new MemoryStream())
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                { using var file = zip.CreateEntry("runtime.exe").Open(); file.Write(Binary); }
                Archive = buffer.ToArray();
            }
            var now = DateTimeOffset.UtcNow;
            Source = new("source-1", "test-runtime", "test-publisher", RuntimeSourceKind.HttpsManifest,
                "https://example.com/catalog/", "manifest.json", "key-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                true, 0, [RuntimeChannel.Stable, RuntimeChannel.Prerelease], 65536, 86400,
                ["https://example.com/artifacts/"], [], []);
            var artifact = new RuntimeArtifact("a-1", Hash(Archive), Archive.Length, "zip", "runtime.exe", "key-1",
                new(1024, 4, [new("runtime.exe", Binary.Length, Hash(Binary), true)]));
            Release = new("r-1", Source.RuntimeId, "1.0.0", prerelease ? RuntimeChannel.Prerelease : RuntimeChannel.Stable,
                new("windows", "amd64", null, null, RuntimeInstallMode.Archive, "scm"), artifact,
                "https://example.com/artifacts/runtime.zip", now);
            Manifest = new(1, Source.SourceId, 1, now, now.AddHours(1), [Release], []);
            Store = new(Path.Combine(root, "trust"), path => Directory.CreateDirectory(path));
            Store.Confirm(Source, 0, Source.ConfirmationFingerprint, "test-admin", now);
            Accept(Manifest);
            Transport = new() { Bytes = Archive };
            Preparer = new(Store, Transport);
        }

        internal Task<PreparedRuntimeArtifact> PrepareAsync(bool allowPrerelease = false) =>
            Preparer.PrepareAsync(Source.SourceId, Release.ReleaseId, 1, 1, Release.Target, allowPrerelease, Stage, default);
        internal void Accept(RuntimeManifest value, DateTimeOffset? now = null)
        {
            var payload = RuntimeJson.Serialize(value);
            var signed = RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(payload).ToArray();
            var envelope = RuntimeJson.Serialize(new SignedRuntimeCatalog(1, Source.KeyId, Convert.ToBase64String(payload),
                Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
            Store.Accept(Source.SourceId, 1, envelope, now ?? DateTimeOffset.UtcNow, _ => new("1.0.0", "test-adapter", supported));
        }
        public void Dispose()
        {
            key.Dispose();
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Test cleanup escaped temp root");
            Directory.Delete(root, true);
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class FakeTransport : IRuntimeArchiveTransport
    {
        internal byte[] Bytes { get; set; } = [];
        internal Action? AfterCopy { get; set; }
        internal int Calls { get; private set; }
        public async Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct)
        {
            Calls++;
            await destination.WriteAsync(Bytes, ct);
            AfterCopy?.Invoke();
        }
    }
}
