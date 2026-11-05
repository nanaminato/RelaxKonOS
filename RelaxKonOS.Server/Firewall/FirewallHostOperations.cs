using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Only the Core can map a closed Firewall request to its privileged transport.</summary>
internal sealed class FirewallHostOperations(IPrivilegedOperationTransport transport) : IFirewallHostOperations
{
    public async Task<FirewallHostResult> ExecuteAsync(FirewallHostRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = request.Operation switch
        {
            FirewallHostOperation.UfwStatus => PrivilegedOperationKind.FirewallUfwStatus,
            FirewallHostOperation.UfwSetEnabled => PrivilegedOperationKind.FirewallUfwSetEnabled,
            FirewallHostOperation.UfwSetDefaults => PrivilegedOperationKind.FirewallUfwSetDefaults,
            FirewallHostOperation.UfwCreateRule => PrivilegedOperationKind.FirewallUfwCreateRule,
            FirewallHostOperation.UfwReplaceRule => PrivilegedOperationKind.FirewallUfwReplaceRule,
            FirewallHostOperation.UfwDeleteRule => PrivilegedOperationKind.FirewallUfwDeleteRule,
            FirewallHostOperation.WindowsStatus => PrivilegedOperationKind.FirewallWindowsStatus,
            FirewallHostOperation.WindowsRules => PrivilegedOperationKind.FirewallWindowsRules,
            FirewallHostOperation.WindowsSetEnabled => PrivilegedOperationKind.FirewallWindowsSetEnabled,
            FirewallHostOperation.WindowsSetDefaults => PrivilegedOperationKind.FirewallWindowsSetDefaults,
            FirewallHostOperation.WindowsCreateRule => PrivilegedOperationKind.FirewallWindowsCreateRule,
            FirewallHostOperation.WindowsReplaceRule => PrivilegedOperationKind.FirewallWindowsReplaceRule,
            FirewallHostOperation.WindowsDeleteRule => PrivilegedOperationKind.FirewallWindowsDeleteRule,
            _ => throw new InvalidOperationException("Unsupported Firewall Host operation."),
        };
        if (!ValidShape(request)) return new(false, ProblemCode: PrivilegedProblemCode.InvalidRequest);
        var result = await transport.ExecuteAsync(new(operation,
            FirewallEnabled: request.FirewallEnabled, FirewallNumberedStatus: request.FirewallNumberedStatus,
            FirewallIncomingPolicy: request.FirewallIncomingPolicy, FirewallOutgoingPolicy: request.FirewallOutgoingPolicy,
            FirewallRuleAction: request.FirewallRuleAction, FirewallRuleDirection: request.FirewallRuleDirection,
            FirewallRuleProtocol: request.FirewallRuleProtocol, FirewallSource: request.FirewallSource,
            FirewallDestination: request.FirewallDestination, FirewallPort: request.FirewallPort,
            FirewallRuleNumber: request.FirewallRuleNumber, FirewallCompanionRuleNumber: request.FirewallCompanionRuleNumber), cancellationToken);
        return new(result.Success, result.OutputBase64, result.ProblemCode);
    }
    private static bool ValidShape(FirewallHostRequest request)
    {
        FirewallHostRequest? shape = request.Operation switch
        {
            FirewallHostOperation.UfwStatus => new(request.Operation, FirewallNumberedStatus: request.FirewallNumberedStatus),
            FirewallHostOperation.WindowsStatus or FirewallHostOperation.WindowsRules => new(request.Operation),
            FirewallHostOperation.UfwSetEnabled or FirewallHostOperation.WindowsSetEnabled when request.FirewallEnabled is not null
                => new(request.Operation, FirewallEnabled: request.FirewallEnabled),
            FirewallHostOperation.UfwSetDefaults or FirewallHostOperation.WindowsSetDefaults when request.FirewallIncomingPolicy is not null && request.FirewallOutgoingPolicy is not null
                => new(request.Operation, FirewallIncomingPolicy: request.FirewallIncomingPolicy, FirewallOutgoingPolicy: request.FirewallOutgoingPolicy),
            FirewallHostOperation.UfwCreateRule or FirewallHostOperation.WindowsCreateRule => Rule(request, false),
            FirewallHostOperation.UfwReplaceRule or FirewallHostOperation.WindowsReplaceRule when request.FirewallRuleNumber is > 0 and <= 10000 => Rule(request, true),
            FirewallHostOperation.UfwDeleteRule or FirewallHostOperation.WindowsDeleteRule when request.FirewallRuleNumber is > 0 and <= 10000
                => new(request.Operation, FirewallRuleNumber: request.FirewallRuleNumber, FirewallCompanionRuleNumber: request.Operation == FirewallHostOperation.UfwDeleteRule ? request.FirewallCompanionRuleNumber : null),
            _ => null,
        };
        return shape is not null && shape == request
            && (request.FirewallCompanionRuleNumber is null or > 0 and <= 10000);
    }
    private static FirewallHostRequest Rule(FirewallHostRequest request, bool replacement) => new(request.Operation,
        FirewallRuleAction: request.FirewallRuleAction, FirewallRuleDirection: request.FirewallRuleDirection,
        FirewallRuleProtocol: request.FirewallRuleProtocol, FirewallSource: request.FirewallSource,
        FirewallDestination: request.FirewallDestination, FirewallPort: request.FirewallPort,
        FirewallRuleNumber: replacement ? request.FirewallRuleNumber : null,
        FirewallCompanionRuleNumber: replacement && request.Operation == FirewallHostOperation.UfwReplaceRule ? request.FirewallCompanionRuleNumber : null);
}

internal sealed class FirewallExecutionLog(ILogger logger) : IFirewallExecutionLog
{
    public void OperationFailed(FirewallHostOperation operation, PrivilegedProblemCode problem) =>
        logger.LogWarning("Structured Firewall Helper operation failed. Operation={Operation}, Problem={Problem}", operation, problem);
}
