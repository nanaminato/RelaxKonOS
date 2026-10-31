using System.Text.Json.Serialization;
using System.Text.Json;

namespace RelaxKonOS.Protocol.Common;

[JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleAction>))]
public enum ServerModuleAction { Enable, Disable, SetResidencyPolicy, Wake, TrySleep, Install, Update, Repair, Rollback, Remove }
[JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleOperationState>))]
public enum ServerModuleOperationState { Prepared, Applying, AwaitingRestart, Verifying, Succeeded, Failed, RecoveryRequired }

public sealed record ServerModuleBlocker(string ProblemCode, string? ModuleId = null);
public sealed record ServerModuleResidencyPolicyDto(string Strategy, int IdleGraceSeconds);
public sealed record ServerModuleActionAdmission([property: JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleAction>))] ServerModuleAction Action, bool Allowed, IReadOnlyList<ServerModuleBlocker> BlockedBy);
/// <summary>EnabledModuleIds is the complete exact desired set, never an incremental patch.</summary>
public sealed record ServerModulePlanRequest([property: JsonRequired] string ServiceId, [property: JsonRequired, JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleAction>))] ServerModuleAction Action,
    [property: JsonRequired] IReadOnlyList<string> ModuleIds, [property: JsonRequired] IReadOnlyList<string> EnabledModuleIds, [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey, string? ReleaseId = null, string? Strategy = null, int? IdleGraceSeconds = null);
public sealed record ServerModulePlanDto([property: JsonRequired] Guid PlanId, [property: JsonRequired] string ServiceId, [property: JsonRequired] ServerModulePlanRequest Target,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] DateTimeOffset ExpiresAt, [property: JsonRequired] bool RestartRequired,
    [property: JsonRequired] IReadOnlyList<ServerModuleBlocker> BlockedBy, [property: JsonRequired] IReadOnlyList<string> Added, [property: JsonRequired] IReadOnlyList<string> Removed);
public sealed record ServerModuleOperationDto([property: JsonRequired] Guid OperationId, [property: JsonRequired] Guid PlanId, [property: JsonRequired] string ServiceId,
    [property: JsonRequired, JsonConverter(typeof(ServerModuleEnumConverter<ServerModuleOperationState>))] ServerModuleOperationState State, [property: JsonRequired] DateTimeOffset CreatedAt, [property: JsonRequired] DateTimeOffset UpdatedAt,
    [property: JsonRequired] string DesiredRevision, [property: JsonRequired] IReadOnlyList<string> EnabledModuleIds, string? ProblemCode = null,
    IReadOnlyList<ServerModuleActionResult>? ModuleResults = null);
public sealed record ServerModuleActionResult([property: JsonRequired] string ModuleId, [property: JsonRequired] bool Succeeded, string? ProblemCode = null);
public sealed record ServerModuleOperationPage([property: JsonRequired] IReadOnlyList<ServerModuleOperationDto> Items, [property: JsonRequired] int? NextOffset);
public sealed record ServerModulePlanStatusDto([property: JsonRequired] ServerModulePlanDto Plan, [property: JsonRequired] ServerModuleOperationDto? Operation);

/// <summary>Only the canonical current enum name is a wire value; numbers and aliases are refused.</summary>
public sealed class ServerModuleEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name
            && Enum.TryParse<T>(name, out var value) && Enum.IsDefined(value) && Enum.GetName(value) == name) return value;
        throw new JsonException("Invalid module enum value.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("Invalid module enum value."));
}
public sealed record ServerModuleRecoveryReportDto(string ServiceId, string DesiredRevision,
    IReadOnlyList<string> DesiredEnabledModuleIds, IReadOnlyDictionary<string, ServerModuleResidencyPolicyDto> DesiredPolicies,
    IReadOnlyList<ServerModuleOperationDto> UnresolvedOperations, DateTimeOffset ObservedAt, string ObservationScope);

