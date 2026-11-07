using System.Net;
using System.Security.Authentication;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.WebsitePublishing;

/// <summary>Executes one admitted publication and its compensation under the coordinator's work lease.</summary>
internal sealed class WebsitePublicationExecution(
    WebsitePublicationStore store,
    ApplicationDeploymentManager applications,
    ApplicationDeploymentCatalogStore catalog,
    IWebServerManager webServers,
    ICertificateManager certificates,
    CertificateOperationStore certificateOperations,
    IHttpClientFactory httpClients,
    IHostApplicationLifetime lifetime,
    ILogger<WebsitePublicationExecution> logger,
    NginxWebServerManager? nginx = null)
{
    internal async Task RunAsync(Guid operationId, PublishWebsiteRequest request, string actor, CancellationToken workStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(workStopping, lifetime.ApplicationStopping);
        var stopping = linked.Token;
        var checks = new List<WebsitePublicationCheckDto>();
        var bootstrapPending = false;
        try
        {
            stopping.ThrowIfCancellationRequested();
            StartRunning(operationId, WebsitePublicationStage.Preflight);
            var application = applications.Require(request.ApplicationId);
            var observed = await applications.SnapshotAsync(request.ApplicationId, stopping);
            stopping.ThrowIfCancellationRequested();
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

            var server = (await webServers.ListAsync(stopping)).SingleOrDefault(item => item.Id == request.WebServerId)
                ?? throw new WebsitePublicationException("website.webserver_not_found", 404);
            stopping.ThrowIfCancellationRequested();
            if (!server.Capabilities.CanRead || !server.Capabilities.CanTestConfiguration)
                throw new WebsitePublicationException("website.webserver_not_manageable", 409);

            if (application.SiteId is not null && !string.Equals(application.SiteInstanceId, server.Id, StringComparison.Ordinal))
                throw new WebsitePublicationException("website.application_site_server_conflict");
            if (application.SiteId is not null && catalog.ReadApplications().Any(candidate => candidate.Id != application.Id && candidate.SiteId == application.SiteId))
                throw new WebsitePublicationException("website.site_in_use");
            var existingSites = await webServers.ListSitesAsync(server.Id, stopping) ?? [];
            stopping.ThrowIfCancellationRequested();
            var existingByDomain = existingSites.FirstOrDefault(site => site.Bindings.Any(binding => string.Equals(binding.Domain, request.Domain, StringComparison.OrdinalIgnoreCase)));
            var existingSite = application.SiteId is null ? null : existingSites.FirstOrDefault(site => site.Id == application.SiteId);
            if (existingByDomain is not null && !string.Equals(existingByDomain.Id, application.SiteId, StringComparison.Ordinal))
                throw new WebsitePublicationException("website.domain_conflict");

            var siteId = application.SiteId ?? $"app{application.Id:N}";
            if (request.CertificateId is null && !NginxWebServerManager.HasAcmeWebRoot(server)
                && !(existingSite is { AcmeHttp01: true } && existingSite.Bindings.Any(binding => binding.Domain == request.Domain && binding.Port == 80)))
            {
                if (nginx is null || application.SiteId is not null)
                    throw new WebsitePublicationException("website.acme_webroot_required");
                var problem = await nginx.PrepareWebsiteWebRootAsync(server, operationId, siteId, request.Domain, () =>
                {
                    store.Update(operationId, current => current with
                    {
                        SiteId = siteId,
                        RecoveryProblemCode = WebsitePublicationStore.SiteApplicationUnverified,
                    });
                    bootstrapPending = true;
                }, stopping);
                if (problem is not null) throw new WebsitePublicationException(problem);
            }
            Guid certificateId;
            try { certificateId = await ResolveCertificateAsync(operationId, request, actor, stopping); }
            catch (WebsitePublicationException error) when (bootstrapPending && error.ProblemCode == "certificate.validation_failed"
                && !stopping.IsCancellationRequested)
            {
                var failed = await certificateOperations.GetWebsiteIssueAsync(operationId, stopping);
                if (failed is { State: CertificateOperationState.Failed, CompletedAt: not null, ProblemCode: "certificate.validation_failed" }
                    && await TryCleanupBootstrapAsync(store.Get(operationId)!, stopping))
                    bootstrapPending = false;
                throw;
            }
            var previousSite = existingSite;
            void RecordBinding() => SetStage(operationId, WebsitePublicationStage.Configuring, siteId: siteId, certificateId: certificateId);
            if (!bootstrapPending) RecordBinding();
            var site = await ApplySiteAsync(server.Id, siteId, application.Name, request.Domain, hostPort, certificateId,
                existingSite?.UpdatedAt, bootstrapPending ? operationId : null, RecordBinding, stopping);

            try
            {
                // Site application has already committed. Finish the association or its compensation
                // before honoring shutdown, so the live site and catalog cannot diverge.
                catalog.BindSite(application.Id, server.Id, site.Id, site.DomainsDisplay);
            }
            catch (Exception error) when (error is ApplicationDeploymentException or IOException)
            {
                var rollback = await RestoreSiteAsync(server.Id, previousSite, site);
                Complete(operationId, WebsitePublicationState.Failed,
                    rollback ? "website.application_association_failed" : WebsitePublicationStore.AssociationRecoveryFailed,
                    rollback ? null : "website.recheck_required", checks);
                return;
            }

            store.Update(operationId, current => current with
            {
                Stage = WebsitePublicationStage.Associating, SiteId = site.Id, CertificateId = certificateId, RecoveryProblemCode = null,
            });
            bootstrapPending = false;
            stopping.ThrowIfCancellationRequested();
            SetStage(operationId, WebsitePublicationStage.Verifying, siteId: site.Id, certificateId: certificateId);
            checks.AddRange(await VerifyAsync(request.Domain, stopping));
            stopping.ThrowIfCancellationRequested();
            Complete(operationId, WebsitePublicationState.Succeeded, "", null, checks);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Leave the durable operation active. Startup will mark it interrupted; no side effect is replayed.
        }
        catch (WebsitePublicationException error)
        {
            Complete(operationId, WebsitePublicationState.Failed, error.ProblemCode,
                bootstrapPending ? WebsitePublicationStore.SiteApplicationUnverified : null, checks);
        }
        catch (Exception error) when (error is not ResidentWorkCompletionException)
        {
            logger.LogError(error, "Website publication failed. OperationId={OperationId}", operationId);
            Complete(operationId, WebsitePublicationState.Failed, "website.operation_failed",
                bootstrapPending ? WebsitePublicationStore.SiteApplicationUnverified : null, checks);
        }
    }

    internal async Task<WebsitePublicationOperationDto> CleanupBootstrapAsync(Guid operationId, CancellationToken ct)
    {
        var operation = store.Get(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
        if (WebsitePublicationStore.Active(operation)) throw new WebsitePublicationException("website.operation_conflict");
        if (operation.BootstrapCleanedAt is not null) return operation;
        if (operation.RecoveryProblemCode != WebsitePublicationStore.SiteApplicationUnverified || operation.BootstrapResumeStartedAt is not null)
            throw new WebsitePublicationException("website.recheck_required");
        CertificateOperationDto? certificate;
        try { certificate = await certificateOperations.GetWebsiteIssueAsync(operationId, ct); }
        catch (ModuleUnavailableException) { throw new WebsitePublicationException("website.recheck_required"); }
        if (certificate is not null && (certificate.CompletedAt is null
                || certificate.State is CertificateOperationState.Queued or CertificateOperationState.Running)
            || operation.CertificateOperationId is { } expected && certificate?.OperationId != expected
            || !await TryCleanupBootstrapAsync(operation, ct))
            throw new WebsitePublicationException("website.recheck_required");
        return store.Get(operationId)!;
    }

    internal async Task<WebsitePublicationOperationDto> ResumeBootstrapAsync(Guid operationId, CancellationToken ct)
    {
        var operation = store.Get(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
        if (operation.BootstrapResumedAt is not null) return operation;
        if (nginx is null || operation.State != WebsitePublicationState.Interrupted
            || operation.InterruptedFromStage != WebsitePublicationStage.Certificate
            || operation.RecoveryProblemCode != WebsitePublicationStore.SiteApplicationUnverified
            || operation.BootstrapCleanupOperationId is not null
            || operation.SiteId != $"app{operation.ApplicationId:N}"
            || operation.CertificateId is not { } certificateId || operation.CertificateOperationId is not { } issueId)
            throw new WebsitePublicationException("website.recheck_required");

        CertificateOperationDto? certificate;
        try { certificate = await certificateOperations.GetWebsiteIssueAsync(operationId, ct); }
        catch (ModuleUnavailableException) { throw new WebsitePublicationException("website.recheck_required"); }
        if (certificate is not { State: CertificateOperationState.Succeeded, CompletedAt: not null }
            || certificate.CertificateId != certificateId || certificate.OperationId != issueId)
            throw new WebsitePublicationException("website.recheck_required");

        var material = await certificates.GetAsync(certificateId, ct);
        var now = DateTimeOffset.UtcNow;
        if (material is null || material.Status is not (CertificateStatus.Issued or CertificateStatus.Active)
            || material.NotBefore is null || material.NotBefore > now || material.NotAfter is null || material.NotAfter <= now
            || material.FingerprintSha256 is not { Length: > 0 } fingerprint
            || !CertificateUsagePolicy.Covers(material.SubjectAlternativeNames, operation.Domain))
            throw new WebsitePublicationException("website.recheck_required");

        var plan = store.GetBootstrapBinding(operationId);
        if (operation.BootstrapResumeStartedAt is not null && plan is null)
            throw new WebsitePublicationException("website.recheck_required");
        var application = catalog.Find(operation.ApplicationId);
        if (application is null || application.HostPort is not { } hostPort
            || application.BindAddress != "127.0.0.1"
            || application.SiteId is not null && (plan is null || !HasOriginalAssociation(application, operation))
            || catalog.ReadApplications().Any(item => item.Id != application.Id && HasOriginalAssociation(item, operation))
            || (await applications.SnapshotAsync(operation.ApplicationId, ct)).Application.ActualState != ApplicationActualState.Running)
            throw new WebsitePublicationException("website.recheck_required");

        if (plan is null)
        {
            await ApplySiteAsync(operation.WebServerId, operation.SiteId, application.Name, operation.Domain, hostPort,
                certificateId, null, operationId, null, ct,
                (site, digest) => store.RecordBootstrapBinding(operationId, new(site, digest, fingerprint)));
            operation = store.Get(operationId)!;
            plan = store.GetBootstrapBinding(operationId)!;
        }
        if (plan.CertificateFingerprint != fingerprint
            || plan.Site.Routes is not [{ Path: "/" } route] || route.Upstream != $"http://127.0.0.1:{hostPort}"
            || operation.BootstrapBindingOperationId is not { } bindingId)
            throw new WebsitePublicationException("website.recheck_required");

        var instance = (await webServers.ListAsync(ct)).SingleOrDefault(item => item.Id == operation.WebServerId);
        if (instance is null) throw new WebsitePublicationException("website.recheck_required");
        try
        {
            if (!await nginx.ReconcileWebsiteBindingAsync(instance, operationId, bindingId, plan.Site, plan.ConfigurationSha256,
                hasBackup => catalog.Update(application.Id, current =>
                {
                    if (current.HostPort != hostPort || current.BindAddress != "127.0.0.1"
                        || catalog.ReadApplications().Any(item => item.Id != current.Id && HasOriginalAssociation(item, operation)))
                        throw new WebsitePublicationException("website.recheck_required");
                    if (HasOriginalAssociation(current, operation) && current.Domain == plan.Site.DomainsDisplay) return current;
                    if (!hasBackup || current.SiteId is not null)
                        throw new WebsitePublicationException("website.recheck_required");
                    return current with
                    {
                        SiteInstanceId = operation.WebServerId, SiteId = plan.Site.Id, Domain = plan.Site.DomainsDisplay,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                }), ct))
                throw new WebsitePublicationException("website.recheck_required");
        }
        catch (Exception error) when (error is ApplicationDeploymentException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Website bootstrap association remains unconfirmed. OperationId={OperationId}", operationId);
            throw new WebsitePublicationException("website.recheck_required");
        }
        var checks = await VerifyAsync(operation.Domain, ct);
        return store.Update(operationId, current => current with
        {
            BootstrapResumedAt = DateTimeOffset.UtcNow,
            RecoveryProblemCode = null,
            Checks = checks,
        });
    }

    private static bool HasOriginalAssociation(ApplicationRecord application, WebsitePublicationOperationDto operation)
        => application.SiteInstanceId == operation.WebServerId && application.SiteId == operation.SiteId;

    private async Task<WebServerSiteDto> ApplySiteAsync(string serverId, string siteId, string name, string domain, int hostPort,
        Guid certificateId, DateTimeOffset? expectedUpdatedAt, Guid? bootstrapId, Action? recordBinding, CancellationToken ct,
        Func<WebServerSiteDto, string, Guid>? recordTrackedBinding = null)
    {
        var request = new UpsertWebServerSiteRequest(siteId, name, [new WebServerSiteBindingDto(domain, 80)], null, false, false,
            [new WebServerProxyRouteDto("/", $"http://127.0.0.1:{hostPort}")], certificateId, true, true, false,
            ExpectedUpdatedAt: expectedUpdatedAt);
        try
        {
            return (bootstrapId.HasValue
                ? await nginx!.UpsertSiteAsync(serverId, request, bootstrapId, ct, recordBinding, recordTrackedBinding)
                : await webServers.UpsertSiteAsync(serverId, request, ct))
                ?? throw new WebsitePublicationException("website.site_apply_failed");
        }
        catch (NginxWebServerManager.WebServerSiteValidationException error) { throw new WebsitePublicationException(error.ProblemCode, 400); }
        catch (NginxWebServerManager.WebServerSiteConflictException error) { throw new WebsitePublicationException(error.ProblemCode); }
        catch (NginxWebServerManager.WebServerSiteApplyException error) { throw new WebsitePublicationException(error.ProblemCode); }
    }

    private async Task<bool> TryCleanupBootstrapAsync(WebsitePublicationOperationDto operation, CancellationToken ct)
    {
        if (nginx is null || operation.SiteId != $"app{operation.ApplicationId:N}"
            || catalog.ReadApplications().Any(application => application.SiteInstanceId == operation.WebServerId
                && application.SiteId == operation.SiteId)) return false;
        var instance = (await webServers.ListAsync(ct)).SingleOrDefault(item => item.Id == operation.WebServerId);
        if (instance is null) return false;
        try
        {
            if (!await nginx.CleanupWebsiteWebRootAsync(instance, operation.OperationId, operation.SiteId, operation.Domain,
                operation.BootstrapCleanupOperationId, () => store.Update(operation.OperationId, current => current with
                {
                    BootstrapCleanupOperationId = current.BootstrapCleanupOperationId ?? Guid.NewGuid(),
                }).BootstrapCleanupOperationId!.Value, ct))
                return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Website bootstrap cleanup remains unconfirmed. OperationId={OperationId}", operation.OperationId);
            return false;
        }
        store.Update(operation.OperationId, current => current with
        {
            BootstrapCleanedAt = DateTimeOffset.UtcNow,
            RecoveryProblemCode = null,
        });
        return true;
    }

    private async Task<Guid> ResolveCertificateAsync(Guid operationId, PublishWebsiteRequest request, string actor, CancellationToken stopping)
    {
        if (request.CertificateId is { } existing)
        {
            var certificate = await certificates.GetAsync(existing, stopping);
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
            CertificateChallengeType.WebRootHttp01, request.ContactEmail!, request.AcceptedTerms, CertificateKeyAlgorithm.EcdsaP256,
            request.PublicReachabilityConfirmed), actor, stopping, websitePublicationId: operationId);
        if (issued.OperationId == Guid.Empty) throw new WebsitePublicationException(issued.ProblemCode, 409);
        SetStage(operationId, WebsitePublicationStage.Certificate, certificateId: issued.CertificateId, certificateOperationId: issued.OperationId);

        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            var current = await certificateOperations.GetAsync(issued.OperationId, stopping)
                ?? throw new WebsitePublicationException("website.certificate_operation_missing", 503);
            if (current.State == CertificateOperationState.Succeeded && current.CertificateId is { } certificateId) return certificateId;
            if (current.State is CertificateOperationState.Failed or CertificateOperationState.Cancelled)
                throw new WebsitePublicationException(current.ProblemCode is { Length: > 0 } problem ? problem : "website.certificate_failed", 409);
            await Task.Delay(TimeSpan.FromSeconds(1), stopping);
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
            using var client = httpClients.CreateClient("WebsitePublicationVerification");
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
        catch (Exception error) { throw new ResidentWorkCompletionException(error); }
    }
}
