using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.WebServer;


internal sealed partial class NginxWebServerManager
{
    public async Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed)) return null;
        return await ReadSitesAsync(instance, cancellationToken);
    }

    public async Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null)
        {
            logger.LogWarning("Nginx site save rejected because the requested instance was not found. InstanceId={InstanceId}", instanceId);
            return null;
        }
        if (instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed))
        {
            logger.LogWarning("Nginx site save rejected because the instance is not integrated or managed. InstanceId={InstanceId}, ManagementMode={ManagementMode}, Configuration={Configuration}", instance.Id, instance.ManagementMode, instance.ConfigurationPath);
            return null;
        }
        if (!TryNormalizeSite(instance, request, out var site, out var problem))
        {
            logger.LogWarning("Rejected Nginx site definition. InstanceId={InstanceId}, Problem={Problem}", instanceId, problem);
            throw new WebServerSiteValidationException(problem);
        }
        var directory = GetSitesDirectory(instance);
        if (directory is null)
        {
            logger.LogError("Nginx site save rejected because the RelaxKonOS site directory could not be resolved. InstanceId={InstanceId}, Configuration={Configuration}, ManagementMode={ManagementMode}", instance.Id, instance.ConfigurationPath, instance.ManagementMode);
            return null;
        }
        logger.LogInformation("Saving Nginx site. InstanceId={InstanceId}, SiteId={SiteId}, Name={Name}, ListenPort={ListenPort}, RootPath={RootPath}, RouteCount={RouteCount}, HttpsEnabled={HttpsEnabled}, SitesDirectory={SitesDirectory}",
            instance.Id, site.Id, site.Name, site.Bindings[0].Port, site.RootPath, site.Routes.Count, site.HttpsEnabled, directory);
        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            var sites = (await ReadSitesAsync(instance, cancellationToken)).ToList();
            var index = sites.FindIndex(item => item.Id == site.Id);
            WebServerSiteConcurrency.RequireCurrent(index >= 0 ? sites[index] : null, request.ExpectedUpdatedAt);
            if (site.HttpsEnabled && site.CertificateId is { } certificateId)
            {
                var certificate = await certificates.GetAsync(certificateId, cancellationToken);
                if (CertificateUsagePolicy.Problem(certificate, DateTimeOffset.UtcNow) is not null)
                    throw new WebServerSiteValidationException("webserver.site_certificate_not_usable");
                if (site.Bindings.Any(binding => !CertificateUsagePolicy.Covers(certificate!.Domains, binding.Domain)))
                    throw new WebServerSiteValidationException("webserver.site_certificate_domain_mismatch");
            }
            if (FindRoutingConflict(sites, site) is { } conflict)
            {
                logger.LogInformation("Nginx site save rejected because a domain and port are already assigned. InstanceId={InstanceId}, SiteId={SiteId}, ConflictingSiteId={ConflictingSiteId}, Domain={Domain}, Port={Port}", instance.Id, site.Id, conflict.SiteId, conflict.Domain, conflict.Port);
                throw new WebServerSiteConflictException("webserver.site_binding_conflict");
            }
            if (index >= 0 && site.UpdatedAt <= sites[index].UpdatedAt) site = site with { UpdatedAt = sites[index].UpdatedAt.AddTicks(1) };
            var anchorProblem = await EnsureSiteIncludeAnchorAsync(instance, cancellationToken);
            if (anchorProblem is not null)
            {
                logger.LogError("Nginx site save rejected because the RelaxKonOS include anchor cannot be used. InstanceId={InstanceId}, Problem={Problem}, Configuration={Configuration}, SitesDirectory={SitesDirectory}", instance.Id, anchorProblem, instance.ConfigurationPath, directory);
                return null;
            }
            if (IsSymbolicLink(directory))
            {
                logger.LogWarning("Nginx site save rejected because the sites directory is a symbolic link. InstanceId={InstanceId}, SitesDirectory={SitesDirectory}", instance.Id, directory);
                return null;
            }
            if (request.GrantNginxReadAccess && site.RootPath is not null)
            {
                var grant = await privilegedNginx.GrantStaticSiteReadAccessAsync(site.RootPath, cancellationToken);
                if (!grant.Success)
                    throw new WebServerSiteApplyException(grant.ProblemCode == PrivilegedProblemCode.DependencyMissing
                        ? "webserver.site_acl_package_required" : "webserver.site_permission_grant_failed");
            }
            if (site.RootPath is not null && (!Directory.Exists(site.RootPath) || IsSymbolicLink(site.RootPath)))
                throw new WebServerSiteValidationException("webserver.site_root_invalid");
            if (index >= 0) sites[index] = site; else sites.Add(site);
            var applyProblem = await WriteSiteConfigurationAsync(instance, site, cancellationToken);
            if (applyProblem is not null)
            {
                logger.LogWarning("Nginx site save failed before metadata was committed. InstanceId={InstanceId}, SiteId={SiteId}, Problem={Problem}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, applyProblem, directory);
                throw new WebServerSiteApplyException(applyProblem);
            }
            if (!await WriteSitesAsync(directory, sites, cancellationToken))
                throw new WebServerSiteApplyException("webserver.site_save_failed");
            logger.LogInformation("Nginx site saved successfully. InstanceId={InstanceId}, SiteId={SiteId}, ListenPort={ListenPort}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, site.Bindings[0].Port, directory);
            return site;
        }
        catch (IOException exception) { logger.LogError(exception, "I/O failure while saving Nginx site. InstanceId={InstanceId}, SiteId={SiteId}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, directory); return null; }
        catch (UnauthorizedAccessException exception) { logger.LogError(exception, "Access denied while saving Nginx site. InstanceId={InstanceId}, SiteId={SiteId}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, directory); return null; }
        finally { IntegrationGate.Release(); }
    }

    public async Task<bool?> DeleteSiteAsync(string instanceId, string siteId, DeleteWebServerSiteRequest request, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed) || !SiteIdPattern().IsMatch(siteId)) return null;
        var directory = GetSitesDirectory(instance);
        if (directory is null) return null;
        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            var sites = (await ReadSitesAsync(instance, cancellationToken)).ToList();
            var existing = sites.SingleOrDefault(site => site.Id == siteId);
            WebServerSiteConcurrency.RequireCurrent(existing, request.ExpectedUpdatedAt);
            if (!sites.RemoveAll(site => site.Id == siteId).Equals(1)) return false;
            var config = Path.Combine(directory, $"{siteId}.conf");
            if (!IsRelaxKonOSSiteConfig(config)) return null;
            var backup = config + ".rollback";
            if (!await MoveNginxFileAsync(config, backup, overwrite: false, cancellationToken)) return null;
            try
            {
                if (await ReloadAfterTestAsync(instance, cancellationToken) is not null) { _ = await MoveNginxFileAsync(backup, config, overwrite: false, cancellationToken); _ = await ReloadAfterTestAsync(instance, cancellationToken); return null; }
                if (!await DeleteNginxFileAsync(backup, cancellationToken)) return null;
                if (!await WriteSitesAsync(directory, sites, cancellationToken)) return null;
                return true;
            }
            finally { if (File.Exists(backup) && !File.Exists(config)) _ = await MoveNginxFileAsync(backup, config, overwrite: false, CancellationToken.None); }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { IntegrationGate.Release(); }
    }

    private async Task<IReadOnlyList<WebServerSiteDto>> ReadSitesAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var directory = GetSitesDirectory(instance);
        if (directory is null) return [];
        if (IsSymbolicLink(directory)) throw new IOException("Web site metadata directory is unsafe.");
        var path = Path.Combine(directory, "sites.json");
        if (IsSymbolicLink(path)) throw new IOException("Web site metadata file is unsafe.");
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            var sites = await JsonSerializer.DeserializeAsync<List<WebServerSiteDto>>(stream, SiteJson, cancellationToken)
                ?? throw new IOException("Web site metadata is empty.");
            if (sites.Any(site => site is null || !SiteIdPattern().IsMatch(site.Id ?? "") || site.ServerId != instance.Id
                || site.Bindings is null || site.Routes is null || site.UpdatedAt == default)
                || sites.Select(site => site.Id).Distinct(StringComparer.Ordinal).Count() != sites.Count)
                throw new IOException("Web site metadata is invalid.");
            return sites;
        }
        catch (JsonException exception) { throw new IOException("Web site metadata is invalid.", exception); }
        catch (UnauthorizedAccessException exception) { throw new IOException("Web site metadata is inaccessible.", exception); }
    }

    private async Task<string?> EnsureSiteIncludeAnchorAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (instance.ConfigurationPath is null) return "configuration_path_missing";
        var include = instance.ManagementMode == WebServerManagementMode.Managed
            ? Path.Combine(Path.GetDirectoryName(instance.ConfigurationPath)!, "conf.d")
            : FindOwnedIncludeDirectory(instance.ConfigurationPath);
        if (include is null) return "include_directory_not_found";
        var anchor = Path.Combine(include, OwnedFileName);
        var expected = AnchorContent(Path.Combine(include, "relaxkonos.d"));
        if (!File.Exists(anchor))
        {
            // A managed installation owns the entire generated conf.d layout. Its initial
            // configuration has no site anchor until the first site is saved, so create it
            // atomically here. An existing Nginx must be explicitly integrated first.
            if (instance.ManagementMode != WebServerManagementMode.Managed) return "integration_anchor_missing";
            var anchorStage = anchor + ".stage";
            if (!await WriteNginxFileAsync(anchorStage, expected, cancellationToken)
                || !await MoveNginxFileAsync(anchorStage, anchor, overwrite: false, cancellationToken)) return "webserver.site_save_failed";
            return null;
        }
        if (!IsOwnedFile(anchor)) return "integration_anchor_not_owned";
        if (File.ReadAllText(anchor) == expected) return null;
        var stage = anchor + ".stage";
        if (!await WriteNginxFileAsync(stage, expected, cancellationToken)
            || !await MoveNginxFileAsync(stage, anchor, overwrite: true, cancellationToken)) return "webserver.site_save_failed";
        return null;
    }

    private async Task<bool> WriteSitesAsync(string directory, IReadOnlyList<WebServerSiteDto> sites, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "sites.json");
        var stage = path + ".stage";
        return await WriteNginxFileAsync(stage, JsonSerializer.Serialize(sites.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase), SiteJson), cancellationToken)
            && await MoveNginxFileAsync(stage, path, overwrite: true, cancellationToken);
    }

}
