using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.WebsitePublishing;

/// <summary>
/// Coordinates an application-to-Nginx publication as one durable operation. The HTTP request only
/// records intent; certificate issuance, site generation, application association, and host-observed
/// verification continue after the phone disconnects. A restart marks active work interrupted rather
/// than repeating an ACME request or overwriting a site whose final state cannot be known.
/// </summary>
internal sealed class WebsitePublicationCoordinator(
    WebsitePublicationStore store,
    ApplicationDeploymentManager applications,
    ApplicationDeploymentCatalogStore catalog,
    IWebServerManager webServers,
    ICertificateManager certificates,
    CertificateOperationStore certificateOperations,
    IHttpClientFactory httpClients,
    IHostApplicationLifetime lifetime,
    ILogger<WebsitePublicationCoordinator> logger) : IHostedService
{
    private readonly ConcurrentDictionary<Guid, (PublishWebsiteRequest Request, string Actor)> pending = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private volatile bool ready;

    public WebsitePublicationOperationDto Start(PublishWebsiteRequest request, string actor, string idempotencyKey)
    {
        if (request is null) throw new WebsitePublicationException("website.request_invalid", 400);
        request = request with
        {
            Domain = request.Domain?.Trim().TrimEnd('.').ToLowerInvariant() ?? "",
            ContactEmail = request.ContactEmail?.Trim(),
            WebServerId = request.WebServerId?.Trim() ?? "",
        };
        Validate(request, idempotencyKey);
        if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
            throw new WebsitePublicationException("website.operation_store_unavailable", 503);
        _ = applications.Require(request.ApplicationId);
        var operation = store.Start(request, actor, idempotencyKey, out var created);
        if (created)
        {
            pending[operation.OperationId] = (request, actor);
            running[operation.OperationId] = RunAsync(operation.OperationId, request, actor);
        }
        return operation;
    }

    public WebsitePublicationOperationDto? Get(Guid operationId) => store.Get(operationId);
    public IReadOnlyList<WebsitePublicationOperationDto> History(Guid applicationId, int limit) => store.History(applicationId, limit);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { _ = store.InterruptActive(); ready = true; }
        catch (WebsitePublicationException error) { logger.LogError(error, "Website publication ledger is unavailable at startup."); }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ready = false;
        try { await Task.WhenAll(running.Values).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(Guid operationId, PublishWebsiteRequest request, string actor)
    {
        var checks = new List<WebsitePublicationCheckDto>();
        try
        {
            StartRunning(operationId, WebsitePublicationStage.Preflight);
            var application = applications.Require(request.ApplicationId);
            var observed = await applications.SnapshotAsync(request.ApplicationId, lifetime.ApplicationStopping);
            if (observed.Application.ActualState != ApplicationActualState.Running || application.HostPort is not { } hostPort)
            {
                checks.Add(new("upstream", "host", WebsitePublicationCheckState.Failed, "website.application_not_ready", DateTimeOffset.UtcNow));
                throw new WebsitePublicationException("website.application_not_ready", 409);
            }
            if (!application.BindAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !application.BindAddress.Equals("::1", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new("upstream", "host", WebsitePublicationCheckState.Failed, "website.application_loopback_required", DateTimeOffset.UtcNow));
                throw new WebsitePublicationException("website.application_loopback_required", 409);
            }
            checks.Add(new("upstream", "host", WebsitePublicationCheckState.Passed, "", DateTimeOffset.UtcNow));

            var server = (await webServers.ListAsync(lifetime.ApplicationStopping)).SingleOrDefault(item => item.Id == request.WebServerId)
                ?? throw new WebsitePublicationException("website.webserver_not_found", 404);
            if (!server.Capabilities.CanRead || !server.Capabilities.CanTestConfiguration)
                throw new WebsitePublicationException("website.webserver_not_manageable", 409);

            if (application.SiteId is not null && !string.Equals(application.SiteInstanceId, server.Id, StringComparison.Ordinal))
                throw new WebsitePublicationException("website.application_site_server_conflict");
            if (application.SiteId is not null && catalog.ReadApplications().Any(candidate => candidate.Id != application.Id && candidate.SiteId == application.SiteId))
                throw new WebsitePublicationException("website.site_in_use");
            var existingSites = await webServers.ListSitesAsync(server.Id, lifetime.ApplicationStopping) ?? [];
            var existingByDomain = existingSites.FirstOrDefault(site => site.Bindings.Any(binding => string.Equals(binding.Domain, request.Domain, StringComparison.OrdinalIgnoreCase)));
            var existingSite = application.SiteId is null ? null : existingSites.FirstOrDefault(site => site.Id == application.SiteId);
            if (existingByDomain is not null && !string.Equals(existingByDomain.Id, application.SiteId, StringComparison.Ordinal))
                throw new WebsitePublicationException("website.domain_conflict");

            var siteId = application.SiteId ?? $"app{application.Id:N}";
            var certificateId = await ResolveCertificateAsync(operationId, request, actor);
            SetStage(operationId, WebsitePublicationStage.Configuring, siteId: siteId, certificateId: certificateId);

            var previousSite = existingSite;
            WebServerSiteDto site;
            try
            {
                site = await webServers.UpsertSiteAsync(server.Id, new UpsertWebServerSiteRequest(
                    siteId, application.Name, [new WebServerSiteBindingDto(request.Domain, 80)], null, false, false,
                    [new WebServerProxyRouteDto("/", $"http://127.0.0.1:{hostPort}")], certificateId, true, true, false, ExpectedUpdatedAt: existingSite?.UpdatedAt), lifetime.ApplicationStopping)
                    ?? throw new WebsitePublicationException("website.site_apply_failed");
            }
            catch (NginxWebServerManager.WebServerSiteValidationException error) { throw new WebsitePublicationException(error.ProblemCode, 400); }
            catch (NginxWebServerManager.WebServerSiteConflictException error) { throw new WebsitePublicationException(error.ProblemCode); }
            catch (NginxWebServerManager.WebServerSiteApplyException error) { throw new WebsitePublicationException(error.ProblemCode); }

            try
            {
                SetStage(operationId, WebsitePublicationStage.Associating, siteId: site.Id, certificateId: certificateId);
                catalog.BindSite(application.Id, server.Id, site.Id, site.DomainsDisplay);
            }
            catch (Exception error) when (error is ApplicationDeploymentException or IOException)
            {
                var rollback = await RestoreSiteAsync(server.Id, previousSite, site);
                throw new WebsitePublicationException(rollback ? "website.application_association_failed" : "website.application_association_recovery_failed", 503);
            }

            SetStage(operationId, WebsitePublicationStage.Verifying, siteId: site.Id, certificateId: certificateId);
            checks.AddRange(await VerifyAsync(request.Domain, lifetime.ApplicationStopping));
            Complete(operationId, WebsitePublicationState.Succeeded, "", null, checks);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Leave the durable operation active. Startup will mark it interrupted; no side effect is replayed.
        }
        catch (WebsitePublicationException error)
        {
            Complete(operationId, WebsitePublicationState.Failed, error.ProblemCode, null, checks);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Website publication failed. OperationId={OperationId}", operationId);
            Complete(operationId, WebsitePublicationState.Failed, "website.operation_failed", null, checks);
        }
        finally
        {
            pending.TryRemove(operationId, out _);
            running.TryRemove(operationId, out _);
        }
    }

    private async Task<Guid> ResolveCertificateAsync(Guid operationId, PublishWebsiteRequest request, string actor)
    {
        if (request.CertificateId is { } existing)
        {
            var certificate = await certificates.GetAsync(existing, lifetime.ApplicationStopping);
            if (certificate is null) throw new WebsitePublicationException("website.certificate_not_found", 404);
            if (certificate.Status is not (CertificateStatus.Issued or CertificateStatus.Active) || certificate.NotBefore is null || certificate.NotAfter is null
                || certificate.NotBefore > DateTimeOffset.UtcNow || certificate.NotAfter <= DateTimeOffset.UtcNow)
                throw new WebsitePublicationException("website.certificate_not_usable", 409);
            if (!CertificateUsagePolicy.Covers(certificate.SubjectAlternativeNames, request.Domain))
                throw new WebsitePublicationException("website.certificate_domain_mismatch", 409);
            SetStage(operationId, WebsitePublicationStage.Certificate, certificateId: existing);
            return existing;
        }

        SetStage(operationId, WebsitePublicationStage.Certificate);
        var issued = await certificates.RequestAsync($"website-{operationId:N}", new RequestCertificateRequest([request.Domain],
            CertificateChallengeType.DirectHttp01, request.ContactEmail!, request.AcceptedTerms, CertificateKeyAlgorithm.EcdsaP256,
            request.PublicReachabilityConfirmed), actor, lifetime.ApplicationStopping);
        if (issued.OperationId == Guid.Empty) throw new WebsitePublicationException(issued.ProblemCode, 409);
        SetStage(operationId, WebsitePublicationStage.Certificate, certificateId: issued.CertificateId, certificateOperationId: issued.OperationId);

        while (true)
        {
            lifetime.ApplicationStopping.ThrowIfCancellationRequested();
            var current = await certificateOperations.GetAsync(issued.OperationId, lifetime.ApplicationStopping)
                ?? throw new WebsitePublicationException("website.certificate_operation_missing", 503);
            if (current.State == CertificateOperationState.Succeeded && current.CertificateId is { } certificateId) return certificateId;
            if (current.State is CertificateOperationState.Failed or CertificateOperationState.Cancelled)
                throw new WebsitePublicationException(current.ProblemCode is { Length: > 0 } problem ? problem : "website.certificate_failed", 409);
            await Task.Delay(TimeSpan.FromSeconds(1), lifetime.ApplicationStopping);
        }
    }

    private async Task<IReadOnlyList<WebsitePublicationCheckDto>> VerifyAsync(string domain, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var checks = new List<WebsitePublicationCheckDto>();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken);
            checks.Add(new("dns", "host", addresses.Length > 0 ? WebsitePublicationCheckState.Passed : WebsitePublicationCheckState.Failed,
                addresses.Length > 0 ? "" : "website.dns_no_records", DateTimeOffset.UtcNow));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new("dns", "host", WebsitePublicationCheckState.Failed, "website.dns_lookup_failed", DateTimeOffset.UtcNow));
        }
        try
        {
            var client = httpClients.CreateClient("WebsitePublicationVerification");
            using var response = await client.GetAsync($"https://{domain}/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            checks.Add(new("tls", "host", WebsitePublicationCheckState.Passed, "", DateTimeOffset.UtcNow));
            var httpPassed = response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400;
            checks.Add(new("http", "host", httpPassed ? WebsitePublicationCheckState.Passed : WebsitePublicationCheckState.Failed,
                httpPassed ? "" : "website.http_status_failed", DateTimeOffset.UtcNow));
        }
        catch (AuthenticationException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new("tls", "host", WebsitePublicationCheckState.Failed, "website.tls_handshake_failed", DateTimeOffset.UtcNow));
            checks.Add(new("http", "host", WebsitePublicationCheckState.Unverified, "website.http_unverified", DateTimeOffset.UtcNow));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new("tls", "host", WebsitePublicationCheckState.Unverified, "website.tls_unverified", DateTimeOffset.UtcNow));
            checks.Add(new("http", "host", WebsitePublicationCheckState.Unverified, "website.http_unverified", DateTimeOffset.UtcNow));
        }
        return checks;
    }

    private async Task<bool> RestoreSiteAsync(string serverId, WebServerSiteDto? previous, WebServerSiteDto applied)
    {
        try
        {
            if (previous is null) return await webServers.DeleteSiteAsync(serverId, applied.Id, new DeleteWebServerSiteRequest(applied.UpdatedAt), CancellationToken.None) is true;
            var restored = await webServers.UpsertSiteAsync(serverId, new UpsertWebServerSiteRequest(previous.Id, previous.Name, previous.Bindings,
                previous.RootPath, false, previous.SpaFallback, previous.Routes, previous.CertificateId, previous.HttpsEnabled,
                previous.RedirectHttpToHttps, previous.Ipv6Enabled, previous.CertificatePath, previous.PrivateKeyPath, applied.UpdatedAt), CancellationToken.None);
            return restored is not null;
        }
        catch { return false; }
    }

    private void StartRunning(Guid id, WebsitePublicationStage stage) => store.Update(id, operation => operation with
    {
        State = WebsitePublicationState.Running, Stage = stage, StartedAt = DateTimeOffset.UtcNow,
    });

    private void SetStage(Guid id, WebsitePublicationStage stage, string? siteId = null, Guid? certificateId = null, Guid? certificateOperationId = null)
        => store.Update(id, operation => operation with
        {
            Stage = stage, SiteId = siteId ?? operation.SiteId, CertificateId = certificateId ?? operation.CertificateId,
            CertificateOperationId = certificateOperationId ?? operation.CertificateOperationId,
        });

    private void Complete(Guid id, WebsitePublicationState state, string problem, string? recovery, IReadOnlyList<WebsitePublicationCheckDto> checks)
    {
        try
        {
            _ = store.Update(id, operation => operation with
            {
                State = state, Stage = state == WebsitePublicationState.Succeeded ? WebsitePublicationStage.Completed : WebsitePublicationStage.Failed,
                ProblemCode = problem, RecoveryProblemCode = recovery, Checks = checks, CompletedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (WebsitePublicationException) { }
    }

    private static void Validate(PublishWebsiteRequest request, string key)
    {
        if (request is null || !request.Confirmed) throw new WebsitePublicationException("website.confirmation_required", 400);
        if (request.ApplicationId == Guid.Empty || string.IsNullOrWhiteSpace(request.WebServerId) || request.WebServerId.Length > 128)
            throw new WebsitePublicationException("website.request_invalid", 400);
        if (!Uri.CheckHostName(request.Domain?.Trim().TrimEnd('.') ?? "").Equals(UriHostNameType.Dns))
            throw new WebsitePublicationException("website.domain_invalid", 400);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(character => character < 33 || character > 126))
            throw new WebsitePublicationException("website.idempotency_required", 400);
        if (request.CertificateId is null && (string.IsNullOrWhiteSpace(request.ContactEmail) || !request.AcceptedTerms || !request.PublicReachabilityConfirmed))
            throw new WebsitePublicationException("website.certificate_request_incomplete", 400);
    }
}
