using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.Server.Startup;

/// <summary>Routes one private provider operation. It accepts neither a URL nor a module supplied by the caller.</summary>
internal static class CloudWebDavRelay
{
    private static readonly HttpClient Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static void Map(WebApplication app, IServiceProvider services) =>
        app.MapMethods(CloudWebDavProtocol.RequestPath, [HttpMethods.Get, HttpMethods.Post], (HttpContext http) =>
            RelayAsync(http, services.GetRequiredService<ModuleHostSupervisor>()));

    private static async Task RelayAsync(HttpContext http, ModuleHostSupervisor hosts)
    {
        using var caller = hosts.AcquireAuthenticated(http.Request.Headers[ModuleHostProtocol.ServiceTokenHeader].ToString(),
            CloudWebDavProtocol.CallerModuleId);
        if (caller is null) { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        using var provider = hosts.AcquireConnection(CloudWebDavProtocol.ProviderModuleId, trustedProvider: true);
        if (provider is null) { http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        if (HttpMethods.IsGet(http.Request.Method)) { http.Response.StatusCode = StatusCodes.Status204NoContent; return; }
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type)
            || !string.Equals(type.MediaType, CloudWebDavProtocol.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        if (http.Request.ContentLength > CloudWebDavProtocol.MaximumRequestBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = CloudWebDavProtocol.MaximumRequestBytes;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, caller.Stopping, provider.Stopping);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(provider.Endpoint, CloudWebDavProtocol.RequestPath))
            {
                Content = new StreamContent(http.Request.Body),
            };
            request.Headers.Add(ModuleHostProtocol.ServiceTokenHeader, provider.ServiceToken);
            request.Content.Headers.ContentType = type;
            request.Content.Headers.ContentLength = http.Request.ContentLength;
            using var response = await Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping.Token);
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentLength = response.Content.Headers.ContentLength;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(http.Response.Body, stopping.Token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested) http.Abort();
            else
            {
                http.Response.Clear();
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }
    }
}
