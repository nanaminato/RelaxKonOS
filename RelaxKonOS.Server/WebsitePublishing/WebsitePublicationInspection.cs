using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.WebsitePublishing;

/// <summary>Compares current evidence for one retained receipt in a quiescent isolated environment.</summary>
internal static class WebsitePublicationInspection
{
    // The isolated caller supplies this only after checking the expected certificate and upstream response.
    internal sealed record HttpsObservation(string Domain, Guid CertificateId, string CertificateThumbprint);

    internal sealed record SiteMetadata(string Domains, Guid? CertificateId, bool HttpsEnabled);

    internal sealed record Report(
        WebsitePublicationOperationDto Receipt,
        string? ConfigurationPath,
        bool? OwnershipMarkerMatches,
        SiteMetadata? RegisteredSite,
        string? AssociatedInstanceId,
        string? AssociatedSiteId,
        string? AssociatedDomain,
        HttpsObservation? LiveRoute,
        IReadOnlyList<string> Findings);

    internal static async Task<Report> InspectAsync(
        Guid operationId, WebsitePublicationStore publications, ApplicationDeploymentCatalogStore catalog,
        NginxWebServerManager nginx, WebServerDto instance, HttpsObservation? observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = publications.Get(operationId) ?? throw new WebsitePublicationException("website.operation_not_found", 404);
        if (WebsitePublicationStore.Active(receipt) || instance.Id != receipt.WebServerId)
            throw new InvalidOperationException("Inspection requires a terminal receipt and its original Nginx instance.");
        if (observation is not null && (receipt.SiteId is null || receipt.CertificateId != observation.CertificateId
            || !string.Equals(receipt.Domain, observation.Domain, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The HTTPS observation does not match the publication target.", nameof(observation));

        var site = receipt.SiteId is { } siteId ? await nginx.InspectSiteAsync(instance, siteId, cancellationToken) : null;
        var registered = site?.RegisteredSite;
        var application = catalog.Find(receipt.ApplicationId);
        var findings = new List<string>();
        if (receipt.State == WebsitePublicationState.Interrupted || receipt.RecoveryProblemCode is not null)
            findings.Add("publication_outcome_unverified");
        if (site is null)
        {
            findings.Add("site_id_unrecorded");
        }
        else
        {
            if (site.OwnershipMarkerMatches != true)
                findings.Add(site.OwnershipMarkerMatches is null ? "site_configuration_missing" : "site_configuration_ownership_mismatch");
            if (registered is null)
                findings.Add(observation is not null ? "live_site_unregistered" : "site_metadata_missing");
            else if (!registered.HttpsEnabled || registered.CertificateId != receipt.CertificateId
                || !registered.Bindings.Any(binding => string.Equals(binding.Domain, receipt.Domain, StringComparison.OrdinalIgnoreCase)))
                findings.Add("site_metadata_mismatch");

            if (application is null)
                findings.Add("application_missing");
            else if (application.SiteId != receipt.SiteId || application.SiteInstanceId != receipt.WebServerId
                || !string.Equals(application.Domain, receipt.Domain, StringComparison.OrdinalIgnoreCase))
                findings.Add("application_association_mismatch");

            if (observation is null)
                findings.Add("live_route_unverified");
        }

        var metadata = registered is null ? null : new SiteMetadata(registered.DomainsDisplay, registered.CertificateId, registered.HttpsEnabled);
        return new(receipt, site?.ConfigurationPath, site?.OwnershipMarkerMatches, metadata,
            application?.SiteInstanceId, application?.SiteId, application?.Domain, observation, findings.AsReadOnly());
    }
}
