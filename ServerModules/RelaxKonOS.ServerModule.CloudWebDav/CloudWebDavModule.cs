using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

    public string Id => CloudProviderProtocol.WebDavModuleId;
    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context) =>
        CloudProviderEndpoint.Map(endpoints, CloudFileProvider.WebDav,
            (request, _) => request.UserName is { Length: > 0 and <= 256 } && request.Secret.Length > 0
                && !request.UserName.Contains(':') && request.Action <= CloudProviderAction.Upload, ExecuteAsync);

    private async Task<IResult> ExecuteAsync(CloudProviderRequest request, Uri endpoint, Stream? content, HttpContext http)
    {
        using var client = _transport(endpoint, request.AllowPrivate);
        if (request.Action is CloudProviderAction.Upload or CloudProviderAction.Download)
            client.Timeout = Timeout.InfiniteTimeSpan;
        var dav = new WebDavConnector(request, client);
        var ct = http.RequestAborted;
        return request.Action switch
        {
            CloudProviderAction.List => Results.Ok(await dav.ListAsync(request.Path, request.Cursor, ct)),
            CloudProviderAction.Info => await dav.InfoAsync(request.Path, ct) is { } entry ? Results.Ok(entry) : Results.NotFound(),
            CloudProviderAction.Download => await DownloadAsync(dav, request.Path, http),
            CloudProviderAction.CreateFolder => await CloudProviderEndpoint.CompleteAsync(dav.CreateFolderAsync(request.Path, ct)),
            CloudProviderAction.Move => await dav.InfoAsync(request.Path, ct) is null ? Results.NotFound()
                : await CloudProviderEndpoint.CompleteAsync(dav.MoveAsync(request.Path, request.Destination!, ct)),
            CloudProviderAction.Delete => await dav.InfoAsync(request.Path, ct) is null ? Results.NotFound()
                : await CloudProviderEndpoint.CompleteAsync(dav.DeleteAsync(request.Path, ct)),
            CloudProviderAction.Upload => await CloudProviderEndpoint.CompleteAsync(dav.UploadAsync(request.Path, content!, ct, request.Replace)),
            _ => Results.BadRequest(),
        };
    }

    private static async Task<IResult> DownloadAsync(WebDavConnector dav, string path, HttpContext http)
    {
        using var response = await dav.DownloadAsync(path, http.RequestAborted);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(http.RequestAborted);
        return await CloudProviderEndpoint.DownloadAsync(stream, response.Content.Headers.ContentLength, http);
    }
}
