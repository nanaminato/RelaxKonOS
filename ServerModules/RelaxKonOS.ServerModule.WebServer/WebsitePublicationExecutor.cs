using System.Net;
using System.Security.Authentication;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.WebsitePublishing;

namespace RelaxKonOS.Server.WebsitePublishing;

/// <summary>Executes one admitted publication and its compensation under the coordinator's work lease.</summary>
public sealed class WebsitePublicationExecutor(IWebsitePublicationPorts ports) : IWebsitePublicationExecutor
{
    public async Task RunAsync(Guid operationId, PublishWebsiteRequest request, string actor, CancellationToken stopping)
    {
        var checks = new List<WebsitePublicationCheckDto>();
        var bootstrapPending = false;
        try
        {
            stopping.ThrowIfCancellationRequested();
            StartRunning(operationId, WebsitePublicationStage.Preflight);
            var application = ports.RequireApplication(request.ApplicationId);
            var running = await ports.IsApplicationRunningAsync(request.ApplicationId, stopping);
            stopping.ThrowIfCancellationRequested();
            if (!running || application.HostPort is not { } hostPort)
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

            var server = (await ports.ListWebServersAsync(stopping)).SingleOrDefault(item => item.Id == request.WebServerId)
                ?? throw new WebsitePublicationException("website.webserver_not_found", 404);
            stopping.ThrowIfCancellationRequested();
            if (!server.Capabilities.CanRead || !server.Capabilities.CanTestConfiguration)
                throw new WebsitePublicationException("website.webserver_not_manageable", 409);

            if (application.SiteId is not null && !string.Equals(application.SiteInstanceId, server.Id, StringComparison.Ordinal))
                throw new WebsitePublicationException("website.application_site_server_conflict");
            if (application.SiteId is not null && ports.ReadApplications().Any(candidate => candidate.Id != application.Id && candidate.SiteId == application.SiteId))
                throw new WebsitePublicationException("website.site_in_use");
            var existingSites = await ports.ListSitesAsync(server.Id, stopping) ?? [];
            stopping.ThrowIfCancellationRequested();
            var existingByDomain = existingSites.FirstOrDefault(site => site.Bindings.Any(binding => string.Equals(binding.Domain, request.Domain, StringComparison.OrdinalIgnoreCase)));
            var existingSite = application.SiteId is null ? null : existingSites.FirstOrDefault(site => site.Id == application.SiteId);
            if (existingByDomain is not null && !string.Equals(existingByDomain.Id, application.SiteId, StringComparison.Ordinal))
                throw new WebsitePublicationException("website.domain_conflict");

            var siteId = application.SiteId ?? $"app{application.Id:N}";
            if (request.CertificateId is null && !ports.HasAcmeWebRoot(server)
                && !(existingSite is { AcmeHttp01: true } && existingSite.Bindings.Any(binding => binding.Domain == request.Domain && binding.Port == 80)))
            {
                if (!ports.SupportsBootstrap || application.SiteId is not null || application.BindAddress != "127.0.0.1")
                    throw new WebsitePublicationException("website.acme_webroot_required");
                var problem = await ports.PrepareWebRootAsync(server, operationId, siteId, request.Domain, () =>
                {
                    ports.Update(operationId, current => current with
                    {
                        SiteId = siteId,
                        RecoveryProblemCode = WebsitePublicationProblems.SiteApplicationUnverified,
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
                var failed = await ports.GetWebsiteIssueAsync(operationId, stopping);
                if (failed is { State: CertificateOperationState.Failed, CompletedAt: not null, ProblemCode: "certificate.validation_failed" }
                    && await TryCleanupBootstrapAsync(ports.Get(operationId)!, stopping))
                    bootstrapPending = false;
                throw;
            }
            var previousSite = existingSite;
            if (!bootstrapPending) SetStage(operationId, WebsitePublicationStage.Configuring, siteId: siteId, certificateId: certificateId);
            var fingerprint = bootstrapPending ? await ReadBootstrapFingerprintAsync(certificateId, request.Domain, stopping) : null;
            var site = await ApplySiteAsync(server.Id, siteId, application.Name, request.Domain, hostPort, certificateId,
                existingSite?.UpdatedAt, bootstrapPending ? operationId : null, stopping,
                fingerprint is null ? null : (planned, digest) => ports.RecordBootstrapBinding(operationId, new(planned, digest, fingerprint)));

            try
            {
                // Site application has already committed. Finish the association or its compensation
                // before honoring shutdown, so the live site and catalog cannot diverge.
                if (fingerprint is not null)
                    await ReconcileBootstrapBindingAsync(ports.Get(operationId)!, ports.GetBootstrapBinding(operationId)!,
                        application.Id, hostPort, stopping);
                else
                    ports.BindSite(application.Id, server.Id, site);
            }
            catch (Exception error) when (error is WebsiteAssociationException or IOException)
            {
                var rollback = await RestoreSiteAsync(server.Id, previousSite, site);
                Complete(operationId, WebsitePublicationState.Failed,
                    rollback ? "website.application_association_failed" : WebsitePublicationProblems.AssociationRecoveryFailed,
                    rollback ? null : "website.recheck_required", checks);
                return;
            }

            ports.Update(operationId, current => current with
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
                bootstrapPending ? WebsitePublicationProblems.SiteApplicationUnverified : null, checks);
        }
        catch (Exception error) when (error is not WebsiteCompletionException)
        {
            ports.LogFailure(operationId, "publication", error.GetType().Name);
            Complete(operationId, WebsitePublicationState.Failed, "website.operation_failed",
                bootstrapPending ? WebsitePublicationProblems.SiteApplicationUnverified : null, checks);
        }
    }

    public async Task<WebsitePublicationOperationDto> CleanupBootstrapAsync(Guid operationId, CancellationToken ct)
    {
        var operation = ports.Get(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
        if (operation.State is WebsitePublicationState.Queued or WebsitePublicationState.Running) throw new WebsitePublicationException("website.operation_conflict");
        if (operation.BootstrapCleanedAt is not null) return operation;
        if (operation.RecoveryProblemCode != WebsitePublicationProblems.SiteApplicationUnverified
            || operation.BootstrapResumeStartedAt is not null || operation.BootstrapBindingOperationId is not null)
            throw new WebsitePublicationException("website.recheck_required");
        var certificate = await ReadRecoveryCertificateAsync(operationId, ct);
        if (certificate is not null && (certificate.CompletedAt is null
                || certificate.State is CertificateOperationState.Queued or CertificateOperationState.Running)
            || operation.CertificateOperationId is { } expected && certificate?.OperationId != expected
            || !await TryCleanupBootstrapAsync(operation, ct))
            throw new WebsitePublicationException("website.recheck_required");
        return ports.Get(operationId)!;
    }

    public async Task<WebsitePublicationOperationDto> ResumeBootstrapAsync(Guid operationId, CancellationToken ct)
    {
        var operation = ports.Get(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
        if (operation.BootstrapResumedAt is not null) return operation;
        var plan = ports.GetBootstrapBinding(operationId);
        if (!ports.SupportsBootstrap || operation.State is not (WebsitePublicationState.Interrupted or WebsitePublicationState.Failed)
            || plan is null && (operation.State != WebsitePublicationState.Interrupted
                || operation.InterruptedFromStage != WebsitePublicationStage.Certificate || operation.BootstrapResumeStartedAt is not null)
            || operation.RecoveryProblemCode != WebsitePublicationProblems.SiteApplicationUnverified
            || operation.BootstrapCleanupOperationId is not null
            || operation.SiteId != $"app{operation.ApplicationId:N}"
            || operation.CertificateId is not { } certificateId || operation.CertificateOperationId is not { } issueId)
            throw new WebsitePublicationException("website.recheck_required");

        var certificate = await ReadRecoveryCertificateAsync(operationId, ct);
        if (certificate is not { State: CertificateOperationState.Succeeded, CompletedAt: not null }
            || certificate.CertificateId != certificateId || certificate.OperationId != issueId)
            throw new WebsitePublicationException("website.recheck_required");

        var application = ports.FindApplication(operation.ApplicationId);
        if (application is null || application.HostPort is not { } hostPort
            || application.BindAddress != "127.0.0.1"
            || application.SiteId is not null && (plan is null || !HasOriginalAssociation(application, operation))
            || ports.ReadApplications().Any(item => item.Id != application.Id && HasOriginalAssociation(item, operation))
            || !await ports.IsApplicationRunningAsync(operation.ApplicationId, ct))
            throw new WebsitePublicationException("website.recheck_required");

        if (plan is null)
        {
            var fingerprint = await ReadBootstrapFingerprintAsync(certificateId, operation.Domain, ct);
            await ApplySiteAsync(operation.WebServerId, operation.SiteId, application.Name, operation.Domain, hostPort,
                certificateId, null, operationId, ct,
                (site, digest) => ports.RecordBootstrapBinding(operationId, new(site, digest, fingerprint)));
            operation = ports.Get(operationId)!;
            plan = ports.GetBootstrapBinding(operationId)!;
        }
        var resumedAt = DateTimeOffset.UtcNow;
        await ReconcileBootstrapBindingAsync(operation, plan, application.Id, hostPort, ct);
        var checks = await VerifyAsync(operation.Domain, ct);
        return ports.Update(operationId, current => current with
        {
            BootstrapResumeStartedAt = current.BootstrapResumeStartedAt ?? resumedAt,
            BootstrapResumedAt = DateTimeOffset.UtcNow,
            RecoveryProblemCode = null,
            Checks = checks,
        });
    }

    private async Task<string> ReadBootstrapFingerprintAsync(Guid certificateId, string domain, CancellationToken ct)
    {
        var material = await ports.GetCertificateAsync(certificateId, ct);
        var now = DateTimeOffset.UtcNow;
        if (material is null || material.Status is not (CertificateStatus.Issued or CertificateStatus.Active)
            || material.NotBefore is null || material.NotBefore > now || material.NotAfter is null || material.NotAfter <= now
            || material.FingerprintSha256 is not { Length: > 0 } fingerprint
            || !ports.Covers(material.SubjectAlternativeNames, domain))
            throw new WebsitePublicationException("website.recheck_required");
        return fingerprint;
    }

    private async Task<CertificateOperationDto?> ReadRecoveryCertificateAsync(Guid operationId, CancellationToken ct)
    {
        try { return await ports.GetWebsiteIssueAsync(operationId, ct); }
        catch (WebsiteCertificateEvidenceUnavailableException) { throw new WebsitePublicationException("website.recheck_required"); }
    }

    private async Task ReconcileBootstrapBindingAsync(WebsitePublicationOperationDto operation, WebsiteBootstrapBinding plan,
        Guid applicationId, int hostPort, CancellationToken ct)
    {
        if (plan.Site.CertificateId is not { } certificateId
            || plan.CertificateFingerprint != await ReadBootstrapFingerprintAsync(certificateId, operation.Domain, ct)
            || plan.Site.Routes is not [{ Path: "/" } route] || route.Upstream != $"http://127.0.0.1:{hostPort}"
            || operation.BootstrapBindingOperationId is not { } bindingId)
            throw new WebsitePublicationException("website.recheck_required");

        var instance = (await ports.ListWebServersAsync(ct)).SingleOrDefault(item => item.Id == operation.WebServerId);
        if (instance is null) throw new WebsitePublicationException("website.recheck_required");
        try
        {
            if (!await ports.ReconcileBindingAsync(instance, operation, bindingId, plan, applicationId, hostPort, ct))
                throw new WebsitePublicationException("website.recheck_required");
        }
        catch (Exception error) when (error is WebsiteAssociationException or IOException or UnauthorizedAccessException)
        {
            ports.LogFailure(operation.OperationId, "bootstrap-association", error.GetType().Name);
            throw new WebsitePublicationException("website.recheck_required");
        }
    }

    private static bool HasOriginalAssociation(WebsiteApplication application, WebsitePublicationOperationDto operation)
        => application.SiteInstanceId == operation.WebServerId && application.SiteId == operation.SiteId;

    private async Task<WebServerSiteDto> ApplySiteAsync(string serverId, string siteId, string name, string domain, int hostPort,
        Guid certificateId, DateTimeOffset? expectedUpdatedAt, Guid? bootstrapId, CancellationToken ct,
        Func<WebServerSiteDto, string, Guid>? recordTrackedBinding = null)
    {
        var request = new UpsertWebServerSiteRequest(siteId, name, [new WebServerSiteBindingDto(domain, 80)], null, false, false,
            [new WebServerProxyRouteDto("/", $"http://127.0.0.1:{hostPort}")], certificateId, true, true, false,
            ExpectedUpdatedAt: expectedUpdatedAt);
        return await ports.UpsertSiteAsync(serverId, request, bootstrapId, ct, recordTrackedBinding)
            ?? throw new WebsitePublicationException("website.site_apply_failed");
    }

    private async Task<bool> TryCleanupBootstrapAsync(WebsitePublicationOperationDto operation, CancellationToken ct)
    {
        if (!ports.SupportsBootstrap || operation.SiteId != $"app{operation.ApplicationId:N}"
            || ports.ReadApplications().Any(application => application.SiteInstanceId == operation.WebServerId
                && application.SiteId == operation.SiteId)) return false;
        var instance = (await ports.ListWebServersAsync(ct)).SingleOrDefault(item => item.Id == operation.WebServerId);
        if (instance is null) return false;
        try
        {
            if (!await ports.CleanupWebRootAsync(instance, operation.OperationId, operation.SiteId, operation.Domain,
                operation.BootstrapCleanupOperationId, () => ports.Update(operation.OperationId, current => current with
                {
                    BootstrapCleanupOperationId = current.BootstrapCleanupOperationId ?? Guid.NewGuid(),
                }).BootstrapCleanupOperationId!.Value, ct))
                return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ports.LogFailure(operation.OperationId, "bootstrap-cleanup", error.GetType().Name);
            return false;
        }
        ports.Update(operation.OperationId, current => current with
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
            var certificate = await ports.GetCertificateAsync(existing, stopping);
            if (certificate is null) throw new WebsitePublicationException("website.certificate_not_found", 404);
            if (certificate.Status is not (CertificateStatus.Issued or CertificateStatus.Active) || certificate.NotBefore is null || certificate.NotAfter is null
                || certificate.NotBefore > DateTimeOffset.UtcNow || certificate.NotAfter <= DateTimeOffset.UtcNow)
                throw new WebsitePublicationException("website.certificate_not_usable", 409);
            if (!ports.Covers(certificate.SubjectAlternativeNames, request.Domain))
                throw new WebsitePublicationException("website.certificate_domain_mismatch", 409);
            SetStage(operationId, WebsitePublicationStage.Certificate, certificateId: existing);
            return existing;
        }

        SetStage(operationId, WebsitePublicationStage.Certificate);
        var issued = await ports.RequestCertificateAsync(operationId, new RequestCertificateRequest([request.Domain],
            CertificateChallengeType.WebRootHttp01, request.ContactEmail!, request.AcceptedTerms, CertificateKeyAlgorithm.EcdsaP256,
            request.PublicReachabilityConfirmed), actor, stopping);
        if (issued.OperationId == Guid.Empty) throw new WebsitePublicationException(issued.ProblemCode, 409);
        SetStage(operationId, WebsitePublicationStage.Certificate, certificateId: issued.CertificateId, certificateOperationId: issued.OperationId);

        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            var current = await ports.GetCertificateOperationAsync(issued.OperationId, stopping)
                ?? throw new WebsitePublicationException("website.certificate_operation_missing", 503);
            if (current.State == CertificateOperationState.Succeeded && current.CertificateId is { } certificateId) return certificateId;
            if (current.State is CertificateOperationState.Failed or CertificateOperationState.Cancelled)
                throw new WebsitePublicationException(current.ProblemCode is { Length: > 0 } problem ? problem : "website.certificate_failed", 409);
            await Task.Delay(TimeSpan.FromSeconds(1), stopping);
        }
    }

    private async Task<IReadOnlyList<WebsitePublicationCheckDto>> VerifyAsync(string domain, CancellationToken cancellationToken)
    {
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
            using var client = ports.CreateVerificationClient();
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
            if (previous is null) return await ports.DeleteSiteAsync(serverId, applied.Id, new DeleteWebServerSiteRequest(applied.UpdatedAt), CancellationToken.None) is true;
            var restored = await ports.UpsertSiteAsync(serverId, new UpsertWebServerSiteRequest(previous.Id, previous.Name, previous.Bindings,
                previous.RootPath, false, previous.SpaFallback, previous.Routes, previous.CertificateId, previous.HttpsEnabled,
                previous.RedirectHttpToHttps, previous.Ipv6Enabled, previous.CertificatePath, previous.PrivateKeyPath, applied.UpdatedAt), null, CancellationToken.None);
            return restored is not null;
        }
        catch { return false; }
    }

    private void StartRunning(Guid id, WebsitePublicationStage stage) => ports.Update(id, operation => operation with
    {
        State = WebsitePublicationState.Running, Stage = stage, StartedAt = DateTimeOffset.UtcNow,
    });

    private void SetStage(Guid id, WebsitePublicationStage stage, string? siteId = null, Guid? certificateId = null, Guid? certificateOperationId = null)
        => ports.Update(id, operation => operation with
        {
            Stage = stage, SiteId = siteId ?? operation.SiteId, CertificateId = certificateId ?? operation.CertificateId,
            CertificateOperationId = certificateOperationId ?? operation.CertificateOperationId,
        });

    private void Complete(Guid id, WebsitePublicationState state, string problem, string? recovery, IReadOnlyList<WebsitePublicationCheckDto> checks) =>
        ports.Complete(id, operation => operation with
        {
            State = state, Stage = state == WebsitePublicationState.Succeeded ? WebsitePublicationStage.Completed : WebsitePublicationStage.Failed,
            ProblemCode = problem, RecoveryProblemCode = recovery, Checks = checks, CompletedAt = DateTimeOffset.UtcNow,
        });
}
