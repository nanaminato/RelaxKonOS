namespace RelaxKonOS.ModuleHost.Protocol;

public static class ModulePrivilegeProtocol
{
    public const string BrokerEnvironment = "RELAXKONOS_MODULE_BROKER_URL";
    public const string RequestContextHeader = "X-RelaxKonOS-Module-Context";
}

/// <summary>Reference to a Host-owned secret. It names a purpose and target; it never carries a value.</summary>
public sealed record ModuleSecretReference(string Purpose, string Target, string Permission);

/// <summary>Closed request. Category + action + target; no commands, executable paths, argv, identity or
/// administrator credentials.</summary>
public sealed record ModulePrivilegeRequest(string Kind, string Action, string Target, string Reason,
    string IdempotencyKey, ModuleSecretReference[]? Secrets = null);
public sealed record ModulePrivilegePrepared(Guid PlanId, string PlanHash, string State);
public sealed record ModulePrivilegeStatus(Guid PlanId, string State, string? ProblemCode, DateTimeOffset UpdatedAt);
/// <summary>Display projection of the exact package identity a plan froze. The Host assembles it from the
/// Helper's read-only resolution; a module never supplies it and a client never re-resolves it. It carries
/// no secret value and no path on this host.</summary>
public sealed record ModulePackagePlanIdentity(string SourceRef, string Package, string Version, string Architecture,
    string Repository, string ArtifactFileName, string Sha256, long Bytes, ModulePackagePlanDependency[] Dependencies);

public sealed record ModulePackagePlanDependency(string FileName, long Bytes, string Sha256);

public sealed record ModulePrivilegePlan(Guid PlanId, string ModuleId, string Version, string PublisherId,
    string ArtifactSha256, string Kind, string Action, string Target, string Reason, string[] Effects, string[] Warnings,
    ModulePackagePlanIdentity? Package, string PlanHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State,
    string? ProblemCode, string ConfirmationToken);
public sealed record ModulePrivilegeApproval(string PlanHash, string ConfirmationToken,
    string? AdministratorUsername = null, string? Password = null);

public sealed record ModulePrivilegeConsentRequest(string ModuleId, string Kind, string Action, string Target,
    string Reason, DateTimeOffset ExpiresAt, string IdempotencyKey);
public sealed record ModulePrivilegeConsent(Guid ConsentId, string ModuleId, string Version, string PublisherId,
    string ArtifactSha256, string Kind, string Action, string Target, string Reason, string GrantedBy,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State, string PlanHash, string ConfirmationToken);
public sealed record ModulePrivilegeBackgroundRequest(Guid ConsentId, ModulePrivilegeRequest Operation);
