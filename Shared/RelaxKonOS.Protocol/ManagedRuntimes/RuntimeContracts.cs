using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeOwnership>))]
public enum RuntimeOwnership { Unknown, Absent, External, Managed }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeTrust>))]
public enum RuntimeTrust { Unconfirmed, Confirmed, Revoked, Conflict }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeChannel>))]
public enum RuntimeChannel { Stable, Prerelease }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeInstallMode>))]
public enum RuntimeInstallMode { Archive, SystemPackage }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeEvidence>))]
public enum RuntimeEvidence { Unverified, NativeConfiguration, Interoperability }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeChange>))]
public enum RuntimeChange { Install, Update, Repair, Rollback, Uninstall, Configure }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeSourceKind>))]
public enum RuntimeSourceKind { HttpsManifest }

/// <summary>Public identity only. Credentials and trust key material belong in protected host storage.</summary>
public sealed record RuntimeSourceIdentity(string SourceId, string Publisher, bool Enabled,
    RuntimeTrust Trust, long TrustRevision);

/// <summary>Platform names are exact adapter-defined identities; no architecture aliases are inferred.</summary>
public sealed record RuntimeTarget(string Os, string Architecture, string? Distribution,
    string? Libc, RuntimeInstallMode InstallMode, string ServiceHost);

/// <summary>Immutable identity authenticated by a source adapter, never by a client request.</summary>
public sealed record RuntimeArtifact(string ArtifactId, string Sha256, long MaximumBytes,
    string Format, string EntryPoint, string VerificationIdentity, RuntimeArchiveLayout Layout);

public sealed record RuntimeArchiveFile(string Path, long Length, string Sha256, bool Executable);
public sealed record RuntimeArchiveLayout(long MaximumExpandedBytes, int MaximumEntries, IReadOnlyList<RuntimeArchiveFile> Files);

public sealed record RuntimeRelease(string ReleaseId, string RuntimeId, string OriginalVersion,
    string SortVersion, RuntimeChannel Channel, string SourceId, RuntimeTarget Target,
    RuntimeArtifact Artifact, string ConfigurationCapability, bool ConfigurationSupported,
    RuntimeTrust Trust, RuntimeEvidence Evidence, DateTimeOffset? PublishedAt);

/// <summary>SignatureVerified must come from the trusted adapter; these DTOs do not verify signatures.</summary>
public sealed record RuntimeCatalog(string SourceId, long Revision, long TrustRevision,
    DateTimeOffset ExpiresAt, bool SignatureVerified, IReadOnlyList<RuntimeRelease> Releases);

public sealed record RuntimeInstallation(Guid InstallationId, string HostId, string RuntimeId,
    long StateRevision, RuntimeOwnership Ownership, RuntimeTarget Target, string? ActiveReleaseId,
    bool RecoveryRequired);

/// <summary>Every mutation names its target and expected revisions. Uninstall has no release target.</summary>
public sealed record RuntimePlanRequest(Guid InstallationId, string HostId, RuntimeChange Change,
    long ExpectedStateRevision, long ExpectedCatalogRevision, long ExpectedTrustRevision,
    string? ReleaseId, string IdempotencyKey, bool AllowPrerelease = false, bool KeepData = true);

/// <summary>A frozen proposal, not permission to execute. Helper independently revalidates trust.</summary>
public sealed record RuntimeChangePlan(RuntimePlanRequest Request, string RuntimeId,
    RuntimeRelease? Release, string? PreviousReleaseId);

/// <summary>
/// A frozen proposal, not permission to execute. Helper independently revalidates trust.
/// <paramref name="Dependencies"/> is the Host's measurement for the requested release, returned even when
/// the request is refused so the refusal can name the fact that failed.
/// </summary>
public sealed record RuntimePlanResult(RuntimeChangePlan? Plan, IReadOnlyList<string> Problems,
    RuntimeDependencyReport? Dependencies = null)
{
    public bool Allowed => Plan is not null && Problems.Count == 0;
}

public sealed record RuntimeSourceView(RuntimeSourceIdentity Identity, string RuntimeId, RuntimeSourceKind Kind, string BaseUri,
    string ConfirmationFingerprint, long CatalogRevision);
public sealed record RuntimeCatalogSnapshot(RuntimeSourceView Source, RuntimeCatalog? Catalog, bool Fresh, string? ProblemCode);

public static class RuntimeRoutes
{
    public const string Root = "/" + Common.RelaxKonOSEndpoints.ApiVersionPrefix + "/managed-runtimes";
    public const string Sources = Root + "/sources";
    public const string Catalog = Sources + "/{sourceId}/catalog";
    public const string Refresh = Sources + "/{sourceId}/refresh";
    public const string SingBoxInstallation = Root + "/sing-box/installation";
    public const string SingBoxPlan = Root + "/sing-box/plan";
    /// <summary>Read-only ledger and live service facts for an unfinished transaction.</summary>
    public const string SingBoxRecovery = Root + "/sing-box/recovery";
    /// <summary>Retries the Host's own compensation; never prepares a new revision.</summary>
    public const string SingBoxRecoveryRetry = SingBoxRecovery + "/retry";
    /// <summary>Stops the managed service; leaves the transaction ledger untouched.</summary>
    public const string SingBoxRecoveryStop = SingBoxRecovery + "/stop";
    /// <summary>Closes an unrecoverable transaction after the operator confirms the observed state.</summary>
    public const string SingBoxRecoveryClose = SingBoxRecovery + "/close";
}
