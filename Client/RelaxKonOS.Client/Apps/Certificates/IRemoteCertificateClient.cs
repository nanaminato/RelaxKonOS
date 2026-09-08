using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Client.Apps.Certificates;

/// <summary>
/// Client-side facade for the host-global TLS certificate API. Long-running operations
/// (request/renew/deploy/revoke/delete) return an operation id that is polled to completion;
/// the API never exposes private keys or ACME account material.
/// </summary>
public interface IRemoteCertificateClient
{
    Task<IReadOnlyList<CertificateDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<CertificateDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CertificatePreflightResultDto> PreflightAsync(CertificatePreflightRequest request, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto> RequestAsync(RequestCertificateRequest request, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto> CreateSelfSignedAsync(CreateSelfSignedCertificateRequest request, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto> DeployKestrelAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto> RenewAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto> RevokeAsync(Guid id, RevokeCertificateRequest request, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto> DeleteAsync(Guid id, DeleteCertificateRequest request, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<CertificateOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
}
