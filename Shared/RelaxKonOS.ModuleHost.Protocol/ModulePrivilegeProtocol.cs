namespace RelaxKonOS.ModuleHost.Protocol;

public static class ModulePrivilegeProtocol
{
    public const string BrokerEnvironment = "RELAXKONOS_MODULE_BROKER_URL";
    public const string RequestContextHeader = "X-RelaxKonOS-Module-Context";
}

/// <summary>Closed request. No commands, executable paths, argv, identity or administrator credentials.</summary>
public sealed record ModulePrivilegeRequest(string Kind, string Target, string Reason, string IdempotencyKey);
public sealed record ModulePrivilegePrepared(Guid PlanId, string PlanHash, string State);
public sealed record ModulePrivilegeStatus(Guid PlanId, string State, string? ProblemCode, DateTimeOffset UpdatedAt);
public sealed record ModulePrivilegePlan(Guid PlanId, string ModuleId, string Version, string PublisherId,
    string ArtifactSha256, string Kind, string Target, string Reason, string[] Effects, string[] Warnings,
    string PlanHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State, string? ProblemCode,
    string ConfirmationToken);
public sealed record ModulePrivilegeApproval(string PlanHash, string ConfirmationToken,
    string? AdministratorUsername = null, string? Password = null);
