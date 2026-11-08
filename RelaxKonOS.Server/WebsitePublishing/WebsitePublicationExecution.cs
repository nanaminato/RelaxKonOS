using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.WebsitePublishing;

/// <summary>Owns the admitted executor for the entire request or background scope.</summary>
internal sealed class WebsitePublicationExecution : IDisposable
{
    private readonly WebsitePublicationCode.Lease lease;
    private readonly IHostApplicationLifetime lifetime;

    public WebsitePublicationExecution(WebsitePublicationStore store, ApplicationDeploymentManager applications,
        ApplicationDeploymentCatalogStore catalog, IWebServerManager webServers, ICertificateManager certificates,
        CertificateOperationStore certificateOperations, IHttpClientFactory httpClients, IHostApplicationLifetime lifetime,
        ILogger<WebsitePublicationExecution> logger, NginxWebServerManager? nginx = null)
        : this(store, applications, catalog, webServers, certificates, certificateOperations, httpClients, lifetime,
            logger, WebsitePublicationCode.Shared, nginx) { }

    internal WebsitePublicationExecution(WebsitePublicationStore store, ApplicationDeploymentManager applications,
        ApplicationDeploymentCatalogStore catalog, IWebServerManager webServers, ICertificateManager certificates,
        CertificateOperationStore certificateOperations, IHttpClientFactory httpClients, IHostApplicationLifetime lifetime,
        ILogger<WebsitePublicationExecution> logger, WebsitePublicationCode code, NginxWebServerManager? nginx = null)
    {
        this.lifetime = lifetime;
        lease = code.Acquire(new Ports(store, applications, catalog, webServers,
            certificates, certificateOperations, httpClients, logger, nginx));
    }

