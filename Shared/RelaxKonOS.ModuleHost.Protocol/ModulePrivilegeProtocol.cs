namespace RelaxKonOS.ModuleHost.Protocol;

public static class ModulePrivilegeProtocol
{
    public const string BrokerEnvironment = "RELAXKONOS_MODULE_BROKER_URL";
    public const string RequestContextHeader = "X-RelaxKonOS-Module-Context";
}

/// <summary>Reference to a Host-owned secret. It names a purpose and target; it never carries a value.</summary>
public sealed record ModuleSecretReference(string Purpose, string Target, string Permission);

/// <summary>Closed request. Category + action + target; no commands, executable paths, argv, identity or
/// administrator credentials. Content is the exact configuration bytes a file publication confirmed by
/// digest; it is never persisted in the plan store or displayed. BackupRef names one module-scoped backup.</summary>
public sealed record ModulePrivilegeRequest(string Kind, string Action, string Target, string Reason,
    string IdempotencyKey, ModuleSecretReference[]? Secrets = null, string? ContentBase64 = null, string? BackupRef = null);
public sealed record ModulePrivilegePrepared(Guid PlanId, string PlanHash, string State);
public sealed record ModulePrivilegeStatus(Guid PlanId, string State, string? ProblemCode, DateTimeOffset UpdatedAt);
/// <summary>Display projection of the exact package identity a plan froze. The Host assembles it from the
/// Helper's read-only resolution; a module never supplies it and a client never re-resolves it. It carries
/// no secret value and no path on this host.</summary>
public sealed record ModulePackagePlanIdentity(string SourceRef, string Package, string Version, string Architecture,
    string Repository, string ArtifactFileName, string Sha256, long Bytes, ModulePackagePlanDependency[] Dependencies);

public sealed record ModulePackagePlanDependency(string FileName, long Bytes, string Sha256);

/// <summary>Display projection of one frozen configuration-file transaction. It names the canonical path,
/// the observed before state and the exact after digest/bytes/mode. Content itself never appears here.</summary>
public sealed record ModuleFilePlanIdentity(string Root, string Path, string Action, string BeforeKind,
    string BeforeSha256, long BeforeBytes, string AfterKind, string AfterSha256, long AfterBytes, int Mode);

/// <summary>Display projection of one frozen data-root action. The structural digest covers every regular
/// file's relative name, length and content digest; it is not a backup and carries no file content.</summary>
public sealed record ModuleDataPlanIdentity(string Root, string Action, string BeforeKind, long BeforeFiles,
    long BeforeBytes, string BeforeDigest, string BackupRef);

/// <summary>Display projection of one frozen database-management action. It names the instance, account,
/// server version and an opaque secret revision; no password or credential path appears here.</summary>
public sealed record ModuleApplicationPlanIdentity(string Instance, string Protocol, string Action, string Account,
    string AccountHost, string ServerVersion, string SecretRevision);

public sealed record ModulePrivilegePlan(Guid PlanId, string ModuleId, string Version, string PublisherId,
    string ArtifactSha256, string Kind, string Action, string Target, string Reason, string[] Effects, string[] Warnings,
    ModulePackagePlanIdentity? Package, ModuleFilePlanIdentity? File, ModuleDataPlanIdentity? Data,
    ModuleApplicationPlanIdentity? Application,
    string PlanHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State,
    string? ProblemCode, string ConfirmationToken);
public sealed record ModulePrivilegeApproval(string PlanHash, string ConfirmationToken,
    string? AdministratorUsername = null, string? Password = null);

/// <summary>Standing permission request. A file write binds the exact content digest; no content value
/// is accepted here. Other categories must leave it null.</summary>
public sealed record ModulePrivilegeConsentRequest(string ModuleId, string Kind, string Action, string Target,
    string Reason, DateTimeOffset ExpiresAt, string IdempotencyKey, string? ContentSha256 = null);
public sealed record ModulePrivilegeConsent(Guid ConsentId, string ModuleId, string Version, string PublisherId,
    string ArtifactSha256, string Kind, string Action, string Target, string Reason, string GrantedBy,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State, string PlanHash, string ConfirmationToken,
    string? ContentSha256 = null);
public sealed record ModulePrivilegeBackgroundRequest(Guid ConsentId, ModulePrivilegeRequest Operation);
