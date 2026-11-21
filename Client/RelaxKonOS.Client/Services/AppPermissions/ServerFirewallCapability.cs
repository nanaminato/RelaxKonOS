using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Firewall;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerFirewallCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerFirewall? client, CancellationToken lifetime) : IServerFirewall
{
    private IServerFirewall Client => client ?? throw new InvalidOperationException("The firewall client is unavailable.");

    public Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.GetStatusAsync(token), manage: false, cancellationToken);

    public Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.ListRulesAsync(token), manage: false, cancellationToken);

    public Task<FirewallOperationResult> SetEnabledAsync(UpdateFirewallEnabledRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.SetEnabledAsync(request, token), manage: true, cancellationToken);

    public Task<FirewallOperationResult> SetDefaultsAsync(UpdateFirewallDefaultsRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.SetDefaultsAsync(request, token), manage: true, cancellationToken);

    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.CreateRuleAsync(request, token), manage: true, cancellationToken);

    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.UpdateRuleAsync(number, request, token), manage: true, cancellationToken);

    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.DeleteRuleAsync(number, token), manage: true, cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation, bool manage, CancellationToken cancellationToken)
    {
        using var linked = lifetime.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken) : null;
        var token = linked?.Token ?? cancellationToken;
        CheckAccess(manage, token);
        var result = await operation(token);
        CheckAccess(manage, token);
        return result;
    }

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFirewallRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerFirewallManage))
            throw new UnauthorizedAccessException();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's firewall connection has changed.");
    }
}
