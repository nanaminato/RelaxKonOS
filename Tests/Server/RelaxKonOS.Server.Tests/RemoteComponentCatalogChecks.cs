using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ModulePackaging;

internal static class RemoteComponentCatalogChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
        void Refuse(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is RuntimePolicyException or InvalidDataException or IOException) { count++; return; }
            throw new Exception("Accepted " + label);
        }
        async Task RefuseAsync(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is RuntimePolicyException or InvalidDataException or IOException) { count++; return; }
            throw new Exception("Accepted " + label);
        }
        using var fixture = new Fixture();
        var store = fixture.Store;
        var policy = fixture.Policy();
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "unapproved source fetch");
        Check(fixture.Transport.Requests.Count == 0, "unapproved metadata never reaches network");
        Refuse(() => store.Approve(policy, 0, "wrong-fingerprint"), "approval without exact policy fingerprint");
        Refuse(() => store.Approve(policy with { BaseUri = "http://example.test/" }, 0, policy.Fingerprint), "plaintext source");
        Refuse(() => (policy with { InstallerProviders = ["shell"] }).Validate(), "shell provider");
        Refuse(() => (policy with { Trust = "official" }).Validate(), "self-assigned official identity");
        foreach (var path in new[] { "../outside.json", "https://evil.example/a", "/absolute", "a/%2e%2e/b", "a\\b", "//server/share", "a?secret", "a#fragment" })
            Refuse(() => RemoteCatalogRules.Resolve(path), "escaping reference " + path);
        Check(RemoteCatalogRules.Resolve("../packages/app.roapp", "./manifests/app.json") == "packages/app.roapp", "same relative references work over HTTPS");
        store.Approve(policy, 0, policy.Fingerprint);
        fixture.Publish(1);
        await store.RefreshAsync(policy.SourceId);
        Check(store.Sources().Single().Status == "ready", "valid signed metadata accepted");
        var source = new HttpCatalogSource(store, policy.SourceId);
        Check(source.Load().Components.Single().Id == "example.app", "remote discovery does not need a compiled component entry");
        var auth = source.Authorize("example.app", new("linux", "x64"));
        await using (var input = await source.OpenAsync("../packages/app.roapp", source.ManifestReference("example.app")))
        {
            using var output = new MemoryStream(); await input.CopyToAsync(output);
            Check(output.ToArray().SequenceEqual(fixture.Artifact), "signed artifact digest/length and relative base are enforced");
        }
        using (var lease = store.AcquireInstallLease(auth))
        {
            lease.Revalidate(); Check(true, "frozen authorization may enter installation");
            Check(store.Sources().Count == 1 && store.Snapshot(policy.SourceId).Publication.Sequence == 1,
                "UI reads remain available during asynchronous domain installation");
            Refuse(() => store.SetEnabled(policy.SourceId, false, store.Revision), "policy mutation during active domain installation");
        }

        // Even correctly signed input is untrusted structured data; reject null entries without crashing the caller.
        var malformed = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"components\":[null],\"workloads\":[]}");
        var signed = ModulePackageJson.Read<SignedComponentCatalog>(fixture.Transport.Files["catalog.signed.json"]);
        var publication = ModulePackageJson.Read<ComponentCatalogPublication>(Convert.FromBase64String(signed.Payload));
        fixture.Transport.Files["catalog.json"] = malformed;
        fixture.Transport.Files["catalog.signed.json"] = ComponentCatalogPublisher.Sign(publication with
        {
            Sequence = 2,
            Resources = publication.Resources.Select(resource => resource.Path == "catalog.json"
                ? resource with { Bytes = malformed.Length, Sha256 = RemoteCatalogRules.Hash(malformed) } : resource).ToArray()
        }, fixture.KeyId, fixture.Key);
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "signed null manifest entry");
        Check(store.Snapshot(policy.SourceId).Publication.Sequence == 1, "malformed signed metadata cannot advance the high water");
        fixture.Publish(1);

        fixture.Transport.Files["catalog.signed.json"] = fixture.Transport.Files["catalog.signed.json"].Select((value, index) => index == 0 ? (byte)'!' : value).ToArray();
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "forged envelope");
        Check(store.Snapshot(policy.SourceId).Publication.Sequence == 1 && store.Sources().Single().Status == "refresh-failed", "failed refresh is not a successful empty catalog");
        fixture.Publish(2); await store.RefreshAsync(policy.SourceId);
        fixture.Publish(1); await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "sequence rollback");
        fixture.Publish(2, issued: fixture.Clock.Now.AddSeconds(-2));
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "same sequence different content");
        fixture.Artifact = [4, 5, 6]; fixture.Publish(3);
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "same version different bytes");
        fixture.Artifact = [1, 2, 3]; fixture.Publish(3); await store.RefreshAsync(policy.SourceId);

        var beforeDisable = store.Authorize(policy.SourceId, "example.app", new("linux", "x64"));
        store.SetEnabled(policy.SourceId, false, store.Revision);
        Refuse(() => store.AcquireInstallLease(beforeDisable), "disable after plan");
        store.Approve(policy, store.Revision, policy.Fingerprint);
        Refuse(() => store.Authorize(policy.SourceId, "example.app", new("linux", "x64")), "changed policy requires refreshed acceptance");
        await store.RefreshAsync(policy.SourceId);

        var noProviders = policy with { InstallerProviders = ["apt"] };
        store.Approve(noProviders, store.Revision, noProviders.Fingerprint);
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "unapproved installer mechanism");
        store.Approve(policy, store.Revision, policy.Fingerprint); await store.RefreshAsync(policy.SourceId);

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var reused = policy with { PublicKeysSpki = new Dictionary<string, string> { [fixture.KeyId] = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()) } };
        Refuse(() => store.Approve(reused, store.Revision, reused.Fingerprint), "key ID reused with new material");
        var rotated = policy with { PublicKeysSpki = new Dictionary<string, string> { ["second-key"] = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()) } };
        store.Approve(rotated, store.Revision, rotated.Fingerprint);
        await RefuseAsync(() => store.RefreshAsync(policy.SourceId), "retired signing key");
        Refuse(() => store.Approve(policy, store.Revision, policy.Fingerprint), "retired key reintroduction");
        fixture.Publish(3, signingKey: otherKey, keyId: "second-key"); await store.RefreshAsync(policy.SourceId);

        var mirror = fixture.Policy("mirror-source");
        store.Approve(mirror, store.Revision, mirror.Fingerprint);
        fixture.Publish(1, sourceId: mirror.SourceId); await store.RefreshAsync(mirror.SourceId);
        var mirrorAuth = store.Authorize(mirror.SourceId, "example.app", new("linux", "x64"));
        fixture.Publish(4, signingKey: otherKey, keyId: "second-key", revokedDigests: [RemoteCatalogRules.Hash(fixture.Artifact)]);
        await store.RefreshAsync(policy.SourceId);
        Refuse(() => store.AcquireInstallLease(mirrorAuth), "mirror resurrection after revocation");
        store.Remove(policy.SourceId, store.Revision);
        Refuse(() => store.AcquireInstallLease(mirrorAuth), "removal cannot erase global publisher revocation");

        var revoked = store.Snapshot(mirror.SourceId).Publication.Sequence;
        var cache = Path.Combine(fixture.StateRoot, "cache");
        foreach (var file in Directory.GetFiles(cache)) File.Delete(file);
        Check(store.Snapshot(mirror.SourceId).Publication.Sequence == revoked, "cache deletion cannot reset high water");
        fixture.Clock.Now = fixture.Clock.Now.AddDays(8);
        Check(store.Snapshot(mirror.SourceId).Catalog.Components.Count == 1, "expired metadata remains displayable");
        Refuse(() => new HttpCatalogSource(store, mirror.SourceId).Load(), "expired metadata cannot authorize installation");
        fixture.Reopen(); store = fixture.Store;
        Check(store.Sources().Single().Status == "expired", "expiry and trust history survive restart");
        Refuse(() => store.Approve(policy, store.Revision, policy.Fingerprint), "retired keys survive removal and restart");
        fixture.Store.Dispose();
        File.Delete(Path.Combine(fixture.StateRoot, "trust-state.json"));
        fixture.Reopen();
        Check(fixture.Store.RecoveryProblem is not null, "missing trust journal is recovery required, never fresh trust");
        Refuse(() => fixture.Store.Approve(mirror, 0, mirror.Fingerprint), "missing journal cannot reset anti-rollback history");
        Check(new LocalCatalogSource(fixture.ContentRoot, "offline").Load().Components.Count == 1, "remote trust failure leaves local catalog usable");
        Console.WriteLine($"Remote component catalog trust checks passed: {count}");
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Transport : IComponentCatalogTransport
    {
        public Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public List<string> Requests = [];
        public async Task CopyAsync(ComponentCatalogSourcePolicy policy, string path, long maximumBytes, Stream destination, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(policy.SourceId + ":" + path);
            if (!Files.TryGetValue(path, out var bytes)) throw new IOException("Missing fixture resource");
            if (bytes.LongLength > maximumBytes) throw new IOException("Bounded response");
            await destination.WriteAsync(bytes, ct);
        }
    }
    internal sealed class Fixture : IDisposable
    {
        public string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "remote-catalog-" + Guid.NewGuid().ToString("N")));
        public string StateRoot => Path.Combine(Root, "trust");
        public string ContentRoot => Path.Combine(Root, "content");
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string KeyId = "test-key";
        public readonly Clock Clock = new();
        public readonly Transport Transport = new();
        public ComponentCatalogSourceStore Store;
        public byte[] Artifact = [1, 2, 3];
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(ContentRoot, "manifests")); Directory.CreateDirectory(Path.Combine(ContentRoot, "packages"));
            Store = new(StateRoot, "test-owner", Transport, Clock);
        }
        public void Reopen() { Store.Dispose(); Store = new(StateRoot, "test-owner", Transport, Clock); }
        public ComponentCatalogSourcePolicy Policy(string sourceId = "example-source") => new(1, sourceId, "Test publisher", "example", "user-trusted",
            "https://example.test/catalog/", "catalog.signed.json", new Dictionary<string, string> { [KeyId] = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()) },
            ["roapp"], [], 7 * 24 * 60 * 60, true);
        public void Publish(long sequence, string sourceId = "example-source", ECDsa? signingKey = null, string? keyId = null,
            string[]? revokedDigests = null, DateTimeOffset? issued = null)
        {
            var manifest = new ComponentManifest(1, "example.app", "example.app", "system", "Example", "Publisher supplied summary",
                "Application", "example.app", [], [], [], ["example.app"],
                [new("linux", ["x64"], new("roapp", true), "DesktopApp", false, new("catalog"),
                    new("catalog-artifact", Format: "roapp", Reference: "../packages/app.roapp", Sha256: RemoteCatalogRules.Hash(Artifact), Version: "1.0.0"))]);
            var index = new ComponentCatalogIndex(1, [new("example.app", "./manifests/app.json")], []);
            Transport.Files["catalog.json"] = JsonSerializer.SerializeToUtf8Bytes(index, ModulePackageJson.Options);
            Transport.Files["manifests/app.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, ModulePackageJson.Options);
            Transport.Files["packages/app.roapp"] = Artifact.ToArray();
            foreach (var path in new[] { "catalog.json", "manifests/app.json", "packages/app.roapp" })
                File.WriteAllBytes(Path.Combine(ContentRoot, path.Replace('/', Path.DirectorySeparatorChar)), Transport.Files[path]);
            var publication = new ComponentCatalogPublication(1, "component-catalog", sourceId, "example", sequence,
                issued ?? Clock.Now.AddMinutes(-1), Clock.Now.AddDays(7).AddMinutes(-1), "catalog.json",
                new[] { "catalog.json", "manifests/app.json", "packages/app.roapp" }
                    .Select(path => new ComponentCatalogResource(path, Transport.Files[path].Length, RemoteCatalogRules.Hash(Transport.Files[path]))).ToArray(), [], revokedDigests ?? []);
            Transport.Files["catalog.signed.json"] = ComponentCatalogPublisher.Sign(publication, keyId ?? KeyId, signingKey ?? Key);
        }
        public void Dispose()
        {
            Store.Dispose(); Key.Dispose();
            var parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp")) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new Exception("Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
}
