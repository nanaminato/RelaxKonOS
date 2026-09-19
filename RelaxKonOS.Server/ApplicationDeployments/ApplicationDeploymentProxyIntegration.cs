using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal sealed record ApplicationDeploymentProxyResult(bool Success, string? ProblemCode, string? SiteInstanceId, string? SiteId, string? Domain, string? PreviousDefinition);

/// <summary>
/// Optional reverse-proxy integration. An application deploys without a web server, and only an
/// explicitly associated site is ever touched: the route is added to that site's existing
/// definition, validated before it is accepted, and restored when validation or reload fails.
/// </summary>
internal interface IApplicationDeploymentProxyIntegration
{
    Task<ApplicationDeploymentProxyResult> ApplyAsync(ApplicationRecord application, int hostPort, CancellationToken cancellationToken);
    Task<ApplicationDeploymentProxyResult> RevertAsync(ApplicationRecord application, ApplicationDeploymentProxyResult applied, CancellationToken cancellationToken);

    /// <summary>Detaches the application route when the application itself is deleted. It only ever
    /// removes the route that points at this application's own upstream.</summary>
    Task<ApplicationDeploymentProxyResult> RemoveAsync(ApplicationRecord application, int hostPort, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the associated Nginx site through the existing <see cref="IWebServerManager"/> boundary.
/// It never writes configuration itself and never accepts shell text.
/// </summary>
internal sealed class ApplicationDeploymentProxyIntegration(
    IWebServerManager manager,
    ILogger<ApplicationDeploymentProxyIntegration> logger) : IApplicationDeploymentProxyIntegration
{
    public async Task<ApplicationDeploymentProxyResult> ApplyAsync(ApplicationRecord application, int hostPort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(application.SiteId)) return new(true, null, null, null, null, null);

        var located = await LocateAsync(application, cancellationToken);
        if (located is null)
            return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ProxyUnavailable, null, null, null, null);

        var (instanceId, site) = located.Value;
        var upstream = $"http://127.0.0.1:{hostPort}";
        var routes = site.Routes
            .Where(route => !string.Equals(route.Path, "/", StringComparison.Ordinal))
            .ToList();
        routes.Insert(0, new WebServerProxyRouteDto("/", upstream));

        var request = new UpsertWebServerSiteRequest(
            site.Id, site.Name, site.Bindings, site.RootPath, GrantNginxReadAccess: false, site.SpaFallback,
            routes, site.CertificateId, site.HttpsEnabled, site.RedirectHttpToHttps, site.Ipv6Enabled,
            site.CertificatePath, site.PrivateKeyPath);

        try
        {
            var saved = await manager.UpsertSiteAsync(instanceId, request, cancellationToken);
            if (saved is null)
                return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ActivationFailed, null, null, null, null);

            var validation = await manager.TestConfigurationAsync(instanceId, cancellationToken);
            if (validation is { Valid: false })
            {
                logger.LogWarning("Reverse-proxy validation rejected the application route. SiteId={SiteId}", site.Id);
                return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ProxyValidationFailed,
                    instanceId, site.Id, saved.DomainsDisplay, Describe(site));
            }

            return new(true, null, instanceId, site.Id, saved.DomainsDisplay, Describe(site));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            logger.LogWarning(exception, "Reverse-proxy activation failed for site {SiteId}.", site.Id);
            return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ActivationFailed, null, null, null, null);
        }
    }

    /// <summary>Re-applies the site definition captured before the failed change.</summary>
    public async Task<ApplicationDeploymentProxyResult> RevertAsync(ApplicationRecord application, ApplicationDeploymentProxyResult applied, CancellationToken cancellationToken)
    {
        if (applied.PreviousDefinition is null || applied.SiteInstanceId is null || applied.SiteId is null) return new(true, null, null, null, null, null);
        var previous = Deserialize(applied.PreviousDefinition);
        if (previous is null) return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.RecoveryUnknown, null, null, null, null);
        try
        {
            var restored = await manager.UpsertSiteAsync(applied.SiteInstanceId, previous, cancellationToken);
            return restored is null
                ? new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ProxyValidationFailed, null, null, null, null)
                : new(true, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.PreviousInstanceRestored,
                    applied.SiteInstanceId, applied.SiteId, restored.DomainsDisplay, applied.PreviousDefinition);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            logger.LogWarning(exception, "Reverse-proxy restore failed for site {SiteId}.", applied.SiteId);
            return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.RecoveryFailed, null, null, null, null);
        }
    }

    /// <summary>
    /// Detaches only the route this application owns. The route is matched on its exact upstream, so
    /// an operator-defined route at the same path is left alone and a missing site is not an error.
    /// </summary>
    public async Task<ApplicationDeploymentProxyResult> RemoveAsync(ApplicationRecord application, int hostPort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(application.SiteId)) return new(true, null, null, null, null, null);

        var located = await LocateAsync(application, cancellationToken);
        if (located is null) return new(true, null, null, null, null, null);

        var (instanceId, site) = located.Value;
        var upstream = $"http://127.0.0.1:{hostPort}";
        var retained = site.Routes
            .Where(route => !(string.Equals(route.Path, "/", StringComparison.Ordinal) && string.Equals(route.Upstream, upstream, StringComparison.Ordinal)))
            .ToList();
        if (retained.Count == site.Routes.Count) return new(true, null, instanceId, site.Id, site.DomainsDisplay, null);

        var request = new UpsertWebServerSiteRequest(
            site.Id, site.Name, site.Bindings, site.RootPath, GrantNginxReadAccess: false, site.SpaFallback,
            retained, site.CertificateId, site.HttpsEnabled, site.RedirectHttpToHttps, site.Ipv6Enabled,
            site.CertificatePath, site.PrivateKeyPath);

        try
        {
            var saved = await manager.UpsertSiteAsync(instanceId, request, cancellationToken);
            if (saved is null)
                return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ActivationFailed, instanceId, site.Id, null, null);

            var validation = await manager.TestConfigurationAsync(instanceId, cancellationToken);
            if (validation is { Valid: false })
                return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ProxyValidationFailed, instanceId, site.Id, saved.DomainsDisplay, Describe(site));

            return new(true, null, instanceId, site.Id, saved.DomainsDisplay, Describe(site));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            logger.LogWarning(exception, "Reverse-proxy detach failed for site {SiteId}.", site.Id);
            return new(false, Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.ActivationFailed, null, null, null, null);
        }
    }

    private async Task<(string InstanceId, WebServerSiteDto Site)?> LocateAsync(ApplicationRecord application, CancellationToken cancellationToken)
    {
        var instances = await manager.ListAsync(cancellationToken);
        foreach (var instance in instances.Where(instance => !string.IsNullOrWhiteSpace(instance.Id)))
        {
            var sites = await manager.ListSitesAsync(instance.Id, cancellationToken);
            var site = sites?.FirstOrDefault(site => string.Equals(site.Id, application.SiteId, StringComparison.Ordinal));
            if (site is not null) return (instance.Id, site);
        }
        return null;
    }

    private static string Describe(WebServerSiteDto site) => System.Text.Json.JsonSerializer.Serialize(new UpsertWebServerSiteRequest(
        site.Id, site.Name, site.Bindings, site.RootPath, GrantNginxReadAccess: false, site.SpaFallback,
        site.Routes, site.CertificateId, site.HttpsEnabled, site.RedirectHttpToHttps, site.Ipv6Enabled,
        site.CertificatePath, site.PrivateKeyPath));

    private static UpsertWebServerSiteRequest? Deserialize(string value)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<UpsertWebServerSiteRequest>(value, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
