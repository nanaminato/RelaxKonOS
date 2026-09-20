using System.Net;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Creates short-lived clients for host-initiated downloads. A handler is created per operation so
/// changing the shared proxy preference takes effect immediately instead of waiting for the HTTP
/// client factory's handler lifetime to expire.
/// </summary>
public enum OutboundProxyTarget { ImageTags, RuntimeDownloads }

public interface IOutboundProxyHttpClientFactory
{
    Task<HttpClient> CreateAsync(OutboundProxyTarget target, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class OutboundProxyHttpClientFactory(IDockerProxyResolver resolver) : IOutboundProxyHttpClientFactory
{
    public async Task<HttpClient> CreateAsync(OutboundProxyTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var resolution = await resolver.ResolveAsync(cancellationToken);
        var enabled = target switch
        {
            OutboundProxyTarget.ImageTags => resolution.ImageTagsActive,
            OutboundProxyTarget.RuntimeDownloads => resolution.RuntimeDownloadsActive,
            _ => false,
        };

        var handler = new HttpClientHandler { UseProxy = enabled, AllowAutoRedirect = false };
        if (enabled)
        {
            handler.Proxy = new WebProxy(resolution.HttpProxy)
            {
                BypassProxyOnLocal = false,
                BypassList = resolution.NoProxy.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            };
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }
}
