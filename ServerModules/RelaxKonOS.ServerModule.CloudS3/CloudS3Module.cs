using System.Net;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.CloudStorage.Transport;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModule.CloudS3;

public sealed class CloudS3Module : IModuleHostEntry, IModuleHostHttp
{
    private readonly Func<Uri, bool, HttpClient> _transport;

    public CloudS3Module() : this(CloudTransport.Create) { }
    internal CloudS3Module(Func<Uri, bool, HttpClient> transport) => _transport = transport;

    public string Id => CloudProviderProtocol.S3ModuleId;
    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken)
    {
        // Requests carry explicit credentials. SDK profile discovery stays inside this ModuleHost's data directory.
        AWSConfigs.DisableLegacyPersistenceStore = true;
        AWSConfigs.AWSProfilesLocation = Path.Combine(context.ModuleDataPath, "aws-profile");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context) =>
        CloudProviderEndpoint.Map(endpoints, CloudFileProvider.S3, Validate, ExecuteAsync);

    private static bool Validate(CloudProviderRequest request, Uri endpoint) =>
        !request.AllowPrivate && endpoint.AbsolutePath == "/"
        && request.Region is { Length: > 0 and <= 64 } region && Regex.IsMatch(region, "^[a-z0-9-]+$")
        && request.Bucket is { Length: >= 3 and <= 63 } bucket && Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9.-]+[a-z0-9]$")
        && (request.Prefix is null || CloudTransport.ValidPath(request.Prefix))
        && (request.SessionToken is null || request.SessionToken.Length <= 8192 && !request.SessionToken.Any(char.IsControl));

    private async Task<IResult> ExecuteAsync(CloudProviderRequest request, Uri endpoint, Stream? content, HttpContext http)
    {
        using var transport = _transport(endpoint, false);
        if (request.Action is CloudProviderAction.Upload or CloudProviderAction.Download)
            transport.Timeout = Timeout.InfiniteTimeSpan;
        var ct = http.RequestAborted;
        try
        {
            using var s3 = new S3Connector(request, transport);
            return request.Action switch
            {
                CloudProviderAction.List => Results.Ok(await s3.ListAsync(request.Path, request.Cursor, ct)),
                CloudProviderAction.Info => await s3.InfoAsync(request.Path, ct) is { } entry ? Results.Ok(entry) : Results.NotFound(),
                CloudProviderAction.Download => await DownloadAsync(s3, request.Path, http),
                CloudProviderAction.CreateFolder => await CloudProviderEndpoint.CompleteAsync(s3.CreateFolderAsync(request.Path, ct)),
                CloudProviderAction.Move => await CloudProviderEndpoint.CompleteAsync(s3.MoveAsync(request.Path, request.Destination!, ct)),
                CloudProviderAction.Delete => await CloudProviderEndpoint.CompleteAsync(s3.DeleteAsync(request.Path, ct)),
                CloudProviderAction.Upload => await CloudProviderEndpoint.CompleteAsync(
                    s3.UploadAsync(request.Path, content!, request.UploadLength!.Value, ct, request.Replace)),
                _ => Results.BadRequest(),
            };
        }
        catch (AmazonServiceException error)
        {
            var status = error.ErrorCode is "ExpiredToken" or "InvalidToken" or "InvalidAccessKeyId" or "SignatureDoesNotMatch"
                ? HttpStatusCode.Unauthorized : error.StatusCode;
            throw new HttpRequestException("cloud-storage-s3-request-failed", null, status);
        }
    }

    private static async Task<IResult> DownloadAsync(S3Connector s3, string path, HttpContext http)
    {
        using var response = await s3.DownloadAsync(path, http.RequestAborted);
        return await CloudProviderEndpoint.DownloadAsync(response.ResponseStream, response.ContentLength, http);
    }
}
