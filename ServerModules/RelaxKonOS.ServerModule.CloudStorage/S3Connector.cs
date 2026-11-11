using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed class S3Connector : IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _root;

    internal S3Connector(CloudConnectionRequest connection, HttpClient http)
    {
        _bucket = connection.Bucket!;
        _root = string.IsNullOrEmpty(connection.Prefix) ? "" : connection.Prefix.TrimEnd('/') + "/";
        AWSCredentials credentials = string.IsNullOrEmpty(connection.SessionToken)
            ? new BasicAWSCredentials(connection.UserName, connection.Secret)
            : new SessionAWSCredentials(connection.UserName, connection.Secret, connection.SessionToken);
        _client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = connection.Endpoint,
            AuthenticationRegion = connection.Region,
            ForcePathStyle = true,
            DisableLogging = true,
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

    internal async Task UploadAsync(string path, Stream stream, CancellationToken ct)
    {
        const long partSize = 16 * 1024 * 1024;
        if (stream is FileStream file && file.Length > partSize)
        {
            await UploadPartsAsync(_root + path, file, partSize, ct);
            return;
        }
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = _root + path,
            InputStream = stream,
            AutoCloseStream = false,
            IfNoneMatch = "*",
            UseChunkEncoding = false,
        }, ct);
    }

    private async Task UploadPartsAsync(string key, FileStream file, long partSize, CancellationToken ct)
    {
        var started = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket, Key = key,
        }, ct);
        var committed = false;
        try
        {
            var parts = new List<PartETag>();
            for (long offset = 0; offset < file.Length; offset += partSize)
            {
                var number = parts.Count + 1;
                var uploaded = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket, Key = key, UploadId = started.UploadId, PartNumber = number,
                    FilePath = file.Name, FilePosition = offset, PartSize = Math.Min(partSize, file.Length - offset),
                    UseChunkEncoding = false,
                }, ct);
                parts.Add(new(number, uploaded.ETag));
            }
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = key, UploadId = started.UploadId, PartETags = parts, IfNoneMatch = "*",
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