public sealed record ServerInstalledModuleDto([property: JsonRequired] string ModuleId, [property: JsonRequired] string Version,
    [property: JsonRequired] string ReleaseId, [property: JsonRequired] string SourceId, [property: JsonRequired] string PublisherId,
    [property: JsonRequired] string ArtifactSha256, [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] string PackagePath, [property: JsonRequired] string EntryAssembly, [property: JsonRequired] string EntryType,
    [property: JsonRequired] IReadOnlyList<string> Capabilities, [property: JsonRequired] IReadOnlyList<string> Requires,
    [property: JsonRequired] DateTimeOffset InstalledAt, [property: JsonRequired] bool Active, bool Quarantined = false);
public sealed record ServerInstalledModulePageDto([property: JsonRequired] IReadOnlyList<ServerInstalledModuleDto> Items);
public sealed record ServerPackageOperationDto([property: JsonRequired] Guid OperationId, [property: JsonRequired] string Action,
    [property: JsonRequired] string ModuleId, [property: JsonRequired] string Version, [property: JsonRequired] string ReleaseId,
    [property: JsonRequired] string ArtifactSha256, string? PreviousVersion, [property: JsonRequired] string State,
    string? ProblemCode, [property: JsonRequired] DateTimeOffset CreatedAt, [property: JsonRequired] DateTimeOffset UpdatedAt,
    string? ProcessGeneration);
public sealed record ServerPackageOperationPageDto([property: JsonRequired] IReadOnlyList<ServerPackageOperationDto> Items);
public sealed record ServerModulePackageUploadDto([property: JsonRequired] string UploadId, [property: JsonRequired] long Bytes,
    [property: JsonRequired] string Sha256);
public sealed record ServerModulePackageInstallRequest([property: JsonRequired] string SourceId, [property: JsonRequired] string ReleaseId,
    [property: JsonRequired] string UploadId);
public sealed record ServerModulePackageInstallResultDto([property: JsonRequired] Guid OperationId,
    [property: JsonRequired] ServerInstalledModuleDto Record);
public sealed record ServerModulePackagePlanDto([property: JsonRequired] Guid PlanId, [property: JsonRequired] string SourceId,
    [property: JsonRequired] string ReleaseId, [property: JsonRequired] string ModuleId, [property: JsonRequired] string Version,
    [property: JsonRequired] string PublisherId, [property: JsonRequired] string ArtifactSha256,
    [property: JsonRequired] string ManifestSha256, [property: JsonRequired] DateTimeOffset ExpiresAt,
    Guid? AppliedOperationId, [property: JsonRequired] string Runtime,
    [property: JsonRequired] IReadOnlyList<ServerModulePrivilegeDeclarationDto> Privileges,
    [property: JsonRequired] string Name, [property: JsonRequired] string Summary);
public sealed record ServerModulePackageRollbackRequest([property: JsonRequired] string ModuleId,
    [property: JsonRequired] string Version);
public sealed record ServerModulePackageAutoPlanRequest([property: JsonRequired] string UploadId);
public sealed record ServerModuleStoreItemDto([property: JsonRequired] string SourceId, [property: JsonRequired] string ModuleId,
    [property: JsonRequired] string Version, [property: JsonRequired] string ReleaseId, [property: JsonRequired] string Rid,
    [property: JsonRequired] string PublisherId, [property: JsonRequired] string Purpose, [property: JsonRequired] string TrustState,
    [property: JsonRequired] string ArtifactSha256, [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] long ArchiveBytes, [property: JsonRequired] string HostApiMinimum,
    [property: JsonRequired] string HostApiMaximumExclusive,
    [property: JsonRequired] IReadOnlyList<ServerModuleReleaseDependencyDto> Dependencies,
    [property: JsonRequired] IReadOnlyList<string> Capabilities, [property: JsonRequired] bool RidCompatible,
    [property: JsonRequired] bool HostApiCompatible, [property: JsonRequired] bool Revoked,
    [property: JsonRequired] IReadOnlyList<ServerModuleBlocker> BlockedBy);
