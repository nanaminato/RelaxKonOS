using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>
/// Typed client for the server's application-deployment domain. Every mutating call carries an
/// idempotency key, and every long action answers with a durable operation that this client can poll
/// after a reconnect instead of relying on an in-flight HTTP request.
/// </summary>
public interface IRemoteApplicationDeploymentClient
{
    Task<IReadOnlyList<ApplicationDeploymentTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationDto>> ListApplicationsAsync(CancellationToken cancellationToken = default);
    Task<ApplicationDeploymentSnapshotDto?> GetSnapshotAsync(Guid applicationId, CancellationToken cancellationToken = default);
    Task<ApplicationDto> CreateApplicationAsync(CreateApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<ApplicationDto> UpdateApplicationAsync(Guid applicationId, UpdateApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationRevisionDto>> ListRevisionsAsync(Guid applicationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeploymentOperationDto>> ListOperationsAsync(Guid applicationId, int limit = 50, CancellationToken cancellationToken = default);
    Task<DeploymentLogDto> GetLogsAsync(Guid applicationId, int tail = 200, CancellationToken cancellationToken = default);
    /// <summary>Output of the step that produced an operation's outcome, or null when it recorded none.
    /// It is what turns a bare "build failed" into something an operator can act on.</summary>
    Task<DeploymentOperationDiagnosticsDto?> GetOperationDiagnosticsAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task<DeploymentOperationDto> DeployAsync(Guid applicationId, DeployApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<DeploymentOperationDto> RollbackAsync(Guid applicationId, RollbackApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<DeploymentOperationDto> LifecycleAsync(Guid applicationId, string action, ApplicationLifecycleRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<DeploymentOperationDto> DeleteAsync(Guid applicationId, DeleteApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<DeploymentOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<DeploymentOperationDto?> GetActiveOperationAsync(Guid applicationId, CancellationToken cancellationToken = default);
    Task<DeploymentOperationDto> CancelOperationAsync(Guid operationId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Stages an archive and returns the reference the deployment request carries.</summary>
    Task<DeploymentStagedFileDto> UploadArchiveAsync(string fileName, Stream content, CancellationToken cancellationToken = default);
    /// <summary>Registers a file that already exists on the server. The host path never leaves this call.</summary>
    Task<DeploymentStagedFileDto> CreateFileReferenceAsync(string path, CancellationToken cancellationToken = default);
}
