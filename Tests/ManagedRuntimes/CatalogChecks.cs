using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class CatalogChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject(string code, Action action)
        {
            try { action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected " + code);
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
        var source = new RuntimeSource("official", "sing-box", "publisher", RuntimeSourceKind.HttpsManifest, "https://releases.example.com/catalog/",
            "manifest.json", "key-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), true, 1,
            [RuntimeChannel.Stable], 1024 * 1024, 86400,
            ["https://releases.example.com/artifacts/"], [], []);
        var target = new RuntimeTarget("linux", "arm64", null, "glibc", RuntimeInstallMode.Archive, "systemd");
        var artifact = new RuntimeArtifact("artifact-1", new string('a', 64), 1024, "tar.gz", "bin/sing-box", "key-1",
            new(1024, 8, [new("bin/sing-box", 4, new string('b', 64), true)]));
        var release = new ManifestRelease("release-1", "sing-box", "v1.14.3", RuntimeChannel.Stable, target,
            artifact, "https://releases.example.com/artifacts/sing-box.tar.gz", now);
        var manifest = new RuntimeManifest(1, source.SourceId, 1, now, now.AddHours(1), [release], []);
        byte[] SignPayload(byte[] payload)
        {
            var signed = RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(payload).ToArray();
            return RuntimeJson.Serialize(new SignedRuntimeCatalog(1, source.KeyId, Convert.ToBase64String(payload),
                Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        }
        byte[] Sign(RuntimeManifest value) => SignPayload(RuntimeJson.Serialize(value));
        VerifiedRuntimeCatalog Verify(RuntimeManifest? value = null, CatalogHistory? history = null, RuntimeSource? policy = null) =>
            RuntimeCatalogVerifier.Verify(policy ?? source, Sign(value ?? manifest), history ?? CatalogHistory.Empty, now,
                _ => new("1.14.3", "host-generator", false));

        source.Validate();
        var missingLayout = JsonNode.Parse(RuntimeJson.Serialize(artifact))!.AsObject();
        missingLayout.Remove("layout");
        Reject("metadata_invalid", () => RuntimeJson.Read<RuntimeArtifact>(Encoding.UTF8.GetBytes(missingLayout.ToJsonString())));
        var first = Verify();
        Check(first.Catalog.SignatureVerified && first.History.HighWater == 1, "Valid exact-byte signature");
        Check(!first.Catalog.Releases.Single().ConfigurationSupported, "Publisher cannot grant configuration compatibility");
        Check(first.Catalog.Releases.Single().Evidence == RuntimeEvidence.Unverified, "No inferred runtime evidence");
        Check(Verify(history: first.History).PayloadHash == first.PayloadHash, "Same revision and bytes may refresh");
        Check(Verify(manifest with { Revision = 2, Releases = [release with { Artifact = artifact with { Sha256 = artifact.Sha256.ToUpperInvariant() } }] },
            first.History).Catalog.Releases.Single().Trust == RuntimeTrust.Confirmed, "Digest casing is not a changed artifact");
        Reject("catalog_replayed", () => Verify(history: first.History with { HighWater = 2 }));
        Reject("catalog_revision_conflict", () => Verify(manifest with { ExpiresAt = now.AddHours(2) }, first.History));
        Reject("catalog_expired", () => Verify(manifest with { IssuedAt = now.AddHours(-2), ExpiresAt = now }));
        Reject("catalog_validity_invalid", () => Verify(manifest with { IssuedAt = now.AddMinutes(6) }));
        Reject("catalog_validity_invalid", () => Verify(manifest with { ExpiresAt = now.AddDays(2) }));
        Reject("source_disabled", () => Verify(policy: source with { Enabled = false }));
        Reject("release_ambiguous", () => Verify(manifest with { Releases = [release, release] }));
        Reject("release_invalid", () => Verify(manifest with { Releases = [null!] }));
        Reject("release_invalid", () => Verify(manifest with { Releases = [release with { RuntimeId = "another-runtime" }] }));
        Reject("release_invalid", () => Verify(manifest with { Releases = [release with { DownloadUri = "https://evil.example.com/artifacts/a.zip" }] }));
        foreach (var entry in new[] { "../bin", "/bin", "a//b", "a/./b", "a\\b", "a:stream", "a/%2e%2e/b" })
            Reject("release_invalid", () => Verify(manifest with { Releases = [release with { Artifact = artifact with { EntryPoint = entry } }] }));
        using (var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            Reject("signature_invalid", () => Verify(policy: source with { PublicKeySpki = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()) }));
        var envelope = RuntimeJson.Read<SignedRuntimeCatalog>(Sign(manifest));
        var tampered = RuntimeJson.Serialize(envelope with { Payload = Convert.ToBase64String(RuntimeJson.Serialize(manifest with { Revision = 9 })) });
        Reject("signature_invalid", () => RuntimeCatalogVerifier.Verify(source, tampered, CatalogHistory.Empty, now, _ => new("", "", false)));
        var json = Encoding.UTF8.GetString(RuntimeJson.Serialize(manifest));
        var duplicate = Encoding.UTF8.GetBytes(json.Replace("\"revision\":1", "\"revision\":1,\"revision\":2", StringComparison.Ordinal));
        Reject("metadata_invalid", () => RuntimeCatalogVerifier.Verify(source, SignPayload(duplicate), CatalogHistory.Empty, now, _ => new("", "", false)));
        Reject("metadata_too_large", () => RuntimeCatalogVerifier.Verify(source with { MaximumMetadataBytes = 1024 },
            new byte[1025], CatalogHistory.Empty, now, _ => new("", "", false)));
        var changed = release with { ReleaseId = "replacement-id", Artifact = artifact with { Sha256 = new string('b', 64) } };
        var conflict = Verify(manifest with { Revision = 2, Releases = [changed] }, first.History);
        Check(conflict.Catalog.Releases.Single().Trust == RuntimeTrust.Conflict, "Changed tag digest quarantined even with new release id");
        Check(conflict.History.IdentityHashes["release:release-1"] == first.History.IdentityHashes["release:release-1"], "Original immutable identity retained");
        var alias = release with { ReleaseId = "restored-alias" };
        Check(Verify(manifest with { Revision = 3, Releases = [alias] }, conflict.History).Catalog.Releases.Single().Trust == RuntimeTrust.Conflict,
            "Quarantined tag stays blocked through aliases");
        var revoked = Verify(manifest with { Revision = 2, Releases = [], RevokedReleaseIds = [release.ReleaseId] }, first.History);
        Check(Verify(manifest with { Revision = 3, Releases = [alias] }, revoked.History).Catalog.Releases.Single().Trust == RuntimeTrust.Revoked,
            "Revoked artifact stays blocked under a new id");

        foreach (var ip in new[] { "127.0.0.1", "::1", "169.254.169.254", "168.63.129.16", "100.100.100.200", "10.1.2.3",
            "::ffff:127.0.0.1", "fc00::1", "2001:db8::1", "64:ff9b::a9fe:a9fe", "224.0.0.1", "0.0.0.0", "198.18.0.1" })
            Check(!RuntimeNetworkPolicy.AddressAllowed(IPAddress.Parse(ip), 443, []), "Forbidden address " + ip);
        var grant = new RuntimePrivateNetwork("10.20.0.0/16", 8443);
        RuntimeNetworkPolicy.ValidateGrant(grant);
        Check(RuntimeNetworkPolicy.AddressAllowed(IPAddress.Parse("10.20.1.2"), 8443, [grant]), "Exact private mirror network and port");
        Check(!RuntimeNetworkPolicy.AddressAllowed(IPAddress.Parse("10.20.1.2"), 443, [grant]), "Private port constrained");
        Check(!RuntimeNetworkPolicy.AddressAllowed(IPAddress.Parse("10.21.1.2"), 8443, [grant]), "Private network constrained");
        Reject("private_network_invalid", () => RuntimeNetworkPolicy.ValidateGrant(new("0.0.0.0/0", 443)));
        Reject("private_network_invalid", () => RuntimeNetworkPolicy.ValidateGrant(new("127.0.0.1/32", 443)));
        Reject("address_forbidden", () => RuntimeNetworkPolicy.RequireAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Loopback], 443, []));
        Check(RuntimeNetworkPolicy.AddressAllowed(IPAddress.Parse("8.8.8.8"), 443, []), "Public IPv4");
        Check(RuntimeNetworkPolicy.AddressAllowed(IPAddress.Parse("2606:4700:4700::1111"), 443, []), "Public IPv6");
        Check(!RuntimeValidation.Within("https://example.com/releases/", new("https://example.com/releases-evil/file")), "Prefix boundary");
        Check(!RuntimeValidation.Within("https://example.com/releases/", new("https://example.com:444/releases/file")), "Exact port");
        Check(!RuntimeValidation.Within("https://example.com/releases/", new("https://example.com/releases/%2e%2e/file")), "Encoded path rejected");

        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuntimeSourceStore(root, path => Directory.CreateDirectory(path));
            Reject("source_confirmation_required", () => store.Confirm(source, 0, "bad", "administrator", now));
            var accepted = store.Confirm(source, 0, source.ConfirmationFingerprint, "administrator", now);
            Check(accepted.Source.TrustRevision == 1 && accepted.Audit.Single().Actor == "administrator", "Trust confirmed and audited");
            Check(store.List().Single().Source.SourceId == source.SourceId, "Stored sources enumerate");
            using (var held = new FileStream(Path.Combine(root, "registry.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject("source_store_busy", () => store.Read(source.SourceId));
            Reject("trust_revision_conflict", () => store.Confirm(source, 0, source.ConfirmationFingerprint, "administrator", now));
            var saved = store.Accept(source.SourceId, 1, Sign(manifest), now, _ => new("1.14.3", "adapter", false));
            var restarted = new RuntimeSourceStore(root, path => Directory.CreateDirectory(path));
            Check(restarted.Read(source.SourceId)!.History.HighWater == 1, "High water survives restart");
            var disabled = source with { Enabled = false };
            restarted.Confirm(disabled, 1, disabled.ConfirmationFingerprint, "administrator", now);
            Check(restarted.Read(source.SourceId)!.History.HighWater == 1, "Trust change preserves high water");
            Reject("trust_revision_conflict", () => restarted.Accept(source.SourceId, 1, Sign(manifest), now, _ => new("", "", false)));
            Reject("source_disabled", () => restarted.Accept(source.SourceId, 2, Sign(manifest), now, _ => new("", "", false)));
            var enabled = source with { TrustRevision = 2 };
            restarted.Confirm(enabled, 2, enabled.ConfirmationFingerprint, "administrator", now);
            var next = manifest with { Revision = 2 };
            restarted.Accept(source.SourceId, 3, Sign(next), now, _ => new("1.14.3", "adapter", false));
            Reject("catalog_replayed", () => restarted.Accept(source.SourceId, 3, Sign(manifest), now, _ => new("", "", false)));
            Check(restarted.Read(source.SourceId)!.Cached!.Catalog.Revision == 2, "Rejected replay cannot overwrite cache");
            var wrongPublisher = source with { Publisher = "new publisher" };
            Reject("source_identity_immutable", () => restarted.Confirm(wrongPublisher, 3, wrongPublisher.ConfirmationFingerprint, "administrator", now));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        using var rotated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        key.ImportParameters(rotated.ExportParameters(true));
        source = source with { KeyId = "key-2", PublicKeySpki = Convert.ToBase64String(rotated.ExportSubjectPublicKeyInfo()), TrustRevision = 2 };
        var rotatedRelease = release with { Artifact = artifact with { VerificationIdentity = "key-2" } };
        Check(Verify(manifest with { Revision = 2, Releases = [rotatedRelease] }, first.History).Catalog.Releases.Single().Trust == RuntimeTrust.Confirmed,
            "Administrator-confirmed key rotation preserves unchanged artifact identity");
        return count;
    }
}
