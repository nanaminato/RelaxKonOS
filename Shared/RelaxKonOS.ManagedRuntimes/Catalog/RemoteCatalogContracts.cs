using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

public sealed record ComponentCatalogSourcePolicy(int SchemaVersion, string SourceId, string DisplayName, string PublisherId,
    string Trust, string BaseUri, string CatalogPath, IReadOnlyDictionary<string, string> PublicKeysSpki,
    string[] InstallerProviders, RuntimePrivateNetwork[] PrivateNetworks, int MaximumValiditySeconds, bool Enabled)
{
    [JsonIgnore]
    public string Fingerprint => RemoteCatalogRules.Hash(JsonSerializer.SerializeToUtf8Bytes(this with
    {
        PublicKeysSpki = new SortedDictionary<string, string>(PublicKeysSpki.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal),
        InstallerProviders = InstallerProviders.Order(StringComparer.Ordinal).ToArray(),
        PrivateNetworks = PrivateNetworks.OrderBy(network => network.Cidr, StringComparer.Ordinal).ThenBy(network => network.Port).ToArray()
    }, ModulePackageJson.Options));

    [JsonIgnore]
    public IReadOnlyList<string> KeyFingerprints => PublicKeysSpki.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key + ": SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(pair.Value))).TrimEnd('=')).ToArray();

    public void Validate()
    {
        if (SchemaVersion != 1 || !RemoteCatalogRules.Id(SourceId) || SourceId == "relaxkonos-bundled"
            || !RemoteCatalogRules.Id(PublisherId) || DisplayName is not { Length: > 0 and <= 128 } || DisplayName.Any(char.IsControl)
            || Trust is not ("official" or "user-trusted") || !RuntimeValidation.Https(BaseUri, out var uri)
            || uri.AbsoluteUri != BaseUri || !BaseUri.EndsWith('/') || !RemoteCatalogRules.Path(CatalogPath)
            || PublicKeysSpki is not { Count: > 0 and <= 32 } || InstallerProviders is not { Length: > 0 and <= 4 }
            || InstallerProviders.Any(value => value is not ("roapp" or "apt" or "archive" or "download"))
            || InstallerProviders.Distinct(StringComparer.Ordinal).Count() != InstallerProviders.Length
            || PrivateNetworks is not { Length: <= 16 } || MaximumValiditySeconds is <= 0 or > 2678400)
            throw RemoteCatalogRules.Error("source_invalid");
        foreach (var grant in PrivateNetworks) RuntimeNetworkPolicy.ValidateGrant(grant);
        try { PackageCatalogSignature.CheckKeys(PublicKeysSpki, new Dictionary<string, string>(), []); }
        catch (PackageCatalogVerificationException) { throw RemoteCatalogRules.Error("key_invalid"); }
        if (Trust == "official")
        {
            if (SourceId != "relaxkonos-official-components" || PublisherId != "relaxkon" || PublicKeysSpki.Count != 1
                || !PublicKeysSpki.TryGetValue(OfficialRuntimeCatalog.KeyId, out var key) || key != OfficialRuntimeCatalog.PublicKeySpki)
                throw RemoteCatalogRules.Error("official_identity_invalid");
        }
        else if (PublisherId is "relaxkon" or "relaxkonos" || SourceId.StartsWith("relaxkonos-", StringComparison.Ordinal))
            throw RemoteCatalogRules.Error("official_identity_invalid");
    }
}

public sealed record SignedComponentCatalog(int SchemaVersion, string KeyId, string Payload, string Signature);
public sealed record ComponentCatalogResource(string Path, long Bytes, string Sha256);
public sealed record ComponentCatalogPublication(int SchemaVersion, string Kind, string SourceId, string PublisherId,
    long Sequence, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string Catalog,
    ComponentCatalogResource[] Resources, string[] RevokedReleaseIds, string[] RevokedDigests);
public sealed record ComponentCatalogInstallAuthorization(string SourceId, string PublisherId, long PolicyRevision,
    string PolicyFingerprint, long CatalogSequence, string ReleaseIdentity, string ArtifactSha256, string StoreInstanceId);
