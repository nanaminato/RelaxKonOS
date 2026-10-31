namespace RelaxKonOS.ModulePackaging;

public sealed record ModulePackageFile(string Path, long Length, string Sha256);
public sealed record ModulePackageDependency(string ModuleId, string MinimumVersion, string MaximumVersionExclusive);
public sealed record ModulePackageManifest(int SchemaVersion, string PackageKind, string ModuleId, string ReleaseId,
    string Version, string PublisherId, string Purpose, string Rid, string HostApiMinimum, string HostApiMaximumExclusive,
    string EntryAssembly, string EntryType, string BuildId, string SourceCommit,
    ModulePackageDependency[] Dependencies, string[] Capabilities, ModulePackageFile[] Files);
public sealed record ModulePackageRelease(ModulePackageManifest Manifest, string ManifestSha256, string ArchiveSha256,
    long ArchiveBytes, string DownloadUri);
public sealed record ModulePackageCatalog(int SchemaVersion, string SourceId, long Sequence, DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt, ModulePackageRelease[] Releases, string[] RevokedReleaseIds, string[] RevokedDigests);
public sealed record SignedModulePackageCatalog(int SchemaVersion, string KeyId, string Payload, string Signature);

/// <summary>Only trusted Host deployment configuration may supply this policy; never package metadata.</summary>
public sealed record ModulePackageTrustPolicy(int SchemaVersion, string SourceId, long Revision, bool Enabled,
    string PublisherId, string Purpose, string ArtifactOrigin, string[] AuthorizedModuleIds,
    IReadOnlyDictionary<string, string> PublicKeysSpki, int MaximumValiditySeconds, string? DevelopmentServiceId = null);
public sealed record ModulePackageContext(string ServiceId, string Rid, string HostApiVersion, bool DevelopmentInstance);

/// <summary>Acceptance must durably commit the returned history before making releases usable.</summary>
public sealed record ModulePackageTrustHistory(string SourceId, string ServiceId, string Purpose, long PolicyRevision,
    string? PolicyHash, IReadOnlyDictionary<string, string> KeyHashes, string[] RetiredKeyIds, long Sequence,
    string? PayloadHash, IReadOnlyDictionary<string, string> ReleaseIdentities,
    IReadOnlyDictionary<string, string> VersionIdentities, IReadOnlyDictionary<string, string> ReleaseDigests,
    string[] RevokedReleaseIds, string[] RevokedDigests)
{
    public static ModulePackageTrustHistory Empty(ModulePackageTrustPolicy policy, ModulePackageContext context) =>
        new(policy.SourceId, context.ServiceId, policy.Purpose, 0, null, new Dictionary<string, string>(), [], 0, null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), [], []);
}
public sealed record VerifiedModulePackageCatalog(ModulePackageCatalog Catalog, ModulePackageTrustHistory NextHistory, string PayloadSha256);
public sealed record VerifiedModulePackageCandidate(ModulePackageManifest Manifest, string ArchiveSha256, long ArchiveBytes,
    string SourceId, long CatalogSequence, string KeyId, bool ReadOnly);
public sealed class ModulePackageException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode => Message;
}