    internal async Task RunAsync(Guid operationId, PublishWebsiteRequest request, string actor, CancellationToken workStopping)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(workStopping, lifetime.ApplicationStopping);
        await InvokeAsync(async executor => { await executor.RunAsync(operationId, request, actor, stopping.Token); return true; }, stopping.Token);
    }

    internal Task<WebsitePublicationOperationDto> CleanupBootstrapAsync(Guid operationId, CancellationToken ct) =>
        InvokeAsync(executor => executor.CleanupBootstrapAsync(operationId, ct), ct);

    internal Task<WebsitePublicationOperationDto> ResumeBootstrapAsync(Guid operationId, CancellationToken ct) =>
        InvokeAsync(executor => executor.ResumeBootstrapAsync(operationId, ct), ct);

    private async Task<T> InvokeAsync<T>(Func<IWebsitePublicationExecutor, Task<T>> execute, CancellationToken ct)
    {
        try { return await execute(lease.Executor); }
        catch (WebsiteCompletionException) { throw new ResidentWorkCompletionException(new InvalidOperationException("Website publication terminal persistence failed.")); }
        catch (WebsitePublicationException error) { throw new WebsitePublicationException(error.ProblemCode, error.StatusCode); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception) { throw new InvalidOperationException("Website publication execution failed."); }
    }

    public void Dispose() => lease.Dispose();

    private sealed class Ports(WebsitePublicationStore store, ApplicationDeploymentManager applications,
        ApplicationDeploymentCatalogStore catalog, IWebServerManager webServers, ICertificateManager certificates,
        CertificateOperationStore certificateOperations, IHttpClientFactory httpClients, ILogger logger,
        NginxWebServerManager? nginx) : IWebsitePublicationPorts
    {
        public WebsitePublicationOperationDto? Get(Guid operationId) => store.Get(operationId);
        public WebsitePublicationOperationDto Update(Guid operationId, Func<WebsitePublicationOperationDto, WebsitePublicationOperationDto> update) =>
            store.Update(operationId, update);
        public void Complete(Guid operationId, Func<WebsitePublicationOperationDto, WebsitePublicationOperationDto> update)
        {
            try { store.Update(operationId, update); }
            catch (Exception error)
            {
                LogFailure(operationId, "terminal-persistence", error.GetType().Name);
                throw new WebsiteCompletionException();
            }
        }
        public WebsiteBootstrapBinding? GetBootstrapBinding(Guid operationId) => store.GetBootstrapBinding(operationId);
        public Guid RecordBootstrapBinding(Guid operationId, WebsiteBootstrapBinding binding) => store.RecordBootstrapBinding(operationId, binding);

        private static WebsiteApplication Project(ApplicationRecord item) => new(item.Id, item.Name, item.HostPort, item.BindAddress, item.SiteInstanceId, item.SiteId);
        public WebsiteApplication RequireApplication(Guid id) => Project(applications.Require(id));
        public WebsiteApplication? FindApplication(Guid id) => catalog.Find(id) is { } item ? Project(item) : null;
        public IReadOnlyList<WebsiteApplication> ReadApplications() => catalog.ReadApplications().Select(Project).ToArray();
        public async Task<bool> IsApplicationRunningAsync(Guid id, CancellationToken ct) =>
            (await applications.SnapshotAsync(id, ct)).Application.ActualState == ApplicationActualState.Running;
        public void BindSite(Guid applicationId, string instanceId, WebServerSiteDto site)
        {
            try { catalog.BindSite(applicationId, instanceId, site.Id, site.DomainsDisplay); }
            catch (ApplicationDeploymentException) { throw new WebsiteAssociationException(); }
        }

        public Task<IReadOnlyList<WebServerDto>> ListWebServersAsync(CancellationToken ct) => webServers.ListAsync(ct);
        public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken ct) => webServers.ListSitesAsync(instanceId, ct);
        public async Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, Guid? bootstrapId,
            CancellationToken ct, Func<WebServerSiteDto, string, Guid>? recordTrackedBinding = null)
        {
            try
            {
                return bootstrapId.HasValue
                    ? await nginx!.UpsertSiteAsync(instanceId, request, bootstrapId, ct, recordTrackedBinding)
                    : await webServers.UpsertSiteAsync(instanceId, request, ct);
            }
            catch (NginxWebServerManager.WebServerSiteValidationException error) { throw new WebsitePublicationException(error.ProblemCode, 400); }
            catch (NginxWebServerManager.WebServerSiteConflictException error) { throw new WebsitePublicationException(error.ProblemCode); }
            catch (NginxWebServerManager.WebServerSiteApplyException error) { throw new WebsitePublicationException(error.ProblemCode); }
        }
        public Task<bool?> DeleteSiteAsync(string instanceId, string siteId, DeleteWebServerSiteRequest request, CancellationToken ct) =>
            webServers.DeleteSiteAsync(instanceId, siteId, request, ct);
        public bool HasAcmeWebRoot(WebServerDto instance) => NginxWebServerManager.HasAcmeWebRoot(instance);
        public bool SupportsBootstrap => nginx is not null;
        public Task<string?> PrepareWebRootAsync(WebServerDto instance, Guid operationId, string siteId, string domain, Action checkpoint, CancellationToken ct) =>
            nginx!.PrepareWebsiteWebRootAsync(instance, operationId, siteId, domain, checkpoint, ct);
        public Task<bool> CleanupWebRootAsync(WebServerDto instance, Guid operationId, string siteId, string domain, Guid? cleanupId,
            Func<Guid> checkpoint, CancellationToken ct) => nginx!.CleanupWebsiteWebRootAsync(instance, operationId, siteId, domain, cleanupId, checkpoint, ct);

        public async Task<bool> ReconcileBindingAsync(WebServerDto instance, WebsitePublicationOperationDto operation, Guid bindingId,
            WebsiteBootstrapBinding plan, Guid applicationId, int hostPort, CancellationToken ct)
        {
            try
            {
                return await nginx!.ReconcileWebsiteBindingAsync(instance, operation.OperationId, bindingId, plan.Site, plan.ConfigurationSha256,
                    hasBackup => catalog.Update(applicationId, current =>
                    {
                        bool HasOriginalAssociation(ApplicationRecord item) => item.SiteInstanceId == operation.WebServerId && item.SiteId == operation.SiteId;
                        if (current.HostPort != hostPort || current.BindAddress != "127.0.0.1"
                            || catalog.ReadApplications().Any(item => item.Id != current.Id && HasOriginalAssociation(item)))
                            throw new WebsitePublicationException("website.recheck_required");
                        if (HasOriginalAssociation(current) && current.Domain == plan.Site.DomainsDisplay) return current;
                        if (!hasBackup || current.SiteId is not null)
                            throw new WebsitePublicationException("website.recheck_required");
                        return current with
                        {
                            SiteInstanceId = operation.WebServerId, SiteId = plan.Site.Id, Domain = plan.Site.DomainsDisplay,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        };
                    }), ct);
            }
            catch (ApplicationDeploymentException) { throw new WebsiteAssociationException(); }
        }

        public Task<CertificateDto?> GetCertificateAsync(Guid id, CancellationToken ct) => certificates.GetAsync(id, ct);
        public Task<CertificateOperationDto?> GetCertificateOperationAsync(Guid id, CancellationToken ct) => certificateOperations.GetAsync(id, ct);
        public async Task<CertificateOperationDto?> GetWebsiteIssueAsync(Guid operationId, CancellationToken ct)
        {
            try { return await certificateOperations.GetWebsiteIssueAsync(operationId, ct); }
            catch (ModuleUnavailableException) { throw new WebsiteCertificateEvidenceUnavailableException(); }
        }
        public Task<CertificateOperationDto> RequestCertificateAsync(Guid operationId, RequestCertificateRequest request, string actor, CancellationToken ct) =>
            certificates.RequestAsync($"website-{operationId:N}", request, actor, ct, websitePublicationId: operationId);
        public bool Covers(IReadOnlyList<string> names, string domain) => CertificateUsagePolicy.Covers(names, domain);
        public HttpClient CreateVerificationClient() => httpClients.CreateClient("WebsitePublicationVerification");
        public void LogFailure(Guid operationId, string phase, string errorType) =>
            logger.LogWarning("Website publication failure. OperationId={OperationId} Phase={Phase} ErrorType={ErrorType}", operationId, phase, errorType);
    }
}
