using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Windows and other unsupported hosts have no Linux firewall implementation.</summary>
public sealed class UnavailableHostFirewallService : IHostFirewallService
{
    private static readonly FirewallStatusDto Status = new(false, false, "", null, null, null, "firewall.unsupported_platform");
    private static readonly FirewallOperationResult Unavailable = new(false, "firewall.unsupported_platform");

    public Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
    public Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FirewallRuleDto>>([]);
    public Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<FirewallOperationResult> SetDefaultsAsync(string incomingPolicy, string outgoingPolicy, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
}
