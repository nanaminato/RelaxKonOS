using System.Runtime.CompilerServices;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ProcessGuardian;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerGuardianCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerGuardian? client, CancellationToken lifetime) : IServerGuardian
{
    private IServerGuardian Client => client ?? throw new InvalidOperationException("The Guardian client is unavailable.");

    public Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.GetStatusAsync(token), manage: false, cancellationToken);

    public Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.ListWorkloadsAsync(token), manage: false, cancellationToken);

    public Task<GuardianAgentResponse> GetDefinitionAsync(string id, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.GetDefinitionAsync(id, token), manage: false, cancellationToken);

    public Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.UpsertAsync(definition, token), manage: true, cancellationToken);

    public Task<GuardianAgentResponse> DeleteAsync(string id, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.DeleteAsync(id, token), manage: true, cancellationToken);

    public Task<GuardianAgentResponse> ApplyActionAsync(string id, string action, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.ApplyActionAsync(id, action, token), manage: true, cancellationToken);

    public Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string id, CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.ListLogsAsync(id, token), manage: false, cancellationToken);

    public Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken) =>
        InvokeAsync(token => Client.ListAuditAsync(token), manage: false, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<GuardianLogEntryDto>> WatchLogsAsync(string id,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        CheckAccess(manage: false, linked.Token);
        using var observation = connection.ObserveInvalidation(linked.Cancel);
        await foreach (var snapshot in Client.WatchLogsAsync(id, linked.Token).WithCancellation(linked.Token))
        {
            CheckAccess(manage: false, linked.Token);
            yield return snapshot;
        }
    }

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
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerGuardianRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerGuardianManage))
            throw new UnauthorizedAccessException();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's Guardian connection has changed.");
    }
}