public sealed record ComponentCatalogSourceView(ComponentCatalogSourcePolicy Policy, long PolicyRevision, long Sequence,
    DateTimeOffset? ExpiresAt, string Status, string? ProblemCode);
public sealed record ComponentCatalogSourceSnapshot(ComponentCatalogSourcePolicy Policy, long PolicyRevision,
    ComponentCatalogPublication Publication, ResolvedComponentCatalog Catalog, IReadOnlyDictionary<string, byte[]> Metadata);
public interface IAuthenticatedComponentCatalogSource : IComponentCatalogSource
{
    ComponentCatalogInstallAuthorization Authorize(string componentId, ComponentHostEnvironment host);
}
public interface IComponentCatalogInstallLease : IDisposable { void Revalidate(); }

public static class RemoteCatalogRules
{
    public const int PayloadLimit = 4 * 1024 * 1024;
    public const int EnvelopeLimit = 6 * 1024 * 1024;
    public const int MetadataLimit = 8 * 1024 * 1024;
    public const long ArtifactLimit = 128L * 1024 * 1024;
    public const string SignatureContext = "RelaxKonOS.PackageCatalog.v1\n";
    public static RuntimePolicyException Error(string code) => new("component.catalog_" + code);
    public static bool Id(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-');
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static bool Path(string? value) => value is { Length: > 0 and <= 512 }
        && value.Split('/').All(part => part.Length is > 0 and <= 128 && part is not ("." or "..") && !part.EndsWith('.')
            && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));

    public static string Resolve(string reference, string declaringFile = "catalog.json")
    {
        if (string.IsNullOrEmpty(reference) || reference.StartsWith('/') || reference.Length > 512)
            throw Error("reference_invalid");
        var declaring = declaringFile == "catalog.json" ? declaringFile : Resolve(declaringFile);
        var parts = declaring.Split('/').SkipLast(1).ToList();
        foreach (var part in reference.Split('/'))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw Error("reference_invalid");
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                if (!Path(part)) throw Error("reference_invalid");
                parts.Add(part);
            }
        }
        var result = string.Join('/', parts);
        if (!Path(result)) throw Error("reference_invalid");
        return result;
    }

    public static string ReleaseIdentity(ComponentManifest component, ComponentInstallTarget target, string architecture) =>
        Hash(Encoding.UTF8.GetBytes(component.Id + "\n" + target.Artifact.Version + "\n" + target.Os + "\n" + architecture));

    public static void ValidateCatalog(ComponentCatalogSourcePolicy policy, ComponentCatalogPublication publication,
        ResolvedComponentCatalog catalog, IReadOnlyDictionary<string, byte[]> metadata)
    {
        if (catalog.Workloads.Count != 0) throw Error("workloads_unauthorized");
        foreach (var component in catalog.Components)
        {
            if (!component.Id.StartsWith(policy.PublisherId + ".", StringComparison.Ordinal)
                || component.RuntimeId != component.Id || component.ApplicationId != component.Id
                || component.Applications.Any(app => app != component.Id)
                || component.RequiredComponents.Concat(component.OptionalComponents).Concat(component.Conflicts)
                    .Any(id => !id.StartsWith(policy.PublisherId + ".", StringComparison.Ordinal)))
                throw Error("publisher_unauthorized");
            foreach (var target in component.Targets)
            {
                if (!policy.InstallerProviders.Contains(target.Installer.Type, StringComparer.Ordinal)) throw Error("provider_blocked");
                if (target.Artifact.Reference is { } reference)
                {
                    var index = ComponentCatalogFormat.Read<ComponentCatalogIndex>(metadata[publication.Catalog]);
                    var declaring = Resolve(index.Components.Single(entry => entry.Id == component.Id).Manifest, publication.Catalog);
                    var resourcePath = Resolve(reference, declaring);
                    var resource = publication.Resources.SingleOrDefault(file => file.Path == resourcePath);
                    if (resource is null || resource.Sha256 != target.Artifact.Sha256 || resource.Bytes > ArtifactLimit
                        || target.Artifact.Version != target.Artifact.Version?.Trim()) throw Error("artifact_identity_invalid");
                }
            }
        }
    }
}