public sealed record ServerModuleStorePageDto([property: JsonRequired] IReadOnlyList<ServerModuleStoreItemDto> Items,
    int? NextOffset, [property: JsonRequired] IReadOnlyList<ServerModuleBlocker> BlockedBy);
public sealed record ServerModuleStoreDownloadRequest([property: JsonRequired] string SourceId,
    [property: JsonRequired] string ReleaseId);
public sealed record ServerModuleStoreSourceDto([property: JsonRequired] string SourceId,
    [property: JsonRequired] string Purpose, [property: JsonRequired] string Status, [property: JsonRequired] long Sequence,
    [property: JsonRequired] long PolicyRevision, [property: JsonRequired] int ReleaseCount,
    [property: JsonRequired] int RevokedCount, [property: JsonRequired] bool CatalogUriConfigured, string? ProblemCode);
public sealed record ServerModuleStoreSourcePageDto([property: JsonRequired] string CatalogRevision,
    [property: JsonRequired] IReadOnlyList<ServerModuleStoreSourceDto> Sources);
public sealed record ServerModuleCatalogRefreshRequest([property: JsonRequired] string SourceId,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] string IdempotencyKey, string? CatalogUri);
public sealed record ServerModuleCatalogReceiptDto([property: JsonRequired] string SourceId,
    [property: JsonRequired] string Revision, [property: JsonRequired] long Sequence,
    [property: JsonRequired] string PayloadSha256, [property: JsonRequired] DateTimeOffset AcceptedAt);
public sealed record ServerModuleHealthModuleDto([property: JsonRequired] string ModuleId,
    [property: JsonRequired] bool Enabled, [property: JsonRequired] bool Installed, string? Version, string? SourceId,
    [property: JsonRequired] string Status, string? ProblemCode);
public sealed record ServerModuleHealthPageDto([property: JsonRequired] bool Healthy,
    [property: JsonRequired] DateTimeOffset CheckedAt,
    [property: JsonRequired] IReadOnlyList<ServerModuleHealthModuleDto> Modules);
public sealed record ServerModuleHostStatusDto([property: JsonRequired] string ModuleId,
    [property: JsonRequired] string Status, string? Version, string? HostApiVersion, string? ProblemCode);
public sealed record ServerModuleHostPageDto([property: JsonRequired] IReadOnlyList<ServerModuleHostStatusDto> Hosts);

public static class ServerModuleManagementRoutes
{
    public const string Detail = ServerApiRoutes.Modules + "/{moduleId}";
    public const string Plans = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-plans";
    public const string Apply = Plans + "/{planId:guid}/apply";
    public const string Plan = Plans + "/{planId:guid}";
    public const string Operations = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-operations";
    public const string Operation = Operations + "/{operationId:guid}";
    public const string Releases = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-releases";
    public const string InstalledPackages = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-packages/installed";
    public const string PackageVersions = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-packages/{moduleId}/versions";
    public const string PackageOperations = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-package-operations";
    public const string PackageUpload = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-packages/upload";
    public const string PackageInstall = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-packages/install";
    public const string PackagePlans = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-packages/plans";
    public const string PackagePlanApply = PackagePlans + "/{planId:guid}/apply";
    public const string PackageAutoPlan = PackagePlans + "/auto";
    public const string PackageRollback = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-packages/rollback";
    public const string ModuleStoreSearch = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-store/search";
    public const string ModuleStoreDownload = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-store/download";
    public const string ModuleStoreSources = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-store/sources";
    public const string ModuleStoreRefresh = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-store/refresh";
    public const string ModuleHealth = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-health";
    public const string ModuleHosts = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-hosts";
    public const string ModuleHostProxy = ModuleHosts + "/{moduleId}/{**path}";
}

public sealed record ServerModulePrivilegeDeclarationDto(string Kind, string Target);

public static class ServerModulePrivilegeRoutes
{
    public const string Plans = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-privileges/plans";
    public const string Trust = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/server/module-trust/{moduleId}";
}
public sealed record ServerModuleTrustRequest(string ArtifactSha256, string ManifestSha256, bool Trusted);
