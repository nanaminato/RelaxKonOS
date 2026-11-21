using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Hubs;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerApplicationDeploymentsCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerApplicationDeployments? client, CancellationToken lifetime)
    : IServerApplicationDeployments
{
    private IServerApplicationDeployments Client =>
        client ?? throw new InvalidOperationException("Application deployments are unavailable.");

    public Task<IReadOnlyList<ApplicationDeploymentTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ListTemplatesAsync(token), manage: false, cancellationToken);

    public Task<IReadOnlyList<ApplicationCatalogTemplateDto>> ListCatalogAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ListCatalogAsync(token), manage: false, cancellationToken);

    public Task<CatalogApplicationTopologyDto> PreviewCatalogTopologyAsync(string templateId, string templateVersion, int hostPort,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.PreviewCatalogTopologyAsync(templateId, templateVersion, hostPort, token), manage: false, cancellationToken);

    public Task<CatalogApplicationInstallDto> InstallCatalogAsync(InstallCatalogApplicationRequest request, string key, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.InstallCatalogAsync(request, key, token), manage: true, cancellationToken);

    public Task<ApplicationImageTagsDto> ListImageTagsAsync(string repository, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ListImageTagsAsync(repository, token), manage: false, cancellationToken);

    public Task<IReadOnlyList<ApplicationDto>> ListApplicationsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ListApplicationsAsync(token), manage: false, cancellationToken);

    public Task<ApplicationDeploymentSnapshotDto?> GetSnapshotAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.GetSnapshotAsync(applicationId, token), manage: false, cancellationToken);

    public Task<ApplicationDto> CreateApplicationAsync(CreateApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.CreateApplicationAsync(request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<ApplicationDto> UpdateApplicationAsync(Guid applicationId, UpdateApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.UpdateApplicationAsync(applicationId, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<IReadOnlyList<ApplicationRevisionDto>> ListRevisionsAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ListRevisionsAsync(applicationId, token), manage: false, cancellationToken);

    public Task<IReadOnlyList<DeploymentOperationDto>> ListOperationsAsync(Guid applicationId, int limit = 50, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ListOperationsAsync(applicationId, limit, token), manage: false, cancellationToken);

    public Task<DeploymentLogDto> GetLogsAsync(Guid applicationId, int tail = 200, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.GetLogsAsync(applicationId, tail, token), manage: false, cancellationToken);

    public Task<DeploymentOperationDiagnosticsDto?> GetOperationDiagnosticsAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.GetOperationDiagnosticsAsync(operationId, token), manage: false, cancellationToken);

    public Task<DeploymentOperationDto> DeployAsync(Guid applicationId, DeployApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.DeployAsync(applicationId, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<CatalogApplicationUpdatePreviewDto> PreviewCatalogUpdateAsync(Guid applicationId, string templateVersion, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.PreviewCatalogUpdateAsync(applicationId, templateVersion, token), manage: false, cancellationToken);

    public Task<DeploymentOperationDto> UpdateCatalogAsync(Guid applicationId, UpdateCatalogApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.UpdateCatalogAsync(applicationId, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<DeploymentOperationDto> RollbackAsync(Guid applicationId, RollbackApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.RollbackAsync(applicationId, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<DeploymentOperationDto> ReinstallCatalogAsync(Guid applicationId, ReinstallCatalogApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.ReinstallCatalogAsync(applicationId, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<DeploymentOperationDto> LifecycleAsync(Guid applicationId, string action, ApplicationLifecycleRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.LifecycleAsync(applicationId, action, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<DeploymentOperationDto> DeleteAsync(Guid applicationId, DeleteApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.DeleteAsync(applicationId, request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<DeploymentOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.GetOperationAsync(operationId, token), manage: false, cancellationToken);

    public Task<DeploymentOperationDto?> GetActiveOperationAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.GetActiveOperationAsync(applicationId, token), manage: false, cancellationToken);

    public Task<DeploymentOperationDto> CancelOperationAsync(Guid operationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.CancelOperationAsync(operationId, idempotencyKey, token), manage: true, cancellationToken);

    public Task<DeploymentStagedFileDto> UploadArchiveAsync(string fileName, Stream content, IProgress<DeploymentUploadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.UploadArchiveAsync(fileName, content, progress, token), manage: true, cancellationToken);

    public Task<DeploymentStagedFileDto> CreateFileReferenceAsync(string path, CancellationToken cancellationToken = default) =>
        InvokeAsync(token => Client.CreateFileReferenceAsync(path, token), manage: true, cancellationToken);

    public IAsyncDisposable WatchLogs(Guid operationId, Action<DeploymentLiveLogSnapshot> receive,
        Action<bool> connectionChanged, CancellationToken cancellationToken)
    {
        var tokens = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var token = tokens.Token;
        IDisposable? observation = null;
        try
        {
            CheckAccess(manage: false, token);
            observation = connection.ObserveInvalidation(tokens.Cancel);
            var stream = Client.WatchLogs(operationId,
                snapshot => { if (CanRead(token)) receive(snapshot); },
                connected => { if (CanRead(token)) connectionChanged(connected); }, token);
            return new LogSubscription(stream, observation, tokens);
        }
        catch
        {
            observation?.Dispose();
            tokens.Dispose();
            throw;
        }
    }

    private async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation, bool manage, CancellationToken cancellationToken)
    {
        using var tokens = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        CheckAccess(manage, tokens.Token);
        using var observation = connection.ObserveInvalidation(tokens.Cancel);
        tokens.Token.ThrowIfCancellationRequested();
        var result = await operation(tokens.Token);
        CheckAccess(manage, tokens.Token);
        return result;
    }

    private bool CanRead(CancellationToken token) => !token.IsCancellationRequested && connection.IsCurrent
        && permissions.IsGranted(appId, CoreAppPermissions.ServerApplicationDeploymentsRead);

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!connection.IsCurrent) throw new InvalidOperationException("The application's deployment connection has changed.");
        if (!CanRead(token) || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerApplicationDeploymentsManage))
            throw new UnauthorizedAccessException();
    }

    private sealed class LogSubscription(IAsyncDisposable stream, IDisposable observation, CancellationTokenSource tokens) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await stream.DisposeAsync(); }
            finally
            {
                observation.Dispose();
                tokens.Dispose();
            }
        }
    }
}
