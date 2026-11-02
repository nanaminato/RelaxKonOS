using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

/// <summary>Offline publisher only. Private key material never enters source policy, cache, or package files.</summary>
public static class ComponentCatalogPublisher
{
    public static byte[] Sign(ComponentCatalogPublication publication, string keyId, ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw RemoteCatalogRules.Error("key_invalid");
        var payload = JsonSerializer.SerializeToUtf8Bytes(publication, ModulePackageJson.Options);
        if (payload.Length > RemoteCatalogRules.PayloadLimit) throw RemoteCatalogRules.Error("metadata_size");
        var context = Encoding.UTF8.GetBytes(RemoteCatalogRules.SignatureContext);
        var bytes = new byte[context.Length + payload.Length]; context.CopyTo(bytes, 0); payload.CopyTo(bytes, context.Length);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedComponentCatalog(1, keyId, Convert.ToBase64String(payload), Convert.ToBase64String(signature)), ModulePackageJson.Options);
    }

    public static async Task<byte[]> BuildAsync(string root, string sourceId, string publisherId, long sequence, string keyId,
        ECDsa key, DateTimeOffset issuedAt, DateTimeOffset expiresAt, string[] revokedReleaseIds, string[] revokedDigests, CancellationToken ct = default)
    {
        if (!RemoteCatalogRules.Id(sourceId) || !RemoteCatalogRules.Id(publisherId) || sequence <= 0
            || issuedAt.Offset != TimeSpan.Zero || expiresAt.Offset != TimeSpan.Zero || expiresAt <= issuedAt
            || (expiresAt - issuedAt).TotalSeconds > 2678400
            || revokedReleaseIds is not { Length: <= 10000 } || revokedDigests is not { Length: <= 10000 }
            || revokedReleaseIds.Any(id => !RemoteCatalogRules.Digest(id)) || revokedDigests.Any(digest => !RemoteCatalogRules.Digest(digest)))
            throw RemoteCatalogRules.Error("publication_invalid");
        var source = new LocalCatalogSource(root, sourceId);
        var catalog = source.Load();
        var resources = new Dictionary<string, ComponentCatalogResource>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        async Task Add(string reference, string declaring, bool json)
        {
            var path = RemoteCatalogRules.Resolve(reference, declaring);
            if (resources.ContainsKey(path)) return;
            await using var stream = await source.OpenAsync(reference, declaring, ct);
            if (stream.Length is <= 0 or > RemoteCatalogRules.ArtifactLimit || json && stream.Length > 262144)
                throw RemoteCatalogRules.Error("metadata_size");
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            resources.Add(path, new(path, stream.Length, digest));
            if (json)
            {
                stream.Position = 0; var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct);
                metadata[path] = bytes;
            }
        }
        await Add("catalog.json", "catalog.json", true);
        foreach (var component in catalog.Components)
        {
            var manifest = source.ManifestReference(component.Id);
            await Add(manifest, "catalog.json", true);
            foreach (var target in component.Targets)
                if (target.Artifact.Reference is { } reference) await Add(reference, manifest, false);
        }
        var publication = new ComponentCatalogPublication(1, "component-catalog", sourceId, publisherId, sequence,
            issuedAt, expiresAt, "catalog.json", resources.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), revokedReleaseIds, revokedDigests);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var policy = new ComponentCatalogSourcePolicy(1, sourceId, sourceId, publisherId,
            sourceId == "relaxkonos-official-components" ? "official" : "user-trusted", "https://catalog.invalid/", "catalog.signed.json",
            new Dictionary<string, string> { [keyId] = publicKey }, ["roapp", "apt", "archive", "download"], [], 2678400, true);
        policy.Validate(); RemoteCatalogRules.ValidateCatalog(policy, publication, catalog, metadata);
        if (resources.Count > 4096 || metadata.Sum(pair => (long)pair.Value.Length) > RemoteCatalogRules.MetadataLimit)
            throw RemoteCatalogRules.Error("metadata_size");
        return Sign(publication, keyId, key);
    }
}
