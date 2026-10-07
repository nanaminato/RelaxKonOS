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
    private bool TryNormalizeSite(WebServerDto instance, UpsertWebServerSiteRequest request, out WebServerSiteDto site, out string problem)
    {
        site = default!;
        problem = "webserver.site_name_invalid";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) return false;
        var id = string.IsNullOrWhiteSpace(request.Id) ? ToSiteId(request.Name) : request.Id.Trim().ToLowerInvariant();
        problem = "webserver.site_name_invalid";
        if (!SiteIdPattern().IsMatch(id)) return false;
        var bindings = (request.Bindings ?? [])
            .Select(binding => new WebServerSiteBindingDto(binding.Domain.Trim().TrimEnd('.').ToLowerInvariant(), binding.Port))
            .Where(binding => binding.Domain.Length > 0)
            .DistinctBy(binding => (binding.Domain, binding.Port))
            .ToArray();
        problem = "webserver.site_server_name_required";
        if (bindings.Length is < 1 or > 20) return false;
        problem = "webserver.site_port_invalid";
        if (bindings.Any(binding => binding.Port is < 1 or > 65535)) return false;
        problem = "webserver.site_server_name_invalid";
        if (bindings.Any(binding => !IsValidServerName(binding.Domain))) return false;
        problem = "webserver.site_certificate_required";
        var hasManagedCertificate = request.CertificateId is not null;
        var hasLocalCertificate = !string.IsNullOrWhiteSpace(request.CertificatePath) || !string.IsNullOrWhiteSpace(request.PrivateKeyPath);
        string? certificatePath = null;
        string? privateKeyPath = null;
        if (request.HttpsEnabled && !hasManagedCertificate && !hasLocalCertificate) return false;
        problem = "webserver.site_certificate_file_invalid";
        if (hasLocalCertificate && (!TryNormalizeCertificateFile(request.CertificatePath, CertificateFileKind.Certificate, required: true, out certificatePath)
            || !TryNormalizeCertificateFile(request.PrivateKeyPath, CertificateFileKind.PrivateKey, required: false, out privateKeyPath))) return false;
        if (hasLocalCertificate && privateKeyPath is null) privateKeyPath = certificatePath;
        if (hasManagedCertificate && hasLocalCertificate) return false;
        problem = "webserver.site_root_invalid";
        if (!TryNormalizeRootPath(request.RootPath, out var root, requireAccessibleDirectory: !request.GrantNginxReadAccess)) return false;
        var routes = new List<WebServerProxyRouteDto>();
        foreach (var route in request.Routes ?? [])
        {
            problem = "webserver.site_route_path_invalid";
            var path = route.Path?.Trim() ?? string.Empty;
            if (!IsValidRoutePath(path) || routes.Any(existing => string.Equals(existing.Path, path, StringComparison.Ordinal))) return false;
            problem = "webserver.site_upstream_invalid";
            if (!TryNormalizeUpstream(route.Upstream, out var upstream)) return false;
            routes.Add(new WebServerProxyRouteDto(path, upstream, route.DisableBuffering));
        }
        problem = "webserver.site_content_required";
        if (root is null && routes.Count == 0) return false;
        problem = "webserver.site_https_redirect_invalid";
        if (request.RedirectHttpToHttps && !request.HttpsEnabled) return false;
        site = new WebServerSiteDto(id, instance.Id, request.Name.Trim(), bindings, root, request.SpaFallback, routes, request.CertificateId, request.HttpsEnabled, request.RedirectHttpToHttps, request.Ipv6Enabled, DateTimeOffset.UtcNow,
            hasLocalCertificate ? certificatePath : null, hasLocalCertificate ? privateKeyPath : null);
        return true;
    }

    private static bool TryNormalizeRootPath(string? supplied, out string? root, bool requireAccessibleDirectory)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(supplied)) return true;
        try
        {
            if (!Path.IsPathFullyQualified(supplied.Trim())) return false;
            var fullPath = Path.GetFullPath(supplied.Trim());
            // The privileged ACL operation validates existence and symlinks before granting access.
            // A restricted parent may hide an otherwise valid directory from the Server until then.
            if ((requireAccessibleDirectory && (!Directory.Exists(fullPath) || IsSymbolicLink(fullPath))) || fullPath.Any(char.IsControl)
                || fullPath.IndexOfAny([' ', '\t', '"', '\'', ';', '#', '{', '}', '$']) >= 0) return false;
            root = fullPath;
            return true;
        }
        catch (Exception) when (supplied is not null) { return false; }
    }

    private static bool TryNormalizeUpstream(string? supplied, out string upstream)
    {
        upstream = string.Empty;
        if (!Uri.TryCreate(supplied?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        upstream = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimEnd('/');
        return true;
    }

    private static bool IsValidRoutePath(string value) => RoutePathPattern().IsMatch(value);

    /// <summary>
    /// Nginx accepts duplicate server names on a listener with only a warning, then silently
    /// ignores one of the declarations. A site's names are emitted on every one of its listeners,
    /// so treat any overlapping name/listener pair as an explicit conflict instead.
    /// </summary>
    private static SiteRoutingConflict? FindRoutingConflict(IEnumerable<WebServerSiteDto> sites, WebServerSiteDto candidate)
    {
        var candidateBindings = GetRoutingBindings(candidate).ToHashSet();
        foreach (var existing in sites)
        {
            if (string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var binding in GetRoutingBindings(existing))
            {
                if (candidateBindings.Contains(binding))
                    return new SiteRoutingConflict(existing.Id, binding.Domain, binding.Port);
            }
        }
        return null;
    }

    /// <summary>Includes the implicit TLS listener emitted for every HTTPS-enabled site.</summary>
    private static IEnumerable<SiteRoutingBinding> GetRoutingBindings(WebServerSiteDto site)
    {
        var domains = site.Bindings
            .Select(binding => binding.Domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ports = site.Bindings.Select(binding => binding.Port).Distinct().ToArray();
        foreach (var domain in domains)
        foreach (var port in ports)
            yield return new SiteRoutingBinding(domain, port);
        if (!site.HttpsEnabled) yield break;
        foreach (var domain in domains)
            yield return new SiteRoutingBinding(domain, 443);
    }

    private sealed record SiteRoutingBinding(string Domain, int Port);
    private sealed record SiteRoutingConflict(string SiteId, string Domain, int Port);

    private enum CertificateFileKind { Certificate, PrivateKey }

    /// <summary>Local certificate material remains on the host. Only existing regular PEM-style
    /// files with a recognised extension can be referenced by an owned Nginx site.</summary>
    private static bool TryNormalizeCertificateFile(string? supplied, CertificateFileKind kind, bool required, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(supplied)) return !required;
        try
        {
            var candidate = supplied.Trim();
            if (!Path.IsPathFullyQualified(candidate)) return false;
            var fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath) || IsSymbolicLink(fullPath)) return false;
            // These values are rendered into an Nginx directive. Restrict directive syntax
            // characters instead of letting a path alter the generated configuration.
            if (fullPath.Any(char.IsControl) || fullPath.IndexOfAny([' ', '\t', '"', '\'', ';', '#', '{', '}', '$']) >= 0) return false;
            var extension = Path.GetExtension(fullPath);
            var permitted = kind == CertificateFileKind.Certificate
                ? extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) || extension.Equals(".crt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cer", StringComparison.OrdinalIgnoreCase)
                : extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) || extension.Equals(".key", StringComparison.OrdinalIgnoreCase);
            if (!permitted) return false;
            path = fullPath;
            return true;
        }
        catch (Exception) when (supplied is not null) { return false; }
    }

    /// <summary>
    /// Stages the candidate with a .conf suffix so it is in Nginx's include graph during
    /// <c>nginx -t</c>. A .stage suffix would be skipped by the anchor's <c>*.conf</c>
    /// pattern, which meant the old configuration—not the new site—was being tested.
    /// </summary>
}
