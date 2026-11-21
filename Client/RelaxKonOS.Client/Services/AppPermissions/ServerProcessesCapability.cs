using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.SystemMonitor;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerProcessesCapability(AppId appId, IAppPermissionManager permissions,
    UsageMemoryScope connection, ITaskManagerClient client, IHostElevationBroker? broker) : IServerProcesses
{
    private readonly string? _serviceId = connection.Session.ServiceId;
    private IHostElevationBroker Elevation => broker ?? throw new InvalidOperationException("Host authorization is unavailable.");
    public Task<ProcessPageDto> QueryAsync(int page, int pageSize, string? filter, string? sort,
        bool descending, bool administratorMode, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.QueryProcessesAsync(page, pageSize, filter, sort, descending, administratorMode, cancellationToken),
            cancellationToken);

    public Task<(byte[] Data, string ContentType)?> GetIconAsync(int processId, DateTimeOffset startTime,
        bool administratorMode, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetProcessIconAsync(processId, startTime, administratorMode, cancellationToken), cancellationToken);

    public Task AuthorizeAdministratorAsync(CancellationToken cancellationToken)
    {
        EnsureAccess(cancellationToken);
        return Elevation.ExecuteAsync(HostElevationCapability.ProcessManage, "host/processes",
            () => ExecuteAsync(() => client.QueryProcessesAsync(pageSize: 1, elevated: true, ct: cancellationToken), cancellationToken),
            cancellationToken);
    }

    public Task<KillProcessResultDto> TerminateAsync(int processId, DateTimeOffset expectedStartTime,
        bool administratorMode, CancellationToken cancellationToken)
    {
        EnsureAccess(cancellationToken);
        return ProcessTerminationFlow.ExecuteAsync(administratorMode,
            elevated => ExecuteAsync(() => client.KillProcessAsync(processId, expectedStartTime, elevated, cancellationToken), cancellationToken),
            operation =>
            {
                EnsureAccess(cancellationToken);
                return Elevation.ExecuteAsync(HostElevationCapability.ProcessManage, "host/processes", operation, cancellationToken);
            },
            () => !cancellationToken.IsCancellationRequested && IsCurrent());
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        EnsureAccess(cancellationToken);
        var result = await operation();
        EnsureAccess(cancellationToken);
        return result;
    }

    private bool IsCurrent() => _serviceId is not null && connection.Session.ServiceId == _serviceId
        && connection.IsCurrent && connection.Session.State == AuthSessionState.Authenticated;

    private void EnsureAccess(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerProcessesManage))
            throw new UnauthorizedAccessException();
        if (!IsCurrent())
            throw new InvalidOperationException("The application's server connection has changed.");
    }
}
