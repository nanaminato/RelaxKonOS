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
            if (!Directory.Exists(directory) || IsSymbolicLink(directory) || (await ReadSitesAsync(instance, ct)).Count != 0)
                return "website.acme_webroot_required";
            var anchor = Path.Combine(Path.GetDirectoryName(directory)!, OwnedFileName);
            if (!File.Exists(anchor) || IsSymbolicLink(anchor) || await File.ReadAllTextAsync(anchor, ct) != AnchorContent(directory)
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

}
