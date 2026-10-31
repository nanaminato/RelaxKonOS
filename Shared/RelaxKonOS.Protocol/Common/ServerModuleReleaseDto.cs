using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Common;

public sealed record ServerModuleReleaseDependencyDto([property: JsonRequired] string ModuleId,
    [property: JsonRequired] string MinimumVersion, [property: JsonRequired] string MaximumVersionExclusive);

/// <summary>Signed directory metadata, not evidence of a verified archive or an installation.</summary>
public sealed record ServerModuleReleaseDto([property: JsonRequired] string ReleaseId, [property: JsonRequired] string ModuleId,
    [property: JsonRequired] string Version, [property: JsonRequired] string Rid, [property: JsonRequired] string PublisherId,
    [property: JsonRequired] string Purpose, [property: JsonRequired] string ManifestSha256, [property: JsonRequired] string ArchiveSha256,
    [property: JsonRequired] long ArchiveBytes, [property: JsonRequired] string HostApiMinimum, [property: JsonRequired] string HostApiMaximumExclusive,
    [property: JsonRequired] string BuildId, [property: JsonRequired] string SourceCommit,
    [property: JsonRequired] IReadOnlyList<ServerModuleReleaseDependencyDto> Dependencies, [property: JsonRequired] IReadOnlyList<string> Capabilities,
    [property: JsonRequired] string TrustState, [property: JsonRequired] bool RidCompatible, [property: JsonRequired] bool HostApiCompatible,
    [property: JsonRequired] bool ArtifactVerified, [property: JsonRequired] IReadOnlyList<ServerModuleBlocker> BlockedBy);

public sealed record ServerModuleReleasePageDto([property: JsonRequired] string ServiceId, [property: JsonRequired] string ModuleId,
    [property: JsonRequired] string SourceId, [property: JsonRequired] string CatalogRevision, [property: JsonRequired] long CatalogSequence,
    [property: JsonRequired] string PayloadSha256, [property: JsonRequired] DateTimeOffset IssuedAt, [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] DateTimeOffset ObservedAt, [property: JsonRequired] string HostRid, [property: JsonRequired] string HostApiVersion,
    [property: JsonRequired] string ObservationScope, [property: JsonRequired] bool PackageActionsSupported,
    [property: JsonRequired] IReadOnlyList<ServerModuleBlocker> BlockedBy, [property: JsonRequired] IReadOnlyList<ServerModuleReleaseDto> Items,
    [property: JsonRequired] int? NextOffset);
