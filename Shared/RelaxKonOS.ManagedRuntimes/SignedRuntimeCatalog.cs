using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Signature covers the context prefix followed by the exact decoded payload bytes.</summary>
public sealed record SignedRuntimeCatalog(int SchemaVersion, string KeyId, string Payload, string Signature);
public sealed record RuntimeManifest(int SchemaVersion, string SourceId, long Revision,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, ManifestRelease[] Releases, string[] RevokedReleaseIds);
public sealed record ManifestRelease(string ReleaseId, string RuntimeId, string OriginalVersion,
    RuntimeChannel Channel, RuntimeTarget Target, RuntimeArtifact Artifact, string DownloadUri,
    DateTimeOffset? PublishedAt);

/// <summary>Every seen identity is retained, including releases removed from a later catalog.</summary>
public sealed record CatalogHistory(long HighWater, string? PayloadHash,
    Dictionary<string, string> IdentityHashes, Dictionary<string, string> ArtifactDigests,
    string[] ConflictedReleaseIds, string[] ConflictedVersions, string[] RevokedReleaseIds, string[] RevokedDigests)
{
    public static CatalogHistory Empty => new(0, null, new(StringComparer.Ordinal), new(StringComparer.Ordinal), [], [], [], []);
}
public sealed record VerifiedRuntimeCatalog(RuntimeCatalog Catalog, ManifestRelease[] Artifacts,
    CatalogHistory History, string PayloadHash);
public sealed record RuntimeConfigurationSupport(string SortVersion, string Capability, bool Supported);

public static class RuntimeCatalogVerifier
{
    // Domain separation also binds the format version. Signatures cannot be reused for source rotation.
    public static ReadOnlySpan<byte> SignatureContext => "RelaxKonOS.RuntimeCatalog.v1\n"u8;

    public static VerifiedRuntimeCatalog Verify(RuntimeSource source, ReadOnlyMemory<byte> envelopeBytes,
        CatalogHistory history, DateTimeOffset now, Func<ManifestRelease, RuntimeConfigurationSupport> support)
    {
        source.Validate();
        if (!source.Enabled) throw new RuntimePolicyException("runtime.source_disabled");
        if (envelopeBytes.Length > source.MaximumMetadataBytes)
            throw new RuntimePolicyException("runtime.metadata_too_large");
        var envelope = RuntimeJson.Read<SignedRuntimeCatalog>(envelopeBytes);
        if (envelope.SchemaVersion != 1 || envelope.KeyId != source.KeyId)
            throw new RuntimePolicyException("runtime.signature_invalid");
        byte[] payload, signature;
        try { payload = Convert.FromBase64String(envelope.Payload); signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { throw new RuntimePolicyException("runtime.signature_invalid"); }
        if (payload.Length > source.MaximumMetadataBytes || signature.Length != 64)
            throw new RuntimePolicyException("runtime.signature_invalid");
        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(source.PublicKeySpki), out _);
            var signed = new byte[SignatureContext.Length + payload.Length];
            SignatureContext.CopyTo(signed);
            payload.CopyTo(signed, SignatureContext.Length);
            if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new RuntimePolicyException("runtime.signature_invalid");
        }
        var manifest = RuntimeJson.Read<RuntimeManifest>(payload);
        if (manifest.SchemaVersion != 1 || manifest.SourceId != source.SourceId || manifest.Revision < 1
            || manifest.Releases.Length > 4096 || manifest.RevokedReleaseIds.Length > 4096
            || manifest.RevokedReleaseIds.Any(id => !RuntimeValidation.Identifier(id))
            || manifest.RevokedReleaseIds.Distinct().Count() != manifest.RevokedReleaseIds.Length)
            throw new RuntimePolicyException("runtime.metadata_invalid");
        if (manifest.IssuedAt > now.AddMinutes(5) || manifest.ExpiresAt <= manifest.IssuedAt
            || manifest.ExpiresAt - manifest.IssuedAt > TimeSpan.FromSeconds(source.MaximumValiditySeconds))
            throw new RuntimePolicyException("runtime.catalog_validity_invalid");
        if (manifest.ExpiresAt <= now) throw new RuntimePolicyException("runtime.catalog_expired");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        if (manifest.Revision < history.HighWater) throw new RuntimePolicyException("runtime.catalog_replayed");
        if (manifest.Revision == history.HighWater && hash != history.PayloadHash)
            throw new RuntimePolicyException("runtime.catalog_revision_conflict");

