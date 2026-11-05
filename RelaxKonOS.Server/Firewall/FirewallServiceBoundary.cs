using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Materialized shared DTOs and fresh Host exceptions cross the collectible execution boundary.</summary>
internal sealed class FirewallServiceBoundary(IHostFirewallService service) : IHostFirewallService
{
    private IHostFirewallService? _service = service;
    internal void Release() => _service = null;
    public Task<FirewallStatusDto> GetStatusAsync(CancellationToken ct) => Call(firewall => firewall.GetStatusAsync(ct), ct);
    public Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken ct) => Call(firewall => firewall.ListRulesAsync(ct), ct);
    public Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken ct) => Call(firewall => firewall.SetEnabledAsync(enabled, ct), ct);
    public Task<FirewallOperationResult> SetDefaultsAsync(string incoming, string outgoing, CancellationToken ct) => Call(firewall => firewall.SetDefaultsAsync(incoming, outgoing, ct), ct);
    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken ct) => Call(firewall => firewall.CreateRuleAsync(request, ct), ct);
    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken ct) => Call(firewall => firewall.UpdateRuleAsync(number, request, ct), ct);
    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken ct) => Call(firewall => firewall.DeleteRuleAsync(number, ct), ct);
    private async Task<T> Call<T>(Func<IHostFirewallService, Task<T>> action, CancellationToken ct)
    {
        try { return await action(_service ?? throw new ObjectDisposedException(nameof(FirewallServiceBoundary))).ConfigureAwait(false); }
        catch (FirewallRulesUnavailableException failure)
        { throw new FirewallRulesUnavailableException(SafeProblem(failure.ProblemCode)); }
        catch (OperationCanceledException)
        { throw new OperationCanceledException(ct); }
        catch (Exception)
        { throw new FirewallExecutionException("firewall.operation_failed"); }
    }
    private static string SafeProblem(string problem) => problem is { Length: > 0 and <= 128 }
        && problem.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? problem : "firewall.operation_failed";
}
