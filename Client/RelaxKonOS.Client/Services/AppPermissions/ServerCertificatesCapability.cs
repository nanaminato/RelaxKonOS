using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Certificates;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerCertificatesCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerCertificates? client) : IServerCertificates
{
    private IServerCertificates Client => client ?? throw new InvalidOperationException("The certificate client is unavailable.");

    public Task<IReadOnlyList<CertificateRenewalRunDto>> ListRenewalRunsAsync(CancellationToken token) =>
        RunAsync(() => Client.ListRenewalRunsAsync(token), false, token);

    public Task<IReadOnlyList<CertificateRenewalAttemptDto>> GetRenewalHistoryAsync(Guid id, CancellationToken token) =>
        RunAsync(() => Client.GetRenewalHistoryAsync(id, token), false, token);

    public Task<IReadOnlyList<CertificateDto>> ListAsync(CancellationToken token) =>
        RunAsync(() => Client.ListAsync(token), false, token);

    public Task<CertificateDto?> GetAsync(Guid id, CancellationToken token) =>
        RunAsync(() => Client.GetAsync(id, token), false, token);

    public Task<CertificatePreflightResultDto> PreflightAsync(CertificatePreflightRequest request, CancellationToken token) =>
        RunAsync(() => Client.PreflightAsync(request, token), true, token);

    public Task<CertificateOperationDto> RequestAsync(RequestCertificateRequest request, CancellationToken token) =>
        RunAsync(() => Client.RequestAsync(request, token), true, token);

    public Task<CertificateOperationDto> CreateSelfSignedAsync(CreateSelfSignedCertificateRequest request, CancellationToken token) =>
        RunAsync(() => Client.CreateSelfSignedAsync(request, token), true, token);

    public Task<KestrelCertificateDeploymentDto> GetKestrelDeploymentAsync(Guid id, CancellationToken token) =>
        RunAsync(() => Client.GetKestrelDeploymentAsync(id, token), false, token);

    public Task<CertificateOperationDto> DeployKestrelAsync(Guid id, CancellationToken token) =>
        RunAsync(() => Client.DeployKestrelAsync(id, token), true, token);

    public Task<CertificateOperationDto> RenewAsync(Guid id, CancellationToken token) =>
        RunAsync(() => Client.RenewAsync(id, token), true, token);

    public Task<CertificateOperationDto> RevokeAsync(Guid id, RevokeCertificateRequest request, CancellationToken token) =>
        RunAsync(() => Client.RevokeAsync(id, request, token), true, token);

    public Task<CertificateOperationDto> DeleteAsync(Guid id, DeleteCertificateRequest request, CancellationToken token) =>
        RunAsync(() => Client.DeleteAsync(id, request, token), true, token);

    public Task<CertificateOperationDto?> GetOperationAsync(Guid operationId, CancellationToken token) =>
        RunAsync(() => Client.GetOperationAsync(operationId, token), false, token);

    public Task<CertificateOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken token) =>
        RunAsync(() => Client.CancelOperationAsync(operationId, token), true, token);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, bool manage, CancellationToken token)
    {
        CheckAccess(manage, token);
        var result = await operation();
        CheckAccess(manage, token);
        return result;
    }

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerCertificatesRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerCertificatesManage))
            throw new UnauthorizedAccessException();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's certificate context has changed.");
    }
}
