using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class NginxWebServerManager
{
    private const string WebsiteBootstrapOwner = "# RelaxKonOS website publication: ";

    internal async Task<string?> PrepareWebsiteWebRootAsync(WebServerDto instance, Guid operationId, string siteId,
        string domain, Action recordIntent, CancellationToken ct)
    {
        var directory = GetSitesDirectory(instance);
        if (!UsesSystemPackageNginx(instance) || directory is null || !SiteIdPattern().IsMatch(siteId)
            || !DomainPattern().IsMatch(domain) || !IsValidServerName(domain))
            return "website.acme_webroot_required";

        await IntegrationGate.WaitAsync(ct);
        try
        {
            if (await operations.HasPendingWebRootAsync(instance.Id, ct)) return "webserver.site_save_failed";
            if (!Directory.Exists(directory) || IsSymbolicLink(directory) || (await ReadSitesAsync(instance, ct)).Count != 0)
                return "website.acme_webroot_required";
            if (!await HasWebsiteBootstrapAnchorAsync(directory, ct)
                || Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != "sites.json")
                || !(await privilegedNginx.CheckFirstWebsiteAsync(ct)).Success
                || !await IsSystemdNginxActiveAsync(ct))
                return "website.acme_webroot_required";

            ct.ThrowIfCancellationRequested();
            recordIntent();
            var config = Path.Combine(directory, siteId + ".conf");
            var stage = config + ".stage";
            try
            {
                if (!await WriteNginxFileAsync(stage, RenderWebsiteBootstrap(operationId, siteId, domain), ct)
                    || !await MoveNginxFileAsync(stage, config, overwrite: false, ct))
                    return "webserver.site_save_failed";
                var test = await TestConfigurationAsync(instance, ct);
                if (test is not null) return test;
                var reload = await RunTrackedReloadAsync(operationId, ct);
                return reload.Success && reload.NginxReloadReceipt is { Succeeded: true } receipt && receipt.OperationId == operationId
                    ? null : WebServerOperationStore.NativeRecoveryRequired;
            }
            finally
            {
                if (File.Exists(stage)) _ = await DeleteNginxFileAsync(stage, CancellationToken.None);
            }
        }
        finally { IntegrationGate.Release(); }
    }

    private string RenderWebsiteBootstrap(Guid operationId, string siteId, string domain) =>
        $"# Managed by RelaxKonOS. Site: {siteId}\n{WebsiteBootstrapOwner}{operationId:D}\n"
        + $"server {{\n    listen 80;\n    server_name {domain};\n    if ($host != {domain}) {{ return 404; }}\n"
        + $"    location ^~ /.well-known/acme-challenge/ {{ alias {NginxConfigPath(webRootChallenges.RootPath)}/; default_type text/plain; }}\n"
        + "    location / { return 404; }\n}\n";

    private static bool HasWebsiteBootstrap(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "app*.conf*")
            .Any(path => !IsSymbolicLink(path) && File.ReadLines(path).Skip(1).FirstOrDefault()?.StartsWith(WebsiteBootstrapOwner, StringComparison.Ordinal) == true);

    internal async Task<bool> CleanupWebsiteWebRootAsync(WebServerDto instance, Guid publicationId, string siteId, string domain,
        Guid? cleanupId, Func<Guid> recordIntent, CancellationToken ct)
    {
        var directory = GetSitesDirectory(instance);
        if (!UsesSystemPackageNginx(instance) || directory is null || !SiteIdPattern().IsMatch(siteId)
            || !DomainPattern().IsMatch(domain) || !IsValidServerName(domain)) return false;
        await IntegrationGate.WaitAsync(ct);
        try
        {
            if (await operations.HasPendingWebRootAsync(instance.Id, ct)) return false;
            if (!Directory.Exists(directory) || IsSymbolicLink(directory)
                || !await HasSuccessfulWebsiteReloadAsync(publicationId, ct)) return false;

            var config = Path.Combine(directory, siteId + ".conf");
            var backup = config + ".cleanup.rollback";
            if (new FileInfo(config).LinkTarget is not null || new FileInfo(backup).LinkTarget is not null
                || Directory.Exists(config) || Directory.Exists(backup)) return false;
            var hasConfig = File.Exists(config);
            var hasBackup = File.Exists(backup);
            if (!hasConfig && !hasBackup)
                return cleanupId is { } finished && await HasSuccessfulWebsiteReloadAsync(finished, ct);
            if (hasConfig && hasBackup || hasBackup && cleanupId is null || File.Exists(config + ".rollback")
                || (await ReadSitesAsync(instance, ct)).Count != 0
                || !(await privilegedNginx.ConfirmServiceIdleAsync(ct)).Success) return false;

            if (!await HasWebsiteBootstrapAnchorAsync(directory, ct)
                || !await MatchesSiteConfigAsync(hasConfig ? config : backup, RenderWebsiteBootstrap(publicationId, siteId, domain), ct))
                return false;

            var id = cleanupId ?? recordIntent();
            if (hasConfig)
            {
                // A completed cleanup receipt cannot authorize removing a configuration that has reappeared.
                if (await HasSuccessfulWebsiteReloadAsync(id, ct) || !await MoveNginxFileAsync(config, backup, overwrite: false, ct))
                    return false;
            }
            if (await TestConfigurationAsync(instance, ct) is not null) return false;
            var reload = await RunTrackedReloadAsync(id, ct);
            if (!reload.Success || reload.NginxReloadReceipt is not { Succeeded: true } receipt || receipt.OperationId != id)
                return false;
            return await DeleteNginxFileAsync(backup, ct);
        }
        finally { IntegrationGate.Release(); }
    }

    private async Task<bool> HasSuccessfulWebsiteReloadAsync(Guid operationId, CancellationToken ct)
    {
        var observed = await privilegedNginx.QueryReloadReceiptAsync(operationId, ct);
        return observed.Success && observed.NginxReloadReceipt is { Succeeded: true } receipt && receipt.OperationId == operationId;
    }

    internal async Task<bool> ReconcileWebsiteBindingAsync(WebServerDto instance, Guid publicationId, Guid bindingId,
        WebServerSiteDto site, string configSha256, Action<bool> associate, CancellationToken ct)
    {
        var directory = GetSitesDirectory(instance);
        if (!UsesSystemPackageNginx(instance) || directory is null || !SiteIdPattern().IsMatch(site.Id)
            || site.Bindings is not [{ Port: 80 } binding] || !DomainPattern().IsMatch(binding.Domain)) return false;
        await IntegrationGate.WaitAsync(ct);
        try
        {
            if (await operations.HasPendingWebRootAsync(instance.Id, ct)) return false;
            if (!Directory.Exists(directory) || IsSymbolicLink(directory) || !await HasWebsiteBootstrapAnchorAsync(directory, ct)
                || !await HasSuccessfulWebsiteReloadAsync(publicationId, ct) || !await HasSuccessfulWebsiteReloadAsync(bindingId, ct)
                || !(await privilegedNginx.ConfirmServiceIdleAsync(ct)).Success || !await IsSystemdNginxActiveAsync(ct))
                return false;

            var config = Path.Combine(directory, site.Id + ".conf");
            var backup = config + ".rollback";
            if (!File.Exists(config) || IsSymbolicLink(config) || new FileInfo(backup).LinkTarget is not null
                || Directory.Exists(backup) || !await MatchesWebsiteBindingDigestAsync(config, configSha256, ct)) return false;
            var hasBackup = File.Exists(backup);
            if (hasBackup && !await MatchesSiteConfigAsync(backup, RenderWebsiteBootstrap(publicationId, site.Id, binding.Domain), ct))
                return false;

            var sites = await ReadSitesAsync(instance, ct);
            if (sites.Count == 0)
            {
                if (!hasBackup || !await WriteSitesAsync(directory, [site], ct)) return false;
            }
            else if (sites.Count != 1 || JsonSerializer.Serialize(sites[0], SiteJson) != JsonSerializer.Serialize(site, SiteJson))
                return false;

            associate(hasBackup);
            return !hasBackup || await DeleteNginxFileAsync(backup, ct);
        }
        finally { IntegrationGate.Release(); }
    }

    private static async Task<bool> MatchesWebsiteBindingDigestAsync(string path, string expected, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return file.Length <= 64 * 1024 && Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) == expected;
    }

    private static async Task<bool> HasWebsiteBootstrapAnchorAsync(string directory, CancellationToken ct)
    {
        var anchor = Path.Combine(Path.GetDirectoryName(directory)!, OwnedFileName);
        return File.Exists(anchor) && !IsSymbolicLink(anchor) && await File.ReadAllTextAsync(anchor, ct) == AnchorContent(directory);
    }
}
