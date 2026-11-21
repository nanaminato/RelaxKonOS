using System.Net;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerDatabaseCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, Func<IServerPostgreSql>? createClient, CancellationToken lifetime,
    Func<IServerMySql>? createMySqlClient, Func<IInstallationOperations>? createInstallationClient,
    IHostElevationBroker? elevationBroker)
    : IServerPostgreSql, IServerMySql, IDatabaseInstallationOperations
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

    public Task<InstallationOperationDto?> StartAsync(InstallationServiceId service, InstallationOperationKind kind,
        object options, string key, CancellationToken cancellationToken)
    {
        VerifyInstallationService(service);
        return InvokeAsync(createInstallationClient, (client, token) => client.StartAsync(service, kind, options, key, token),
            manage: true, cancellationToken);
    }

    public Task<InstallationOperationDto?> GetActiveAsync(InstallationServiceId service, CancellationToken cancellationToken)
    {
        VerifyInstallationService(service);
        return InvokeAsync(createInstallationClient, (client, token) => client.GetActiveAsync(service, token),
            manage: false, cancellationToken);
    }

    public Task<InstallationOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        InvokeAsync(createInstallationClient, async (client, token) =>
        {
            var operation = await client.GetAsync(id, token);
            return operation is not null && operation.OperationId == id && IsDatabaseInstallationService(operation.Service) ? operation : null;
        }, manage: false, cancellationToken);

    public Task<InstallationOperationDto?> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        InvokeAsync(createInstallationClient, async (client, token) =>
        {
            var operation = await client.GetAsync(id, token);
            if (operation is null || operation.OperationId != id) return null;
            VerifyInstallationService(operation.Service);
            VerifyAccess(manage: true);
            token.ThrowIfCancellationRequested();
            return await client.CancelAsync(id, token);
        }, manage: true, cancellationToken);

    public async Task<bool> RequestAuthorizationAsync(InstallationServiceId service, CancellationToken cancellationToken = default)
    {
        VerifyInstallationService(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        using var invalidation = connection.ObserveInvalidation(cancellation.Cancel);
        VerifyAccess(manage: true);
        cancellation.Token.ThrowIfCancellationRequested();
        if (elevationBroker is null) return false;

        try
        {
            var granted = await elevationBroker.RequestGrantAsync(InstallationClient.ElevationCapability(service),
                service.ToString().ToLowerInvariant(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            VerifyAccess(manage: true);
            return granted;
        }
        catch (RelaxKonOSAuthException error)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            VerifyAccess(manage: true);
            throw new InstallationAuthorizationException(error.Type.Split('/').Last());
        }
    }

    private static void VerifyInstallationService(InstallationServiceId service)
    {
        if (!IsDatabaseInstallationService(service)) throw new UnauthorizedAccessException();
    }

    private static bool IsDatabaseInstallationService(InstallationServiceId service) => service is
        InstallationServiceId.PostgreSql or InstallationServiceId.PostgreSqlAccounts or InstallationServiceId.PostgreSqlAdminRecovery
        or InstallationServiceId.PostgreSqlRemoteAccess or InstallationServiceId.PostgreSqlMaintenance or InstallationServiceId.PostgreSqlBackupAutomation
        or InstallationServiceId.MySql or InstallationServiceId.MySqlAccounts or InstallationServiceId.MySqlAdminRecovery
        or InstallationServiceId.MySqlRemoteAccess or InstallationServiceId.MySqlMaintenance;

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
