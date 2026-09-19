using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.WebServers;

/// <summary>A domain/IP and listen-port entry. All entries on a site are consolidated into one Nginx server block.</summary>
public sealed record WebServerSiteBindingDto(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("port")] int Port);

/// <summary>A path prefix served by an HTTP(S) upstream within a RelaxKonOS-owned virtual host.</summary>
public sealed record WebServerProxyRouteDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("upstream")] string Upstream,
    [property: JsonPropertyName("disableBuffering")] bool DisableBuffering = false);

public sealed record WebServerSiteDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bindings")] IReadOnlyList<WebServerSiteBindingDto> Bindings,
    [property: JsonPropertyName("rootPath")] string? RootPath,
    [property: JsonPropertyName("spaFallback")] bool SpaFallback,
    [property: JsonPropertyName("routes")] IReadOnlyList<WebServerProxyRouteDto> Routes,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId,
    [property: JsonPropertyName("httpsEnabled")] bool HttpsEnabled,
    [property: JsonPropertyName("redirectHttpToHttps")] bool RedirectHttpToHttps,
    [property: JsonPropertyName("ipv6Enabled")] bool Ipv6Enabled,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("certificatePath")] string? CertificatePath = null,
    [property: JsonPropertyName("privateKeyPath")] string? PrivateKeyPath = null)
{
    /// <summary>Compact, table-ready entry list such as <c>app.example.com:5000, app.example.com:6000</c>.</summary>
    [JsonIgnore]
    public string DomainsDisplay => string.Join(", ", Bindings.Select(binding =>
        binding.Port == 80 ? binding.Domain : $"{binding.Domain}:{binding.Port}"));

    [JsonIgnore]
    public string ServiceAddress => RootPath ?? Routes.FirstOrDefault()?.Upstream ?? "—";
    [JsonIgnore]
    public bool HasRootPath => !string.IsNullOrWhiteSpace(RootPath);
    [JsonIgnore]
    public string RoutingDisplay => string.Join(" · ",
        (RootPath is null ? Array.Empty<string>() : ["/"]).Concat(Routes.Select(route => route.Path)));
}

/// <summary>Creates a site when Id is empty, otherwise updates that RelaxKonOS-owned site.</summary>
public sealed record UpsertWebServerSiteRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bindings")] IReadOnlyList<WebServerSiteBindingDto> Bindings,
    [property: JsonPropertyName("rootPath")] string? RootPath = null,
    [property: JsonPropertyName("spaFallback")] bool SpaFallback = false,
    [property: JsonPropertyName("routes")] IReadOnlyList<WebServerProxyRouteDto>? Routes = null,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId = null,
    [property: JsonPropertyName("httpsEnabled")] bool HttpsEnabled = false,
    [property: JsonPropertyName("redirectHttpToHttps")] bool RedirectHttpToHttps = false,
    [property: JsonPropertyName("ipv6Enabled")] bool Ipv6Enabled = false,
    [property: JsonPropertyName("certificatePath")] string? CertificatePath = null,
    [property: JsonPropertyName("privateKeyPath")] string? PrivateKeyPath = null);
