using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Certificate;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
    internal async Task<string[]> GetCertificateReloadTargetsAsync(Guid certificateId, CancellationToken ct)
    {
        var targets = new List<string>();
        foreach (var instance in await DiscoverAsync(ct))
        {
            if (instance.Capabilities.CanReload && (await ReadSitesAsync(instance, ct))
                .Any(site => site.HttpsEnabled && site.CertificateId == certificateId))
                targets.Add(instance.Id);
        }
        return targets.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal async Task<WebServerOperationDto> StartCertificateReloadAsync(PendingNginxRenewal renewal, string instanceId,
        CertificateOperationStore certificateOperations, CancellationToken ct)
    {
        var key = $"certificate-renew:{renewal.CertificateId:D}:{renewal.Version}";
        const string actor = CertificateOperationStore.AutomaticRenewalActor;
        ct.ThrowIfCancellationRequested();
        return await operations.FindRequestAsync(key, instanceId, "reload", actor, ct)
            ?? await operations.StartScopedAsync<DirectExecution>(key, instanceId, "reload", actor, scopes,
                (execution, progress, token) => execution.ReloadRenewedCertificateAsync(renewal, instanceId, certificateOperations, progress, token),
                lifetime.ApplicationStopping);
    }

    private async Task<string?> ValidateCertificateReloadAsync(PendingNginxRenewal renewal, WebServerDto? instance, CancellationToken ct)
    {
        var certificate = await certificates.GetAsync(renewal.CertificateId, ct);
        if (certificate?.Version != renewal.Version || certificate.Status == CertificateStatus.Revoked
            || CertificateUsagePolicy.Problem(certificate, DateTimeOffset.UtcNow) is not null)
            return "webserver.site_certificate_not_usable";
        if (instance is null || !instance.Capabilities.CanReload || GetSitesDirectory(instance) is not { } directory)
            return "webserver.reload_not_permitted";
        var paths = await certificates.GetNginxPathsAsync(renewal.CertificateId, ct);
        if (paths is null) return "webserver.site_certificate_not_usable";
        var sites = (await ReadSitesAsync(instance, ct)).Where(site => site.HttpsEnabled && site.CertificateId == renewal.CertificateId);
        var hasOwnedBinding = false;
        foreach (var site in sites)
        {
            var file = Path.Combine(directory, site.Id + ".conf");
            var challengeRoot = site.AcmeHttp01 || IsAcmeHttp01Enabled(directory) ? webRootChallenges.RootPath : null;
            if (!File.Exists(file) || IsSymbolicLink(file)
                || !await MatchesSiteConfigAsync(file, RenderSiteConfiguration(site, paths, challengeRoot), ct))
                return "webserver.configuration_changed";
            hasOwnedBinding = true;
        }
        if (!hasOwnedBinding) return "webserver.reload_not_permitted";
        var running = UsesSystemPackageNginx(instance) ? await IsSystemdNginxActiveAsync(ct)
            : instance.ManagementMode == WebServerManagementMode.Managed
                ? IsManagedNginxRunning(GetManagedLayout()) : IsNginxRunning(instance.ExecutablePath);
        return running ? await TestConfigurationAsync(instance, ct) : "webserver.reload_not_permitted";
    }
}
