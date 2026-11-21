using System.Net;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.AppSDK;

/// <summary>Transport operations used by the installation task workflow.</summary>
public interface IInstallationOperations
{
    Task<InstallationOperationDto?> StartAsync(InstallationServiceId service, InstallationOperationKind kind,
        object options, string key, CancellationToken cancellationToken);
    Task<InstallationOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<InstallationOperationDto?> GetActiveAsync(InstallationServiceId service, CancellationToken cancellationToken);
    Task<InstallationOperationDto?> CancelAsync(Guid id, CancellationToken cancellationToken);
}

public interface IInstallationPackageOperations : IInstallationOperations
{
    Task<InstallationFileReferenceDto?> CreateFileReferenceAsync(InstallationServiceId service, string path,
        CancellationToken cancellationToken);
    Task<InstallationFileReferenceDto?> UploadPackageAsync(InstallationServiceId service, string fileName,
        Stream content, CancellationToken cancellationToken);
    Task<InstallationFileReferenceDto?> DownloadAndUploadPackageAsync(InstallationServiceId service, string url,
        string fileName, CancellationToken cancellationToken);
}

public sealed class InstallationApiException(string code, HttpStatusCode status) : Exception(code)
{
    public string ProblemCode { get; } = code;
    public HttpStatusCode Status { get; } = status;
}
