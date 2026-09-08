using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Client.Apps.Firewall;

public interface IRemoteFirewallClient
{
    Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken = default);
    Task<FirewallOperationResult> SetEnabledAsync(UpdateFirewallEnabledRequest request, CancellationToken cancellationToken = default);
    Task<FirewallOperationResult> SetDefaultsAsync(UpdateFirewallDefaultsRequest request, CancellationToken cancellationToken = default);
    Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken = default);
    Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default);
    Task<FirewallOperationResult> DeleteRuleAsync(int number, DeleteFirewallRuleRequest request, CancellationToken cancellationToken = default);
}
