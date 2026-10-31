using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static RelaxKonOS.ModulePackaging.ModulePackageRules;

namespace RelaxKonOS.ModulePackaging;

/// <summary>Pure verification: never downloads, extracts, executes, or writes trust state.</summary>
public static class ModulePackageVerifier
{
    public static void ValidateManifest(ModulePackageManifest manifest) => Manifest(manifest);

    private const int MetadataLimit = 4 * 1024 * 1024;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string ObjectHash<T>(T value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value, ModulePackageJson.Options));

    public static VerifiedModulePackageCatalog VerifyCatalog(ReadOnlyMemory<byte> envelopeBytes,
        ModulePackageTrustPolicy policy, ModulePackageContext context, ModulePackageTrustHistory history, DateTimeOffset now)
    {
        Require(policy.SchemaVersion == 1 && policy.Enabled && Id(policy.SourceId) && policy.Revision > 0
            && Id(policy.PublisherId) && policy.Purpose is "official" or "development" or "third-party"
            && policy.MaximumValiditySeconds is > 0 and <= 2678400, "source-invalid");
        Require(!string.IsNullOrWhiteSpace(context.ServiceId) && context.ServiceId.Length <= 256, "service-identity-invalid");
        _ = Version(context.HostApiVersion);
        Require(policy.Purpose != "development" ? policy.DevelopmentServiceId is null
            : context.DevelopmentInstance && policy.DevelopmentServiceId == context.ServiceId, "development-source-forbidden");
        Require(Uri.TryCreate(policy.ArtifactOrigin, UriKind.Absolute, out var origin) && origin.Scheme == "https"
            && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.AbsolutePath == "/" && origin.AbsoluteUri == policy.ArtifactOrigin, "origin-invalid");
        Require(policy.AuthorizedModuleIds is { Length: > 0 and <= 128 } && policy.AuthorizedModuleIds.All(Id)
            && policy.AuthorizedModuleIds.Distinct(StringComparer.Ordinal).Count() == policy.AuthorizedModuleIds.Length
            && policy.PublicKeysSpki is { Count: > 0 and <= 32 }, "source-authorization-invalid");
        Require(history.SourceId == policy.SourceId && history.ServiceId == context.ServiceId && history.Purpose == policy.Purpose
            && history.PolicyRevision >= 0 && history.Sequence >= 0, "history-identity-invalid");
        Require(history.KeyHashes is not null && history.RetiredKeyIds is not null && history.ReleaseIdentities is not null
            && history.VersionIdentities is not null && history.ReleaseDigests is not null
            && history.RevokedReleaseIds is not null && history.RevokedDigests is not null, "history-invalid");
        Require(history.KeyHashes.Count <= 256 && history.RetiredKeyIds.Length <= 256
            && history.ReleaseIdentities.Count <= 10000 && history.VersionIdentities.Count <= 10000
            && history.ReleaseDigests.Count <= 10000 && history.RevokedReleaseIds.Length <= 10000
            && history.RevokedDigests.Length <= 10000, "history-capacity");
        Require(history.KeyHashes.All(p => Id(p.Key) && Digest(p.Value))
            && history.RetiredKeyIds.All(k => Id(k) && history.KeyHashes.ContainsKey(k))
            && history.RetiredKeyIds.Distinct(StringComparer.Ordinal).Count() == history.RetiredKeyIds.Length
            && history.ReleaseIdentities.All(p => Id(p.Key) && Digest(p.Value))
            && history.ReleaseDigests.All(p => Id(p.Key) && Digest(p.Value))
            && history.ReleaseIdentities.Keys.Order(StringComparer.Ordinal).SequenceEqual(history.ReleaseDigests.Keys.Order(StringComparer.Ordinal))
            && history.VersionIdentities.All(p => p.Key.Length <= 256 && Digest(p.Value))
            && history.RevokedReleaseIds.All(Id) && history.RevokedDigests.All(Digest)
            && (history.PolicyRevision == 0 ? history.PolicyHash is null && history.KeyHashes.Count == 0 && history.Sequence == 0 : Digest(history.PolicyHash))
            && (history.Sequence == 0 ? history.PayloadHash is null && history.ReleaseIdentities.Count == 0
                && history.VersionIdentities.Count == 0 && history.RevokedReleaseIds.Length == 0 && history.RevokedDigests.Length == 0
                : Digest(history.PayloadHash) && history.PolicyRevision > 0 && history.KeyHashes.Count > 0), "history-invalid");
        var policyHash = ObjectHash(policy with {
            AuthorizedModuleIds = policy.AuthorizedModuleIds.Order(StringComparer.Ordinal).ToArray(),
            PublicKeysSpki = new SortedDictionary<string, string>(policy.PublicKeysSpki.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
        });
        Require(policy.Revision >= history.PolicyRevision && (policy.Revision != history.PolicyRevision || policyHash == history.PolicyHash), "policy-replay");
        var keys = new Dictionary<string, string>(history.KeyHashes, StringComparer.Ordinal);
        var retired = new HashSet<string>(history.RetiredKeyIds, StringComparer.Ordinal);
        foreach (var previous in keys.Keys)
            if (!policy.PublicKeysSpki.ContainsKey(previous)) retired.Add(previous);
        var envelope = ModulePackageJson.Read<SignedModulePackageCatalog>(envelopeBytes, 6 * 1024 * 1024);
        Require(envelope.SchemaVersion == 1 && Id(envelope.KeyId), "signature-envelope-invalid");
        byte[] payload;
        byte[] signature;
        try
        {
            Require(envelope.Payload is { Length: > 0 and <= 5592408 } && envelope.Signature is { Length: 88 }, "signature-envelope-invalid");
            payload = Convert.FromBase64String(envelope.Payload!);
            signature = Convert.FromBase64String(envelope.Signature!);
        }
        catch (FormatException) { throw new ModulePackageException("module-package-signature-envelope-invalid"); }
        Require(payload.Length <= MetadataLimit && signature.Length == 64, "signature-envelope-invalid");
        bool verified = false;
        foreach (var key in policy.PublicKeysSpki)
        {
            Require(Id(key.Key) && !retired.Contains(key.Key) && key.Value is { Length: > 0 and <= 512 }, "key-invalid");
            try
            {
                var spki = Convert.FromBase64String(key.Value);
                using var crypto = ECDsa.Create();
                crypto.ImportSubjectPublicKeyInfo(spki, out var consumed);
                Require(consumed == spki.Length && crypto.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7", "key-invalid");
                var keyHash = Hash(spki);
                Require(!keys.TryGetValue(key.Key, out var oldHash) || oldHash == keyHash, "key-id-reused");
                keys[key.Key] = keyHash;
                if (key.Key == envelope.KeyId)
                {
                    var domain = Encoding.UTF8.GetBytes("RelaxKonOS.ServerModuleCatalog.v1\n");
                    var signed = new byte[domain.Length + payload.Length];
                    domain.CopyTo(signed, 0); payload.CopyTo(signed, domain.Length);
                    verified = crypto.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
            }
            catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
            { throw new ModulePackageException("module-package-key-invalid"); }
        }
        Require(verified, "signature-invalid");
        var catalog = ModulePackageJson.Read<ModulePackageCatalog>(payload);
        var payloadHash = Hash(payload);
        Require(catalog.SchemaVersion == 1 && catalog.SourceId == policy.SourceId && catalog.Sequence > 0
            && catalog.Sequence >= history.Sequence && (catalog.Sequence != history.Sequence || payloadHash == history.PayloadHash), "catalog-replay");
        Require(catalog.IssuedAt.Offset == TimeSpan.Zero && catalog.ExpiresAt.Offset == TimeSpan.Zero
            && catalog.IssuedAt <= now && catalog.ExpiresAt > now && catalog.ExpiresAt > catalog.IssuedAt
            && (catalog.ExpiresAt - catalog.IssuedAt).TotalSeconds <= policy.MaximumValiditySeconds, "catalog-expired");
        Require(catalog.Releases is { Length: <= 2048 } && catalog.RevokedReleaseIds is { Length: <= 10000 }
            && catalog.RevokedDigests is { Length: <= 10000 }, "catalog-limits");
        var revokedIds = new HashSet<string>(history.RevokedReleaseIds, StringComparer.Ordinal);
        var revokedDigests = new HashSet<string>(history.RevokedDigests, StringComparer.Ordinal);
        Require(catalog.RevokedReleaseIds!.All(Id) && catalog.RevokedDigests!.All(Digest), "revocation-invalid");
        revokedIds.UnionWith(catalog.RevokedReleaseIds); revokedDigests.UnionWith(catalog.RevokedDigests);
        var releases = new Dictionary<string, string>(history.ReleaseIdentities, StringComparer.Ordinal);
        var versions = new Dictionary<string, string>(history.VersionIdentities, StringComparer.Ordinal);
        var digests = new Dictionary<string, string>(history.ReleaseDigests, StringComparer.Ordinal);
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in catalog.Releases!)
        {
            Require(release?.Manifest is not null, "release-invalid");
            var m = release!.Manifest;
            Manifest(m);
            Require(currentIds.Add(m.ReleaseId) && m.PublisherId == policy.PublisherId && m.Purpose == policy.Purpose
                && policy.AuthorizedModuleIds.Contains(m.ModuleId, StringComparer.Ordinal), "release-unauthorized");
            Require(Digest(release.ManifestSha256) && Digest(release.ArchiveSha256)
                && release.ArchiveBytes is > 0 and <= 1077936128, "archive-metadata-invalid");
            Require(Uri.TryCreate(release.DownloadUri, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && uri.GetLeftPart(UriPartial.Authority) == origin!.GetLeftPart(UriPartial.Authority)
                && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0, "download-origin-invalid");
            var identity = ObjectHash(release);
            var versionId = m.ModuleId + "/" + m.Version + "/" + m.Rid;
            Require(!releases.TryGetValue(m.ReleaseId, out var prior) || prior == identity, "release-equivocation");
            Require(!versions.TryGetValue(versionId, out var priorVersion) || priorVersion == identity, "version-equivocation");
            releases[m.ReleaseId] = identity; versions[versionId] = identity; digests[m.ReleaseId] = release.ArchiveSha256;
        }
        Require(releases.Count <= 10000 && versions.Count <= 10000 && digests.Count <= 10000
            && keys.Count <= 256 && retired.Count <= 256 && revokedIds.Count <= 10000 && revokedDigests.Count <= 10000, "history-capacity");
        return new(catalog, new(policy.SourceId, context.ServiceId, policy.Purpose, policy.Revision, policyHash, keys,
            retired.Order(StringComparer.Ordinal).ToArray(), catalog.Sequence, payloadHash, releases, versions, digests,
            revokedIds.Order(StringComparer.Ordinal).ToArray(), revokedDigests.Order(StringComparer.Ordinal).ToArray()), payloadHash);
    }

    public static async Task<VerifiedModulePackageCandidate> VerifyCandidateAsync(Stream archive,
        ReadOnlyMemory<byte> envelope, ModulePackageTrustPolicy policy, ModulePackageContext context,
        ModulePackageTrustHistory history, string releaseId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var verified = VerifyCatalog(envelope, policy, context, history, now);
        var release = verified.Catalog.Releases.SingleOrDefault(r => r.Manifest.ReleaseId == releaseId);
        Require(release is not null, "release-not-found");
        var m = release!.Manifest;
        Require(!verified.NextHistory.RevokedReleaseIds.Contains(releaseId, StringComparer.Ordinal)
            && !verified.NextHistory.RevokedDigests.Contains(release.ArchiveSha256, StringComparer.Ordinal), "release-revoked");
        Require(m.Rid == context.Rid, "rid-mismatch");
        var hostVersion = Version(release.Manifest.Runtime == "module-host" ? "2.0.0" : context.HostApiVersion);
        Require(hostVersion >= Version(m.HostApiMinimum) && hostVersion < Version(m.HostApiMaximumExclusive), "host-api-incompatible");
        Require(archive.CanRead && archive.CanSeek && archive.Length == release.ArchiveBytes, "archive-size");
        archive.Position = 0;
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, cancellationToken));
        Require(digest == release.ArchiveSha256, "archive-digest");
        archive.Position = 0;
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            Require(zip.Entries.Count == m.Files.Length + 1, "archive-file-set");
            var expected = m.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                Require(ModulePackageRules.Path(entry.FullName) && names.Add(entry.FullName)
                    && unixType is 0 or 0x8000 && (entry.ExternalAttributes & 0x410) == 0, "archive-entry-invalid");
                if (entry.FullName == "manifest.json")
                {
                    Require(entry.Length is > 0 and <= MetadataLimit, "manifest-size");
                    var bytes = await ReadEntryAsync(entry, entry.Length, cancellationToken);
                    Require(Hash(bytes) == release.ManifestSha256, "manifest-digest");
                    var actual = ModulePackageJson.Read<ModulePackageManifest>(bytes);
                    Manifest(actual);
                    Require(ObjectHash(actual) == ObjectHash(m), "manifest-mismatch");
                }
                else
                {
                    Require(expected.TryGetValue(entry.FullName, out var file) && entry.Length == file.Length, "archive-file-set");
                    using var stream = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536]; long length = 0; int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
                    { length += count; Require(length <= file!.Length, "archive-expanded-size"); hash.AppendData(buffer, 0, count); }
                    Require(length == file!.Length && Convert.ToHexStringLower(hash.GetHashAndReset()) == file.Sha256, "file-digest");
                }
            }
            Require(names.Contains("manifest.json") && expected.Keys.All(names.Contains), "archive-file-set");
        }
        catch (InvalidDataException) { throw new ModulePackageException("module-package-archive-invalid"); }
        return new(m, digest, archive.Length, policy.SourceId, verified.Catalog.Sequence,
            ModulePackageJson.Read<SignedModulePackageCatalog>(envelope, 6 * 1024 * 1024).KeyId, true);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, long limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using var input = entry.Open();
        var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        { Require(output.Length + count <= limit, "archive-expanded-size"); output.Write(buffer, 0, count); }
        Require(output.Length == limit, "archive-expanded-size");
        return output.ToArray();
    }
}
