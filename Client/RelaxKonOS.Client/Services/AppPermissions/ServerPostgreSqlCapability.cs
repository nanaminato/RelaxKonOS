using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Databases;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerPostgreSqlCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, Func<IServerPostgreSql>? createClient, CancellationToken lifetime)
    : IServerPostgreSql
{
    public Task<PostgreSqlState> ReadAsync(CancellationToken cancellationToken, Guid? instance = null) =>
        InvokeAsync((client, token) => client.ReadAsync(token, instance), manage: false, cancellationToken);

    public Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.MaintenancePlanAsync(token), manage: false, cancellationToken);

    public Task<PostgreSqlState> MigrationPlanAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.MigrationPlanAsync(token), manage: false, cancellationToken);

    public Task StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(async (client, token) =>
        {
            await client.StageCredentialAsync(request, token);
            return true;
        }, manage: true, cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<IServerPostgreSql, CancellationToken, Task<T>> operation,
        bool manage, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        using var invalidation = connection.ObserveInvalidation(cancellation.Cancel);
        VerifyAccess(manage);
        cancellation.Token.ThrowIfCancellationRequested();
        var client = createClient?.Invoke() ?? throw new InvalidOperationException("database.state_unavailable");
        VerifyAccess(manage);
        cancellation.Token.ThrowIfCancellationRequested();
        var result = await operation(client, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        VerifyAccess(manage);
        return result;
    }

    private void VerifyAccess(bool manage)
    {
        if (!connection.IsCurrent)
            throw new InvalidOperationException("database.session_changed");
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerDatabasesRead)
            || manage && (!connection.IsController || !permissions.IsGranted(appId, CoreAppPermissions.ServerDatabasesManage)))
            throw new UnauthorizedAccessException();
    }
}
