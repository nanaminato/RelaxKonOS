using System.Net.Http.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModuleHost;

internal sealed class CloudProviderClient(string? brokerUrl, string? token) : ICloudFileProviders
{
    private static readonly HttpClient Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async ValueTask<bool> IsAvailableAsync(CloudFileProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(provider, HttpMethod.Get);
            using var response = await Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    public async ValueTask<HttpResponseMessage> SendAsync(CloudFileProvider provider, CloudProviderRequest operation,
        Stream? content, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(provider, HttpMethod.Post);
        using var body = new MultipartContent("mixed");
        body.Add(JsonContent.Create(operation, options: ModuleHostProtocol.Json));
        if (content is not null) body.Add(new StreamContent(content));
        request.Content = body;
        return await Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(CloudFileProvider provider, HttpMethod method)
    {
        if (!Uri.TryCreate(brokerUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.IsLoopback || token is not { Length: 64 })
            throw new HttpRequestException("cloud-storage-provider-unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
        var request = new HttpRequestMessage(method, new Uri(endpoint, CloudProviderProtocol.RequestPath(provider)));
        request.Headers.Add(ModuleHostProtocol.ServiceTokenHeader, token);
        return request;
    }
}
