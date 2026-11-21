using System.Buffers;
using RelaxKonOS.CloudStorage.Transport;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudS3;

internal sealed partial class S3Connector : IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _root;
    private readonly bool _isR2;

    internal S3Connector(CloudProviderRequest connection, HttpClient http)
    {
        _bucket = connection.Bucket!;
        _root = string.IsNullOrEmpty(connection.Prefix) ? "" : connection.Prefix.TrimEnd('/') + "/";
        _isR2 = new Uri(connection.Endpoint).IdnHost.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase);
        AWSCredentials credentials = string.IsNullOrEmpty(connection.SessionToken)
            ? new BasicAWSCredentials(connection.UserName, connection.Secret)
            : new SessionAWSCredentials(connection.UserName, connection.Secret, connection.SessionToken);
        _client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = connection.Endpoint,
            AuthenticationRegion = connection.Region,
            ForcePathStyle = true,
            DisableLogging = true,
            IgnoreConfiguredEndpointUrls = true,
            HttpClientFactory = new TransportFactory(http),
            MaxErrorRetry = 1,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        });
    }

    internal async Task<CloudDirectoryDto> ListAsync(string path, string? cursor, CancellationToken ct)
    {
        if (path.Length > 0 && !path.EndsWith('/')) throw new ArgumentException("directory");
        var prefix = _root + path;
        var result = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket, Prefix = prefix, Delimiter = "/", MaxKeys = 200, ContinuationToken = cursor,
        }, ct);
        var entries = new List<CloudEntryDto>();
        foreach (var folder in result.CommonPrefixes ?? [])
            if (Child(folder, prefix, directory: true) is { } reference)
                entries.Add(new(CloudTransport.Reference(reference), Name(reference), true, null, null));
        foreach (var item in result.S3Objects ?? [])
            if (Child(item.Key, prefix, directory: false) is { } reference)
                entries.Add(new(CloudTransport.Reference(reference), Name(reference), false, item.Size,
                    item.LastModified is { } modified ? new DateTimeOffset(modified) : null));
        return new(entries, result.NextContinuationToken);
    }

    internal async Task<CloudEntryDto?> InfoAsync(string path, CancellationToken ct)
    {
        if (path.Length == 0 || path.EndsWith('/'))
            return new(CloudTransport.Reference(path), Name(path), true, null, null);
        var result = await _client.GetObjectMetadataAsync(_bucket, _root + path, ct);
        return new(CloudTransport.Reference(path), Name(path), false, result.ContentLength, result.LastModified);
    }

    internal Task<GetObjectResponse> DownloadAsync(string path, CancellationToken ct)
    {
        if (path.Length == 0 || path.EndsWith('/')) throw new ArgumentException("file");
        return _client.GetObjectAsync(_bucket, _root + path, ct);
    }

    internal async Task UploadAsync(string path, Stream stream, long length, CancellationToken ct, bool replace)
    {
        const int partSize = 16 * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, partSize));
        try
        {
            if (length > partSize)
            {
                await UploadPartsAsync(_root + path, stream, length, buffer, partSize, replace, ct);
                return;
            }
            await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)length), ct);
            await RequireEndAsync(stream, ct);
            using var content = new MemoryStream(buffer, 0, (int)length, writable: false);
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = _root + path,
                InputStream = content,
                AutoCloseStream = false,
                IfNoneMatch = replace ? null : "*",
                UseChunkEncoding = false,
            }, ct);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private async Task UploadPartsAsync(string key, Stream stream, long length, byte[] buffer, int partSize, bool replace, CancellationToken ct)
    {
        var started = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket, Key = key,
        }, ct);
        var committed = false;
        try
        {
            var parts = new List<PartETag>();
            for (long offset = 0; offset < length; offset += partSize)
            {
                var number = parts.Count + 1;
                var count = (int)Math.Min(partSize, length - offset);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                using var content = new MemoryStream(buffer, 0, count, writable: false);
                var uploaded = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket, Key = key, UploadId = started.UploadId, PartNumber = number,
                    InputStream = content, PartSize = count,
                    UseChunkEncoding = false,
                }, ct);
                parts.Add(new(number, uploaded.ETag));
            }
            await RequireEndAsync(stream, ct);
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = key, UploadId = started.UploadId, PartETags = parts, IfNoneMatch = replace ? null : "*",
            }, ct);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _client.AbortMultipartUploadAsync(_bucket, key, started.UploadId, deadline.Token); }
                catch (Exception error) when (error is AmazonServiceException or HttpRequestException or OperationCanceledException) { }
            }
        }
    }

    private static async Task RequireEndAsync(Stream stream, CancellationToken ct)
    {
        if (await stream.ReadAsync(new byte[1], ct) != 0)
            throw new InvalidDataException("cloud-storage-upload-length-mismatch");
    }

    public void Dispose() => _client.Dispose();

    private string? Child(string key, string prefix, bool directory)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length <= prefix.Length
            || key.EndsWith('/') != directory || key[prefix.Length..].TrimEnd('/').Contains('/')) return null;
        var reference = key[_root.Length..];
        return CloudTransport.ValidPath(reference) ? reference : null;
    }

    private static string Name(string path) => path.TrimEnd('/').Split('/')[^1];

    private sealed class TransportFactory(HttpClient http) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => http;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}
