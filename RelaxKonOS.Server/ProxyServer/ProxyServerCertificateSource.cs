using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Certificate;

namespace RelaxKonOS.Server.ProxyServer;

public interface IProxyServerCertificateSource
{
    Task<ProxyServerTlsMaterial> ReadAsync(ProxyServerInbound inbound, CancellationToken ct);
}

internal sealed class ProxyServerCertificateSource(ICertificateStore certificates) : IProxyServerCertificateSource
{
    public async Task<ProxyServerTlsMaterial> ReadAsync(ProxyServerInbound inbound, CancellationToken ct)
    {
        var certificate = inbound.CertificateId is { } id ? await certificates.GetAsync(id, ct) : null;
        if (CertificateUsagePolicy.Problem(certificate, DateTimeOffset.UtcNow) is not null
            || !CertificateUsagePolicy.Covers(certificate!.Domains, inbound.ServerName)) throw new ProxyServerException("proxy_server.certificate_invalid");
        var paths = await certificates.GetNginxPathsAsync(certificate.Id, ct) ?? throw new ProxyServerException("proxy_server.certificate_invalid");
        return new(inbound.Id, await File.ReadAllTextAsync(paths.FullChainPath, ct), await File.ReadAllTextAsync(paths.PrivateKeyPath, ct), certificate.Version);
    }
}
