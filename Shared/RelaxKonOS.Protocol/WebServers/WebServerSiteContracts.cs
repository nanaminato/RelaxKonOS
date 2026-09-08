using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.WebServers;

/// <summary>RelaxKonOS-owned Nginx virtual-host types. RelaxKonOS never edits unmanaged server blocks.</summary>
public enum WebServerSiteKind { ReverseProxy, Static }

/// <summary>A domain/IP and listen-port entry. All entries on a site are consolidated into one Nginx server block.</summary>
public sealed record WebServerSiteBindingDto(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("port")] int Port);

public sealed record WebServerSiteDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] WebServerSiteKind Kind,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("listenPort")] int ListenPort,
    [property: JsonPropertyName("upstream")] string? Upstream,
    [property: JsonPropertyName("rootPath")] string? RootPath,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId,
    [property: JsonPropertyName("httpsEnabled")] bool HttpsEnabled,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    // Kept optional so sites.json files written by earlier RelaxKonOS versions remain readable.
    [property: JsonPropertyName("bindings")] IReadOnlyList<WebServerSiteBindingDto>? Bindings = null,
    [property: JsonPropertyName("certificatePath")] string? CertificatePath = null,
    [property: JsonPropertyName("privateKeyPath")] string? PrivateKeyPath = null)
{
    /// <summary>Uses the legacy domain list when a site has not yet been saved with bindings.</summary>
    [JsonIgnore]
    public IReadOnlyList<WebServerSiteBindingDto> EffectiveBindings => Bindings is { Count: > 0 }
        ? Bindings
        : Domains.Select(domain => new WebServerSiteBindingDto(domain, ListenPort)).ToArray();

    /// <summary>Compact, table-ready entry list such as <c>app.example.com:5000, app.example.com:6000</c>.</summary>
    [JsonIgnore]
    public string DomainsDisplay => string.Join(", ", EffectiveBindings.Select(binding =>
        binding.Port == 80 ? binding.Domain : $"{binding.Domain}:{binding.Port}"));

    /// <summary>Static sites point to their created directory; proxy sites show their upstream.</summary>
    [JsonIgnore]
    public string ServiceAddress => RootPath ?? Upstream ?? "—";
    [JsonIgnore]
    public bool HasRootPath => !string.IsNullOrWhiteSpace(RootPath);
}

/// <summary>Creates a site when Id is empty, otherwise updates that RelaxKonOS-owned site.</summary>
public sealed record UpsertWebServerSiteRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] WebServerSiteKind Kind,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("listenPort")] int ListenPort = 80,
    [property: JsonPropertyName("upstream")] string? Upstream = null,
    [property: JsonPropertyName("siteDirectory")] string? SiteDirectory = null,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId = null,
    [property: JsonPropertyName("httpsEnabled")] bool HttpsEnabled = false,
    [property: JsonPropertyName("bindings")] IReadOnlyList<WebServerSiteBindingDto>? Bindings = null,
    [property: JsonPropertyName("certificatePath")] string? CertificatePath = null,
    [property: JsonPropertyName("privateKeyPath")] string? PrivateKeyPath = null);
