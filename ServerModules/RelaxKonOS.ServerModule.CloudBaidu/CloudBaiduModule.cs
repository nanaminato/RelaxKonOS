using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.CloudStorage.Transport;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModule.CloudBaidu;

public sealed class CloudBaiduModule : IModuleHostEntry, IModuleHostHttp
{
    private const string Origin = "https://pan.baidu.com";
    private readonly Func<Uri, bool, HttpClient> _transport;
    private string _dataPath = null!;

    public CloudBaiduModule() : this(CloudTransport.Create) { }
    internal CloudBaiduModule(Func<Uri, bool, HttpClient> transport) => _transport = transport;

    public string Id => CloudProviderProtocol.BaiduModuleId;
    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken)
    {
        _dataPath = context.ModuleDataPath;
        Directory.CreateDirectory(_dataPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context) =>
        CloudProviderEndpoint.Map(endpoints, CloudFileProvider.Baidu, Validate, ExecuteAsync);

    private static bool Validate(CloudProviderRequest request, Uri endpoint)
    {
        if (request.AllowPrivate || endpoint.GetLeftPart(UriPartial.Authority) != Origin || endpoint.AbsolutePath != "/") return false;
        return request.Action switch
        {
            CloudProviderAction.StartAuthorization => request.Path.Length == 0 && Valid(request.UserName, 512),
            CloudProviderAction.PollAuthorization or CloudProviderAction.Refresh => request.Path.Length == 0
                && Valid(request.UserName, 512) && Valid(request.Secret, 2048) && Valid(request.SessionToken, 8192),
            _ => Valid(request.AccessToken, 8192),
        };
    }

    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    private async Task<IResult> ExecuteAsync(CloudProviderRequest request, Uri endpoint, Stream? content, HttpContext http)
    {
        var ct = http.RequestAborted;
        if (request.Action is CloudProviderAction.StartAuthorization or CloudProviderAction.PollAuthorization)
            return await DeviceAuthorizationAsync(request, ct);
        if (request.Action == CloudProviderAction.Refresh)
            return Results.Ok(await BaiduDriveConnector.RefreshAsync(request.UserName, request.Secret, request.SessionToken!, _transport, ct));

        var drive = new BaiduDriveConnector(request.AccessToken!, _transport);
        return request.Action switch
        {
            CloudProviderAction.List => Results.Ok(await drive.ListAsync(request.Path, request.Cursor, ct)),
            CloudProviderAction.Info => await drive.InfoAsync(request.Path, ct) is { } entry ? Results.Ok(entry) : Results.NotFound(),
            CloudProviderAction.Download => await DownloadAsync(drive, request.Path, http),
            CloudProviderAction.CreateFolder => await CloudProviderEndpoint.CompleteAsync(drive.CreateFolderAsync(request.Path, ct)),
            CloudProviderAction.Delete => await CloudProviderEndpoint.CompleteAsync(
                drive.DeleteAsync(await drive.ResolveMutationPathAsync(request.Path, ct), ct)),
            CloudProviderAction.Move => await MoveAsync(drive, request, ct),
            CloudProviderAction.ResolveMutationPath => Results.Ok(await drive.ResolveMutationPathAsync(request.Path, ct)),
            CloudProviderAction.Upload => await CloudProviderEndpoint.CompleteAsync(UploadAsync(drive, request, content!, ct)),
            _ => Results.BadRequest(),
        };
    }

    private async Task<IResult> DeviceAuthorizationAsync(CloudProviderRequest request, CancellationToken ct)
    {
        var authorization = new BaiduDeviceAuthorization(_transport);
        try
        {
            return request.Action == CloudProviderAction.StartAuthorization
                ? Results.Ok(await authorization.StartAsync(request.UserName, ct))
                : Results.Ok(await authorization.PollAsync(request.UserName, request.Secret, request.SessionToken!, ct));
        }
        catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
    }

    private static async Task<IResult> DownloadAsync(BaiduDriveConnector drive, string path, HttpContext http)
    {
        await drive.DownloadAsync(path, (name, stream, length) =>
        {
            http.Response.ContentLength = length;
            return Results.Stream(stream, "application/octet-stream", name).ExecuteAsync(http);
        }, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task<IResult> MoveAsync(BaiduDriveConnector drive, CloudProviderRequest request, CancellationToken ct)
    {
        var source = await drive.ResolveMutationPathAsync(request.Path, ct);
        var destination = request.Destination!;
        if (source == destination || source.EndsWith('/') && destination.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest();
        return await CloudProviderEndpoint.CompleteAsync(drive.MoveAsync(source, destination, ct));
    }

    private async Task UploadAsync(BaiduDriveConnector drive, CloudProviderRequest request, Stream content, CancellationToken ct)
    {
        // Baidu precreate needs all block hashes before requesting specific blocks by index.
        var path = Path.Combine(_dataPath, "upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        await using var staged = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        if (!OperatingSystem.IsWindows()) File.Delete(path);
        await content.CopyToAsync(staged, ct);
        if (staged.Length != request.UploadLength) throw new InvalidDataException("cloud-baidu-upload-length");
        staged.Position = 0;
        await drive.UploadAsync(request.Path, staged, ct, request.Replace);
    }
}
