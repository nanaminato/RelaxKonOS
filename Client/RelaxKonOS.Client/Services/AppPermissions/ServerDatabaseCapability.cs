using System.Net;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Databases;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerDatabaseCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, Func<IServerPostgreSql>? createClient, CancellationToken lifetime,
    Func<IServerMySql>? createMySqlClient)
    : IServerPostgreSql, IServerMySql
{
    public Task<PostgreSqlState> ReadAsync(CancellationToken cancellationToken, Guid? instance = null) =>
        InvokeAsync(createClient, (client, token) => client.ReadAsync(token, instance), manage: false, cancellationToken);

    public Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken cancellationToken) =>
        InvokeAsync(createClient, (client, token) => client.MaintenancePlanAsync(token), manage: false, cancellationToken);

    public Task<PostgreSqlState> MigrationPlanAsync(CancellationToken cancellationToken) =>
        InvokeAsync(createClient, (client, token) => client.MigrationPlanAsync(token), manage: false, cancellationToken);

    public Task StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(createClient, async (client, token) =>
        {
            await client.StageCredentialAsync(request, token);
            return true;
        }, manage: true, cancellationToken);

    Task<MySqlState?> IServerMySql.ReadAsync(CancellationToken cancellationToken) =>
        InvokeAsync(createMySqlClient, (client, token) => client.ReadAsync(token), manage: false, cancellationToken);

    Task<HttpStatusCode> IServerMySql.StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(createMySqlClient, (client, token) => client.StageCredentialAsync(request, token), manage: true, cancellationToken);

    private async Task<T> InvokeAsync<TClient, T>(Func<TClient>? createClient,
        Func<TClient, CancellationToken, Task<T>> operation,
        bool manage, CancellationToken cancellationToken) where TClient : class
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
