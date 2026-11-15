using System.Net;
using Amazon.Runtime;
using Amazon.S3.Model;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed partial class S3Connector
{
    private const int MaximumMutationObjects = 10_000;

    internal async Task DeleteAsync(string path, CancellationToken ct)
    {
        var objects = await ReadMutationObjectsAsync(path, ct);
        foreach (var source in objects)
            await DeleteObjectAsync(source, ct);
    }

    internal async Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        var objects = await ReadMutationObjectsAsync(source, ct);
        var directory = source.EndsWith('/');
        if (directory) await EnsurePrefixUnusedAsync(destination, ct);

        foreach (var item in objects)
        {
            var target = directory ? destination + item.Key[(_root.Length + source.Length)..] : destination;
            if (!CloudTransport.ValidPath(target)) throw new InvalidDataException("cloud-storage-invalid-destination");
            await CopyObjectAsync(item, target, ct);
        }
        foreach (var item in objects)
            await DeleteObjectAsync(item, ct);
    }

    internal async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        await EnsurePrefixUnusedAsync(path, ct);
        await UploadAsync(path, Stream.Null, ct);
    }

    private async Task EnsurePrefixUnusedAsync(string path, CancellationToken ct)
    {
        var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket, Prefix = _root + path, MaxKeys = 1,
        }, ct);
        if (page.S3Objects is { Count: > 0 })
            throw new HttpRequestException("cloud-storage-conflict", null, HttpStatusCode.Conflict);
    }

    private async Task<IReadOnlyList<MutationObject>> ReadMutationObjectsAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Cannot mutate the connection root.");
        var key = _root + path;
        if (!path.EndsWith('/'))
        {
            var metadata = await _client.GetObjectMetadataAsync(_bucket, key, ct);
            return [new(key, RequireETag(metadata.ETag), metadata.ContentLength)];
        }

        string? cursor = null;
        var objects = new List<MutationObject>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket, Prefix = key, ContinuationToken = cursor,
            }, ct);
            foreach (var entry in page.S3Objects ?? [])
            {
                if (!entry.Key.StartsWith(key, StringComparison.Ordinal) || !keys.Add(entry.Key)
                    || !CloudTransport.ValidPath(entry.Key[_root.Length..]) || entry.Size is not long length || length < 0)
                    throw new InvalidDataException("cloud-storage-invalid-object-list");
                objects.Add(new(entry.Key, RequireETag(entry.ETag), length));
                if (objects.Count > MaximumMutationObjects)
                    throw new InvalidDataException("cloud-storage-directory-too-large");
            }
            cursor = page.NextContinuationToken;
            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidDataException("cloud-storage-repeated-cursor");
        } while (cursor is not null);

        if (objects.Count == 0) throw new KeyNotFoundException();
        return objects;
    }

    private async Task CopyObjectAsync(MutationObject source, string target, CancellationToken ct)
    {
        var key = _root + target;
        var metadata = await _client.GetObjectMetadataAsync(_bucket, source.Key, ct);
        if (metadata.ETag != source.ETag)
            throw new HttpRequestException("cloud-storage-source-changed", null, HttpStatusCode.PreconditionFailed);
        if (source.Length == 0)
        {
            await UploadAsync(target, Stream.Null, ct);
            return;
        }

        const int bufferedPartSize = 16 * 1024 * 1024;
        var partSize = _isR2 ? bufferedPartSize : Math.Max(bufferedPartSize, (source.Length - 1) / MaximumMutationObjects + 1);
        if (partSize > 5L * 1024 * 1024 * 1024 || source.Length > partSize * MaximumMutationObjects)
            throw new InvalidDataException("cloud-storage-object-too-large");
        var start = new InitiateMultipartUploadRequest
        {
            BucketName = _bucket, Key = key, ContentType = metadata.Headers.ContentType,
        };
        foreach (var name in metadata.Metadata.Keys)
            start.Metadata[name] = metadata.Metadata[name];
        var started = await _client.InitiateMultipartUploadAsync(start, ct);
        var committed = false;
        try
        {
            var parts = new List<PartETag>();
            for (long offset = 0; offset < source.Length; offset += partSize)
            {
                var number = parts.Count + 1;
                if (_isR2)
                {
                    parts.Add(await CopyR2PartAsync(source, key, started.UploadId, number, offset,
                        (int)Math.Min(source.Length - offset, partSize), ct));
                    continue;
                }
                var request = new CopyPartRequest
                {
                    SourceBucket = _bucket, SourceKey = source.Key, ETagToMatch = [source.ETag],
                    DestinationBucket = _bucket, DestinationKey = key, UploadId = started.UploadId, PartNumber = number,
                };
                if (source.Length > partSize)
                {
                    request.FirstByte = offset;
                    request.LastByte = Math.Min(source.Length, offset + partSize) - 1;
                }
                var copied = await _client.CopyPartAsync(request, ct);
                parts.Add(new(number, copied.ETag));
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

    private async Task<PartETag> CopyR2PartAsync(MutationObject source, string key, string uploadId,
        int partNumber, long offset, int length, CancellationToken ct)
    {
        using var read = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket, Key = source.Key, EtagToMatch = source.ETag,
            ByteRange = new ByteRange(offset, offset + length - 1),
        }, ct);
        if (read.HttpStatusCode != HttpStatusCode.PartialContent || read.ContentLength != length)
            throw new InvalidDataException("cloud-storage-unexpected-copy-range");
        var bytes = new byte[length];
        await read.ResponseStream.ReadExactlyAsync(bytes, ct);
        using var content = new MemoryStream(bytes, writable: false);
        var uploaded = await _client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _bucket, Key = key, UploadId = uploadId, PartNumber = partNumber,
            PartSize = length, InputStream = content, UseChunkEncoding = false,
        }, ct);
        return new(partNumber, uploaded.ETag);
    }

    private async Task DeleteObjectAsync(MutationObject source, CancellationToken ct)
    {
        if (_isR2)
        {
            // R2 can accept DeleteObject even when If-Match is stale.
            var current = await _client.GetObjectMetadataAsync(_bucket, source.Key, ct);
            if (current.ETag != source.ETag)
                throw new HttpRequestException("cloud-storage-source-changed", null, HttpStatusCode.PreconditionFailed);
        }
        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucket, Key = source.Key, IfMatch = source.ETag,
        }, ct);
    }

    private static string RequireETag(string? etag) => string.IsNullOrWhiteSpace(etag) || etag == "*"
        ? throw new InvalidDataException("cloud-storage-missing-etag")
        : etag;

    private sealed record MutationObject(string Key, string ETag, long Length);
}
