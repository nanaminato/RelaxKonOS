using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using RelaxKonOS.CloudStorage.Transport;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModule.CloudWebDav;

/// <summary>Stateless WebDAV execution behind the ModuleHost's service-token listener.</summary>
public sealed class CloudWebDavModule : IModuleHostEntry, IModuleHostHttp
{
    private readonly Func<Uri, bool, HttpClient> _transport;

    public CloudWebDavModule() : this(CloudTransport.Create) { }
    internal CloudWebDavModule(Func<Uri, bool, HttpClient> transport) => _transport = transport;

    public string Id => CloudWebDavProtocol.ProviderModuleId;
    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context) =>
        endpoints.MapPost(CloudWebDavProtocol.RequestPath, (Func<HttpContext, Task<IResult>>)ExecuteAsync);

    private async Task<IResult> ExecuteAsync(HttpContext http)
    {
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type)
            || !string.Equals(type.MediaType.Value, CloudWebDavProtocol.MediaType, StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 70 } boundary)
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = CloudWebDavProtocol.MaximumRequestBytes;

        var ct = http.RequestAborted;
        try
        {
            var reader = new MultipartReader(boundary, http.Request.Body) { BodyLengthLimit = CloudWebDavProtocol.MaximumMetadataBytes };
            var metadata = await reader.ReadNextSectionAsync(ct);
            if (metadata is null) return Results.BadRequest();
            var operation = await JsonSerializer.DeserializeAsync<CloudWebDavRequest>(metadata.Body, ModuleHostProtocol.Json, ct);
            if (!Validate(operation, out var endpoint)) return Results.BadRequest();
            var request = operation!;
            reader.BodyLengthLimit = CloudWebDavProtocol.MaximumUploadBytes;
            var content = request.Action == CloudWebDavAction.Upload ? await reader.ReadNextSectionAsync(ct) : null;
            if (request.Action == CloudWebDavAction.Upload && content is null) return Results.BadRequest();
            using var client = _transport(endpoint!, request.AllowPrivate);
            if (request.Action is CloudWebDavAction.Upload or CloudWebDavAction.Download)
                client.Timeout = Timeout.InfiniteTimeSpan;
            var dav = new WebDavConnector(request, client);
            return request.Action switch
            {
                CloudWebDavAction.List => Results.Ok(await dav.ListAsync(request.Path, request.Cursor, ct)),
                CloudWebDavAction.Info => await dav.InfoAsync(request.Path, ct) is { } entry ? Results.Ok(entry) : Results.NotFound(),
                CloudWebDavAction.Download => await DownloadAsync(dav, request.Path, http),
                CloudWebDavAction.CreateFolder => await CompletedAsync(dav.CreateFolderAsync(request.Path, ct)),
                CloudWebDavAction.Move => await MoveAsync(dav, request, ct),
                CloudWebDavAction.Delete => await DeleteAsync(dav, request.Path, ct),
                CloudWebDavAction.Upload => await CompletedAsync(dav.UploadAsync(request.Path, content!.Body, ct, request.Replace)),
                _ => Results.BadRequest(),
            };
        }
        catch (Exception error) when (http.Response.HasStarted && error is (IOException or HttpRequestException))
        {
            http.Abort();
            return Results.Empty;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
        { return Results.BadRequest(); }
        catch (HttpRequestException error)
        {
            return error.StatusCode is { } status && (int)status >= 400 && status != HttpStatusCode.ServiceUnavailable
                ? Results.StatusCode((int)status) : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidDataException)
        { return Results.StatusCode(StatusCodes.Status502BadGateway); }
    }

    private static bool Validate(CloudWebDavRequest? request, out Uri? endpoint)
    {
        endpoint = null;
        return request is { Endpoint.Length: > 0 and <= 2048, UserName.Length: > 0 and <= 256, Secret.Length: > 0 and <= 2048, Path: not null }
            && Uri.TryCreate(request.Endpoint, UriKind.Absolute, out endpoint) && endpoint.Scheme == Uri.UriSchemeHttps
            && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
            && !request.UserName.Contains(':') && !request.UserName.Any(char.IsControl) && !request.Secret.Any(char.IsControl)
            && CloudTransport.ValidPath(request.Path)
            && (request.Action is CloudWebDavAction.List or CloudWebDavAction.Info || request.Path.Length > 0)
            && (request.Action != CloudWebDavAction.Upload || request.UploadLength is >= 0 and <= CloudWebDavProtocol.MaximumUploadBytes)
            && (request.Action != CloudWebDavAction.Move || request.Destination is { Length: > 0 } destination && CloudTransport.ValidPath(destination));
    }

    private static async Task<IResult> DownloadAsync(WebDavConnector dav, string path, HttpContext http)
    {
        using var response = await dav.DownloadAsync(path, http.RequestAborted);
        response.EnsureSuccessStatusCode();
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = response.Content.Headers.ContentLength;
        await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task<IResult> MoveAsync(WebDavConnector dav, CloudWebDavRequest request, CancellationToken ct) =>
        await dav.InfoAsync(request.Path, ct) is null ? Results.NotFound()
            : await CompletedAsync(dav.MoveAsync(request.Path, request.Destination!, ct));

    private static async Task<IResult> DeleteAsync(WebDavConnector dav, string path, CancellationToken ct) =>
        await dav.InfoAsync(path, ct) is null ? Results.NotFound() : await CompletedAsync(dav.DeleteAsync(path, ct));

    private static async Task<IResult> CompletedAsync(Task operation)
    {
        await operation;
        return Results.NoContent();
    }
}
