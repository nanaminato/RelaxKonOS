using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Server.Firewall;

/// <summary>
/// Narrow host firewall boundary. Implementations accept only validated, structured UFW options;
/// callers can never provide a shell command or arbitrary UFW arguments.
/// </summary>
public interface IHostFirewallService
{
    Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken);
    Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
    Task<FirewallOperationResult> SetDefaultsAsync(string incomingPolicy, string outgoingPolicy, CancellationToken cancellationToken);
    Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken);
    Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken);
    Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken);
}
