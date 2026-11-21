using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.CloudStorage.Transport;

/// <summary>Bounded private request handling shared by the isolated file providers.</summary>
public static class CloudProviderEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints, CloudFileProvider provider,
        Func<CloudProviderRequest, Uri, bool> validate,
        Func<CloudProviderRequest, Uri, Stream?, HttpContext, Task<IResult>> execute) =>
        endpoints.MapPost(CloudProviderProtocol.RequestPath(provider),
            (Func<HttpContext, Task<IResult>>)(http => ReadAndExecuteAsync(http, validate, execute)));

    public static async Task<IResult> CompleteAsync(Task operation)
    {
        await operation;
        return Results.NoContent();
    }

    public static async Task<IResult> DownloadAsync(Stream stream, long? length, HttpContext http)
    {
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = length;
        await stream.CopyToAsync(http.Response.Body, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task<IResult> ReadAndExecuteAsync(HttpContext http, Func<CloudProviderRequest, Uri, bool> validate,
        Func<CloudProviderRequest, Uri, Stream?, HttpContext, Task<IResult>> execute)
    {
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type)
            || !string.Equals(type.MediaType.Value, CloudProviderProtocol.MediaType, StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 70 } boundary)
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = CloudProviderProtocol.MaximumRequestBytes;

        var ct = http.RequestAborted;
        try
        {
            var reader = new MultipartReader(boundary, http.Request.Body) { BodyLengthLimit = CloudProviderProtocol.MaximumMetadataBytes };
            var metadata = await reader.ReadNextSectionAsync(ct);
            if (metadata is null) return Results.BadRequest();
            var request = await JsonSerializer.DeserializeAsync<CloudProviderRequest>(metadata.Body, ModuleHostProtocol.Json, ct);
            if (request is null || !Validate(request, out var endpoint) || !validate(request, endpoint!)) return Results.BadRequest();
            reader.BodyLengthLimit = CloudProviderProtocol.MaximumUploadBytes;
            var content = request.Action == CloudProviderAction.Upload ? await reader.ReadNextSectionAsync(ct) : null;
            if (request.Action == CloudProviderAction.Upload && content is null) return Results.BadRequest();
            return await execute(request, endpoint!, content?.Body, http);
        }
        catch (Exception error) when (http.Response.HasStarted && error is (IOException or HttpRequestException))
        {
            http.Abort();
            return Results.Empty;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
        { return Results.BadRequest(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (HttpRequestException error)
        {
            return error.StatusCode is { } status && (int)status >= 400 && status != HttpStatusCode.ServiceUnavailable
                ? Results.StatusCode((int)status) : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidDataException or EndOfStreamException)
        { return Results.StatusCode(StatusCodes.Status502BadGateway); }
    }

    private static bool Validate(CloudProviderRequest request, out Uri? endpoint)
    {
        endpoint = null;
        return request is { Endpoint.Length: > 0 and <= 2048, UserName.Length: <= 512, Secret.Length: <= 2048, Path: not null }
            && Uri.TryCreate(request.Endpoint, UriKind.Absolute, out endpoint) && endpoint.Scheme == Uri.UriSchemeHttps
            && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
            && !request.UserName.Any(char.IsControl) && !request.Secret.Any(char.IsControl)
            && CloudTransport.ValidPath(request.Path) && Enum.IsDefined(request.Action)
            && (request.Action is not (CloudProviderAction.Download or CloudProviderAction.Upload or CloudProviderAction.CreateFolder
                or CloudProviderAction.Move or CloudProviderAction.Delete) || request.Path.Length > 0)
            && (request.Action != CloudProviderAction.Upload || request.UploadLength is >= 0 and <= CloudProviderProtocol.MaximumUploadBytes)
            && (request.Action != CloudProviderAction.Move || request.Destination is { Length: > 0 } destination && CloudTransport.ValidPath(destination));
    }
}
