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
    private async Task<string?> WriteSiteConfigurationAsync(WebServerDto instance, WebServerSiteDto site, CancellationToken cancellationToken)
    {
        var directory = GetSitesDirectory(instance);
        if (directory is null) return "webserver.site_save_failed";
        (string FullChainPath, string PrivateKeyPath)? certificatePaths = null;
        if (site.HttpsEnabled)
        {
            certificatePaths = site.CertificateId is { } certificateId
                ? await certificates.GetNginxPathsAsync(certificateId, cancellationToken)
                : (site.CertificatePath!, site.PrivateKeyPath!);
            if (certificatePaths is null)
            {
                logger.LogError("Nginx site save could not resolve the certificate material. InstanceId={InstanceId}, SiteId={SiteId}, CertificateId={CertificateId}", instance.Id, site.Id, site.CertificateId);
                return "webserver.site_certificate_required";
            }
        }
        if (site.RootPath is not null && (!Directory.Exists(site.RootPath) || IsSymbolicLink(site.RootPath)))
            return "webserver.site_root_invalid";
        var config = Path.Combine(directory, $"{site.Id}.conf");
        if (File.Exists(config) && !IsRelaxKonOSSiteConfig(config))
        {
            logger.LogWarning("Nginx site save rejected because an existing configuration is not owned by RelaxKonOS. InstanceId={InstanceId}, SiteId={SiteId}, Configuration={Configuration}", instance.Id, site.Id, config);
            return "webserver.site_save_failed";
        }
        var stage = Path.Combine(directory, $"relaxkonos.{Guid.NewGuid():N}.conf");
        var backup = config + ".rollback";
        var acmeChallengeRoot = IsAcmeHttp01Enabled(directory) ? webRootChallenges.RootPath : null;
        if (!await WriteNginxFileAsync(stage, RenderSiteConfiguration(site, certificatePaths, acmeChallengeRoot), cancellationToken))
            return "webserver.site_save_failed";
        var hadExisting = File.Exists(config);
        try
        {
            var testProblem = await TestConfigurationAsync(instance, cancellationToken);
            if (testProblem is not null) return testProblem;
            if (hadExisting && !await MoveNginxFileAsync(config, backup, overwrite: false, cancellationToken)) return "webserver.site_save_failed";
            if (!await MoveNginxFileAsync(stage, config, overwrite: false, cancellationToken)) return "webserver.site_save_failed";
            var reloadProblem = await ReloadAfterTestAsync(instance, cancellationToken);
            if (reloadProblem is null)
            {
                if (File.Exists(backup)) _ = await DeleteNginxFileAsync(backup, cancellationToken);
                return null;
            }
            _ = await DeleteNginxFileAsync(config, cancellationToken);
            if (File.Exists(backup)) _ = await MoveNginxFileAsync(backup, config, overwrite: false, cancellationToken);
            _ = await ReloadAfterTestAsync(instance, cancellationToken);
            return reloadProblem;
        }
        finally
        {
            if (File.Exists(stage)) _ = await DeleteNginxFileAsync(stage, CancellationToken.None);
            if (File.Exists(backup) && !File.Exists(config)) _ = await MoveNginxFileAsync(backup, config, overwrite: false, CancellationToken.None);
        }
    }

    private async Task<string?> TestConfigurationAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var arguments = instance.ManagementMode == WebServerManagementMode.Managed ? ManagedArguments(GetManagedLayout(), ["-t"]) : new[] { "-t" };
        var result = UsesSystemPackageNginx(instance)
            ? await RunSystemPackageConfigurationTestAsync(cancellationToken)
            : await RunNginxAsync(instance.ExecutablePath, arguments, cancellationToken);
        if (result.Success) return null;
        logger.LogWarning("Nginx site configuration test failed. InstanceId={InstanceId}, Output={Output}", instance.Id, CommandOutputForLog(result.Output));
        return ConfigurationTestProblem(result.Output);
    }

    private static string ConfigurationTestProblem(string output) =>
        output.Contains("host not found in upstream", StringComparison.OrdinalIgnoreCase)
            ? "webserver.site_upstream_unresolvable"
            : "webserver.site_config_test_failed";

    private async Task<string?> ReloadAfterTestAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var testProblem = await TestConfigurationAsync(instance, cancellationToken);
        if (testProblem is not null) return testProblem;
        var running = UsesSystemPackageNginx(instance)
            ? await IsSystemdNginxActiveAsync(cancellationToken)
            : instance.ManagementMode == WebServerManagementMode.Managed
                ? IsManagedNginxRunning(GetManagedLayout())
                : IsNginxRunning(instance.ExecutablePath);
        if (!running) return null;
        if (UsesSystemPackageNginx(instance))
        {
            var helper = await RunSystemdNginxOperationAsync("reload", cancellationToken);
            if (!helper.Success) return ToWebServerProblem(helper.ProblemCode, "webserver.site_reload_failed");
            return null;
        }
        var reloaded = (await RunNginxAsync(instance.ExecutablePath,
            instance.ManagementMode == WebServerManagementMode.Managed ? ManagedArguments(GetManagedLayout(), ["-s", "reload"]) : ["-s", "reload"], cancellationToken)).Success;
        if (reloaded) return null;
        logger.LogWarning("Nginx site configuration reload failed. InstanceId={InstanceId}", instance.Id);
        return "webserver.site_reload_failed";
    }

    private static string RenderSiteConfiguration(WebServerSiteDto site, (string FullChainPath, string PrivateKeyPath)? certificatePaths)
        => RenderSiteConfiguration(site, certificatePaths, null);

    private static string RenderSiteConfiguration(WebServerSiteDto site, (string FullChainPath, string PrivateKeyPath)? certificatePaths, string? acmeChallengeRoot)
    {
        var bindings = site.Bindings;
        var serverNames = string.Join(' ', bindings.Select(binding => binding.Domain).Distinct(StringComparer.OrdinalIgnoreCase));
        var acmeLocation = string.IsNullOrWhiteSpace(acmeChallengeRoot) ? "" : $"\n    location ^~ /.well-known/acme-challenge/ {{\n        alias {NginxConfigPath(acmeChallengeRoot)}/;\n        default_type text/plain;\n    }}";
        var httpListens = bindings.Select(binding => binding.Port).Distinct()
            .Where(port => certificatePaths is null || port != 443)
            .SelectMany(port => RenderListeners(port, "", site.Ipv6Enabled)).ToList();
        var tlsListener = certificatePaths is null ? "" : $"{string.Join("\n    ", RenderListeners(443, "ssl http2", site.Ipv6Enabled))}\n    ssl_certificate {NginxConfigPath(certificatePaths.Value.FullChainPath)};\n    ssl_certificate_key {NginxConfigPath(certificatePaths.Value.PrivateKeyPath)};";
        if (site.RedirectHttpToHttps)
        {
            var redirect = httpListens.Count == 0 ? "" : $"server {{\n    {string.Join("\n    ", httpListens)}\n    server_name {serverNames};{acmeLocation}\n    location / {{ return 301 https://$host$request_uri; }}\n}}\n\n";
            return $"# Managed by RelaxKonOS. Site: {site.Id}\n{redirect}server {{\n    {tlsListener}\n    server_name {serverNames};\n    {RenderSiteBody(site)}\n}}\n";
        }
        var listens = httpListens;
        if (!string.IsNullOrWhiteSpace(tlsListener)) listens.Add(tlsListener);
        return $"# Managed by RelaxKonOS. Site: {site.Id}\nserver {{\n    {string.Join("\n    ", listens)}\n    server_name {serverNames};{acmeLocation}\n    {RenderSiteBody(site)}\n}}\n";
    }

    private static string RenderSiteBody(WebServerSiteDto site)
    {
        var routes = site.Routes.Select(route =>
        {
            var buffering = route.DisableBuffering ? "\n        proxy_request_buffering off;\n        proxy_buffering off;" : "";
            return $"location ^~ {route.Path} {{\n        proxy_pass {route.Upstream};\n        proxy_http_version 1.1;\n        proxy_set_header Upgrade $http_upgrade;\n        proxy_set_header Connection \"upgrade\";\n        proxy_set_header Host $host;\n        proxy_set_header X-Real-IP $remote_addr;\n        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n        proxy_set_header X-Forwarded-Proto $scheme;{buffering}\n    }}";
        });
        var fallback = site.RootPath is not null
            ? $"root {NginxConfigPath(site.RootPath)};\n    index index.html;\n    location / {{ try_files $uri $uri/ {(site.SpaFallback ? "/index.html" : "=404")}; }}"
            : site.Routes.Any(route => route.Path == "/") ? "" : "location / { return 404; }";
        return string.Join("\n    ", routes.Append(fallback).Where(part => !string.IsNullOrEmpty(part)));
    }

    private static IEnumerable<string> RenderListeners(int port, string parameters, bool ipv6Enabled)
    {
        var suffix = string.IsNullOrEmpty(parameters) ? ";" : $" {parameters};";
        yield return $"listen {port}{suffix}";
        if (ipv6Enabled) yield return $"listen [::]:{port}{suffix}";
    }

    private static string? GetSitesDirectory(WebServerDto instance)
    {
        if (instance.ConfigurationPath is null) return null;
        var confd = instance.ManagementMode == WebServerManagementMode.Managed
            ? Path.Combine(Path.GetDirectoryName(instance.ConfigurationPath)!, "conf.d")
            : FindOwnedIncludeDirectory(instance.ConfigurationPath);
        return confd is null ? null : Path.Combine(confd, "relaxkonos.d");
    }

    private static bool IsAcmeHttp01Enabled(string sitesDirectory)
    {
        var marker = Path.Combine(sitesDirectory, AcmeEnabledFileName);
        try { return File.Exists(marker) && !IsSymbolicLink(marker) && File.ReadAllText(marker) == OwnershipMarker + "\nACME HTTP-01 enabled.\n"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<bool> WriteNginxFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            return (await privilegedNginx.WriteManagedFileAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken)).Success;
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
        return true;
    }

    private async Task<bool> MoveNginxFileAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            return (await privilegedNginx.MoveManagedFileAsync(source, destination, overwrite, cancellationToken)).Success;
        File.Move(source, destination, overwrite);
        return true;
    }

    private async Task<bool> DeleteNginxFileAsync(string path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            return (await privilegedNginx.DeleteManagedFileAsync(path, cancellationToken)).Success;
        File.Delete(path);
        return true;
    }

    private static string ToSiteId(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? $"site-{Guid.NewGuid():N}"[..13] : slug[..Math.Min(slug.Length, 60)];
    }

    private static bool IsRelaxKonOSSiteConfig(string path)
    {
        try { return File.Exists(path) && !IsSymbolicLink(path) && File.ReadLines(path).FirstOrDefault()?.StartsWith("# Managed by RelaxKonOS. Site: ", StringComparison.Ordinal) == true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

}
