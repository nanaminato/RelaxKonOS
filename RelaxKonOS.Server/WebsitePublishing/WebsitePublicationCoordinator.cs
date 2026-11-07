using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.WebsitePublishing;

/// <summary>
/// Coordinates an application-to-Nginx publication as one durable operation. The HTTP request only
/// records intent; certificate issuance, site generation, application association, and host-observed
/// verification continue after the phone disconnects. A restart marks active work interrupted rather
/// than repeating an ACME request or overwriting a site whose final state cannot be known.
/// </summary>
internal sealed class WebsitePublicationCoordinator(
    WebsitePublicationStore store,
    ApplicationDeploymentCatalogStore catalog,
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ILogger<WebsitePublicationCoordinator> logger, IResidentModuleWorkLeaseSource leases) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly ResidentBackgroundWorkOwner workOwner = new(leases, ServerModules.WebServerId);
    private bool ready;
    internal bool HasResponsibility => workOwner.HasResponsibility;

    public async Task<WebsitePublicationOperationDto> StartAsync(PublishWebsiteRequest request, string actor, string idempotencyKey, CancellationToken ct = default)
    {
        if (request is null) throw new WebsitePublicationException("website.request_invalid", 400);
        request = request with
        {
            Domain = request.Domain?.Trim().TrimEnd('.').ToLowerInvariant() ?? "",
            ContactEmail = request.ContactEmail?.Trim(),
            WebServerId = request.WebServerId?.Trim() ?? "",
        };
        Validate(request, idempotencyKey);
        lock (gate)
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new WebsitePublicationException("website.operation_store_unavailable", 503);
        using var work = await workOwner.AdmitAsync(ct);
        lock (gate)
        {
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new WebsitePublicationException("website.operation_store_unavailable", 503);
            work.Token.ThrowIfCancellationRequested();
            WebsitePublicationOperationDto operation;
            bool created;
            try
            {
                operation = store.Start(request, actor, idempotencyKey, () =>
                {
                    if (catalog.Find(request.ApplicationId) is null)
                        throw new WebsitePublicationException(ApplicationDeploymentProblemCodes.ApplicationNotFound, 404);
                }, out created);
            }
            catch (Exception error) when (error is not WebsitePublicationException rejected || rejected.ProblemCode == "website.operation_store_unavailable")
            { work.Fail(error); throw; }
            if (created) work.Run(stopping => RunAsync(operation.OperationId, request, actor, stopping));
            return operation;
        }
    }

    public WebsitePublicationOperationDto? Get(Guid operationId) => store.Get(operationId);
    public IReadOnlyList<WebsitePublicationOperationDto> History(Guid applicationId, int limit) => store.History(applicationId, limit);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ResidentBackgroundWorkOwner.Work? recovery = null;
        try
        {
            recovery = await workOwner.AdmitAsync(cancellationToken);
            await AttachInterruptedCertificateAsync(recovery.Token);
            _ = store.InterruptActive(); recovery.Token.ThrowIfCancellationRequested(); lock (gate) ready = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        { recovery?.Fail(error); logger.LogError(error, "Website publication startup is unavailable; its work remains blocked."); }
        finally { recovery?.Dispose(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) ready = false;
        await workOwner.StopAsync(cancellationToken);
    }
    public void Dispose() => workOwner.Dispose();

    private async Task AttachInterruptedCertificateAsync(CancellationToken ct)
    {
        var unlinked = store.Pending().Where(operation => operation.Stage == WebsitePublicationStage.Certificate
            && operation.CertificateId is null && operation.CertificateOperationId is null).ToArray();
        if (unlinked.Length == 0) return;
        await using var scope = scopes.CreateAsyncScope();
        var certificates = scope.ServiceProvider.GetRequiredService<CertificateOperationStore>();
        foreach (var operation in unlinked)
        {
            if (await certificates.GetWebsiteIssueAsync(operation.OperationId, ct) is { } issued)
                store.Update(operation.OperationId, current => current with
                { CertificateId = issued.CertificateId, CertificateOperationId = issued.OperationId });
        }
    }

    private async Task RunAsync(Guid operationId, PublishWebsiteRequest request, string actor, CancellationToken stopping)
    {
        await using var scope = scopes.CreateAsyncScope();
        var execution = scope.ServiceProvider.GetRequiredService<WebsitePublicationExecution>();
        await execution.RunAsync(operationId, request, actor, stopping);
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
