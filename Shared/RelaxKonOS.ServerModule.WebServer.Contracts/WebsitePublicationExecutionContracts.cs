using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.WebsitePublishing;

namespace RelaxKonOS.Server.WebsitePublishing;

public sealed class WebsitePublicationException(string problemCode, int statusCode = 409) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}

public static class WebsitePublicationProblems
{
    public const string SiteApplicationUnverified = "website.site_application_unverified";
    public const string AssociationRecoveryFailed = "website.application_association_recovery_failed";
}

public sealed class WebsiteAssociationException() : Exception("Website application association failed.");
public sealed class WebsiteCompletionException() : Exception("Website publication terminal persistence failed.");
public sealed class WebsiteCertificateEvidenceUnavailableException() : Exception("Certificate evidence is unavailable.");

public sealed record WebsiteApplication(Guid Id, string Name, int? HostPort, string BindAddress, string? SiteInstanceId, string? SiteId);
public sealed record WebsiteBootstrapBinding(WebServerSiteDto Site, string ConfigurationSha256, string CertificateFingerprint);

public interface IWebsitePublicationExecutor
{
    Task RunAsync(Guid operationId, PublishWebsiteRequest request, string actor, CancellationToken stopping);
    Task<WebsitePublicationOperationDto> CleanupBootstrapAsync(Guid operationId, CancellationToken ct);
    Task<WebsitePublicationOperationDto> ResumeBootstrapAsync(Guid operationId, CancellationToken ct);
}

public interface IWebsitePublicationPorts
{
    WebsitePublicationOperationDto? Get(Guid operationId);
    WebsitePublicationOperationDto Update(Guid operationId, Func<WebsitePublicationOperationDto, WebsitePublicationOperationDto> update);
    void Complete(Guid operationId, Func<WebsitePublicationOperationDto, WebsitePublicationOperationDto> update);
    WebsiteBootstrapBinding? GetBootstrapBinding(Guid operationId);
    Guid RecordBootstrapBinding(Guid operationId, WebsiteBootstrapBinding binding);

    WebsiteApplication RequireApplication(Guid id);
    WebsiteApplication? FindApplication(Guid id);
    IReadOnlyList<WebsiteApplication> ReadApplications();
    Task<bool> IsApplicationRunningAsync(Guid id, CancellationToken ct);
    void BindSite(Guid applicationId, string instanceId, WebServerSiteDto site);

    Task<IReadOnlyList<WebServerDto>> ListWebServersAsync(CancellationToken ct);
    Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken ct);
    Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, Guid? bootstrapId,
        CancellationToken ct, Func<WebServerSiteDto, string, Guid>? recordTrackedBinding = null);
    Task<bool?> DeleteSiteAsync(string instanceId, string siteId, DeleteWebServerSiteRequest request, CancellationToken ct);
    bool HasAcmeWebRoot(WebServerDto instance);
    bool SupportsBootstrap { get; }
    Task<string?> PrepareWebRootAsync(WebServerDto instance, Guid operationId, string siteId, string domain, Action checkpoint, CancellationToken ct);
    Task<bool> CleanupWebRootAsync(WebServerDto instance, Guid operationId, string siteId, string domain, Guid? cleanupId,
        Func<Guid> checkpoint, CancellationToken ct);
    Task<bool> ReconcileBindingAsync(WebServerDto instance, WebsitePublicationOperationDto operation, Guid bindingId,
        WebsiteBootstrapBinding plan, Guid applicationId, int hostPort, CancellationToken ct);

    Task<CertificateDto?> GetCertificateAsync(Guid id, CancellationToken ct);
    Task<CertificateOperationDto?> GetCertificateOperationAsync(Guid id, CancellationToken ct);
    Task<CertificateOperationDto?> GetWebsiteIssueAsync(Guid operationId, CancellationToken ct);
    Task<CertificateOperationDto> RequestCertificateAsync(Guid operationId, RequestCertificateRequest request, string actor, CancellationToken ct);
    bool Covers(IReadOnlyList<string> names, string domain);
    HttpClient CreateVerificationClient();
    void LogFailure(Guid operationId, string phase, string errorType);
}