        var identities = new Dictionary<string, string>(history.IdentityHashes, StringComparer.Ordinal);
        var conflicts = new HashSet<string>(history.ConflictedReleaseIds, StringComparer.Ordinal);
        var conflictVersions = new HashSet<string>(history.ConflictedVersions, StringComparer.Ordinal);
        var revoked = new HashSet<string>(history.RevokedReleaseIds.Concat(manifest.RevokedReleaseIds), StringComparer.Ordinal);
        var digests = new Dictionary<string, string>(history.ArtifactDigests, StringComparer.Ordinal);
        var revokedDigests = new HashSet<string>(history.RevokedDigests, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var releases = new List<RuntimeRelease>();
        foreach (var release in manifest.Releases)
        {
            if (release is null || release.Target is null || release.Artifact is null)
                throw new RuntimePolicyException("runtime.release_invalid");
            ValidateRelease(source, release);
            var identity = "release:" + release.ReleaseId;
            var versionIdentity = "version:" + Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(new
                { release.RuntimeId, release.OriginalVersion, release.Target })));
            if (!ids.Add(release.ReleaseId) || !versions.Add(versionIdentity))
                throw new RuntimePolicyException("runtime.release_ambiguous");
            // A new release id cannot hide a changed artifact for an already-confirmed tag/target.
            var artifactHash = Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(new
                { release.RuntimeId, release.OriginalVersion, release.Channel, release.Target,
                    Artifact = release.Artifact with
                    {
                        Sha256 = release.Artifact.Sha256.ToUpperInvariant(), VerificationIdentity = "",
                        Layout = release.Artifact.Layout with { Files = release.Artifact.Layout.Files.OrderBy(f => f.Path, StringComparer.Ordinal)
                            .Select(f => f with { Sha256 = f.Sha256.ToUpperInvariant() }).ToArray() }
                    } })));
            foreach (var name in new[] { identity, versionIdentity })
            {
                if (identities.TryGetValue(name, out var previous) && previous != artifactHash)
                {
                    conflicts.Add(release.ReleaseId);
                    conflictVersions.Add(versionIdentity);
                    // Retain the immutable original fingerprint; never silently accept a replacement.
                    foreach (var old in history.IdentityHashes.Where(p => p.Key.StartsWith("release:", StringComparison.Ordinal)
                        && p.Value == previous)) conflicts.Add(old.Key[8..]);
                }
                else identities[name] = artifactHash;
            }
            if (conflictVersions.Contains(versionIdentity)) conflicts.Add(release.ReleaseId);
            digests.TryAdd(release.ReleaseId, release.Artifact.Sha256.ToUpperInvariant());
            var capability = support(release);
            releases.Add(new(release.ReleaseId, release.RuntimeId, release.OriginalVersion,
                capability.SortVersion, release.Channel, source.SourceId, release.Target, release.Artifact,
                capability.Capability, capability.Supported,
                revoked.Contains(release.ReleaseId) ? RuntimeTrust.Revoked
                    : conflicts.Contains(release.ReleaseId) ? RuntimeTrust.Conflict : RuntimeTrust.Confirmed,
                RuntimeEvidence.Unverified, release.PublishedAt));
        }
        // A conflict discovered later in the same catalog also blocks an earlier release entry.
        foreach (var id in revoked)
            if (digests.TryGetValue(id, out var digest)) revokedDigests.Add(digest);
        var result = releases.Select(r => revoked.Contains(r.ReleaseId) || revokedDigests.Contains(r.Artifact.Sha256)
            ? r with { Trust = RuntimeTrust.Revoked } : conflicts.Contains(r.ReleaseId)
                ? r with { Trust = RuntimeTrust.Conflict } : r).ToArray();
        return new(new(source.SourceId, manifest.Revision, source.TrustRevision, manifest.ExpiresAt, true, result),
            manifest.Releases, new(manifest.Revision, hash, identities, digests, [.. conflicts.Order()],
                [.. conflictVersions.Order()], [.. revoked.Order()], [.. revokedDigests.Order()]), hash);
    }

    private static void ValidateRelease(RuntimeSource source, ManifestRelease release)
    {
        var artifact = release.Artifact;
        var target = release.Target;
        if (!RuntimeValidation.Identifier(release.ReleaseId) || release.RuntimeId != source.RuntimeId
            || string.IsNullOrWhiteSpace(release.OriginalVersion) || release.OriginalVersion.Length > 128
            || !Enum.IsDefined(release.Channel) || !source.Channels.Contains(release.Channel)
            || !RuntimeValidation.Identifier(target.Os) || !RuntimeValidation.Identifier(target.Architecture)
            || !RuntimeValidation.Identifier(target.ServiceHost) || !Enum.IsDefined(target.InstallMode)
            || target.Distribution is not null && !RuntimeValidation.Identifier(target.Distribution)
            || target.Libc is not null && !RuntimeValidation.Identifier(target.Libc)
            || !RuntimeValidation.Identifier(artifact.ArtifactId) || artifact.Sha256.Length != 64
            || !artifact.Sha256.All(Uri.IsHexDigit) || artifact.MaximumBytes is <= 0 or > 2L * 1024 * 1024 * 1024
            || artifact.Format is null || !RuntimeArchiveRules.Formats.Contains(artifact.Format)
            || target.InstallMode != RuntimeInstallMode.Archive
            || !RuntimeValidation.RelativePath(artifact.EntryPoint)
            || !RuntimeArchiveRules.Valid(artifact)
            || artifact.VerificationIdentity != source.KeyId
            || !RuntimeValidation.Https(release.DownloadUri, out var download) || !source.AllowsDownload(download))
            throw new RuntimePolicyException("runtime.release_invalid");
    }
}
