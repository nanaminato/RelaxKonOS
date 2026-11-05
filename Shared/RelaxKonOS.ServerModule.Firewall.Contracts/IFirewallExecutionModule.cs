using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Closed Firewall-only Host port. It cannot name file, service, package or arbitrary execution operations.</summary>
public interface IFirewallHostOperations
{
    Task<FirewallHostResult> ExecuteAsync(FirewallHostRequest request, CancellationToken cancellationToken);
}

public enum FirewallHostOperation
{
    UfwStatus, UfwSetEnabled, UfwSetDefaults, UfwCreateRule, UfwReplaceRule, UfwDeleteRule,
    WindowsStatus, WindowsRules, WindowsSetEnabled, WindowsSetDefaults, WindowsCreateRule, WindowsReplaceRule, WindowsDeleteRule,
}

public sealed record FirewallHostRequest(FirewallHostOperation Operation,
    bool? FirewallEnabled = null, bool? FirewallNumberedStatus = null,
    FirewallDefaultPolicy? FirewallIncomingPolicy = null, FirewallDefaultPolicy? FirewallOutgoingPolicy = null,
    FirewallRuleAction? FirewallRuleAction = null, FirewallRuleDirection? FirewallRuleDirection = null,
    FirewallRuleProtocol? FirewallRuleProtocol = null, string? FirewallSource = null, string? FirewallDestination = null,
    string? FirewallPort = null, int? FirewallRuleNumber = null, int? FirewallCompanionRuleNumber = null);
public sealed record FirewallHostResult(bool Success, string? OutputBase64 = null, PrivilegedProblemCode ProblemCode = PrivilegedProblemCode.None);

/// <summary>Borrowed string-based logger; no implementation Type/exception is retained by Host logging caches.</summary>
public interface IFirewallExecutionLog
{
    void OperationFailed(FirewallHostOperation operation, PrivilegedProblemCode problem);
}
public sealed record FirewallExecutionContext(HostPlatformKind Platform, IFirewallHostOperations Host, IFirewallExecutionLog Log);
public interface IFirewallExecutionModule : IAsyncDisposable
{
    IHostFirewallService Firewall { get; }
}
