using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.ServerModule.CloudStorage;

internal static class CloudR2ConditionalDeleteAcceptance
{
    internal static async Task RunAsync(bool verifyConnector)
    {
        var endpoint = Required("ENDPOINT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Port != 443 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.IdnHost.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected a dedicated R2 HTTPS endpoint.");
        var bucket = Required("BUCKET");
        var prefix = Required("TEST_PREFIX").Trim('/');
        if (prefix.Length == 0 || prefix.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("Expected a nonempty dedicated test prefix.");
        using var client = new AmazonS3Client(Required("KEY_ID"), Required("SECRET"), new AmazonS3Config
        {
            ServiceURL = uri.AbsoluteUri, AuthenticationRegion = "auto", ForcePathStyle = true,
            MaxErrorRetry = 0, DisableLogging = true,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(verifyConnector ? 120 : 45));
        var ct = deadline.Token;
        if (verifyConnector)
        {
            await VerifyConnectorAsync(client, new("R2 acceptance", "s3", uri.AbsoluteUri, "auto",
                bucket, prefix, Required("KEY_ID"), Required("SECRET"), EnableWrites: true), ct);
            return;
        }
        var key = $"{prefix}/relaxkonos-delete-check-{Guid.NewGuid():N}.txt";
        string? current = null;
        try
        {
            var first = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, ContentBody = "original", IfNoneMatch = "*", UseChunkEncoding = false,
            }, ct);
            current = first.ETag;
            var updated = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = key, ContentBody = "updated", IfMatch = first.ETag, UseChunkEncoding = false,
            }, ct);
            current = updated.ETag;
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket, Key = key, IfMatch = first.ETag,
                }, ct);
                throw new InvalidOperationException("R2 accepted deletion with a stale ETag.");
            }
            catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed) { }
            var surviving = await client.GetObjectMetadataAsync(bucket, key, ct);
            if (string.IsNullOrEmpty(current) || surviving.ETag != current)
                throw new InvalidOperationException("The updated object did not survive the stale deletion.");
        }
        catch (AmazonS3Exception error)
        {
            throw new InvalidOperationException($"R2 acceptance request failed with HTTP {(int)error.StatusCode}.");
        }
        finally
        {
            if (!string.IsNullOrEmpty(current))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await client.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = bucket, Key = key, IfMatch = current,
                    }, cleanup.Token);
                    try
                    {
                        await client.GetObjectMetadataAsync(bucket, key, cleanup.Token);
                        throw new InvalidOperationException("R2 acceptance object remained after cleanup.");
                    }
                    catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { }
                }
                catch (AmazonS3Exception error)
                {
                    throw new InvalidOperationException($"R2 acceptance cleanup failed with HTTP {(int)error.StatusCode}.");
                }
            }
        }
        Console.WriteLine("PASS: R2 rejected stale conditional deletion and accepted cleanup with the current ETag.");
    }

    internal static async Task RunAwsAsync()
    {
        string Setting(string name) => Required(name, "AWS");
        var region = Setting("REGION");
        if (!System.Text.RegularExpressions.Regex.IsMatch(region, @"\A[a-z]{2}(-[a-z0-9]+)+-[0-9]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Expected an AWS S3 region.");
        var endpoint = $"https://s3.{region}.amazonaws.com";
        var bucket = Setting("BUCKET");
        var prefix = Setting("TEST_PREFIX").Trim('/');
        if (prefix.Length == 0 || prefix.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("Expected a nonempty dedicated test prefix.");
        var token = Environment.GetEnvironmentVariable("RELAXKONOS_AWS_SESSION_TOKEN");
        var access = Setting("KEY_ID");
        var secret = Setting("SECRET");
        Amazon.Runtime.AWSCredentials credentials = string.IsNullOrEmpty(token)
            ? new Amazon.Runtime.BasicAWSCredentials(access, secret)
            : new Amazon.Runtime.SessionAWSCredentials(access, secret, token);
        using var client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = endpoint, AuthenticationRegion = region, ForcePathStyle = true,
            MaxErrorRetry = 0, DisableLogging = true,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await VerifyConnectorAsync(client, new("AWS S3 acceptance", "s3", endpoint, region,
            bucket, prefix, access, secret, token, EnableWrites: true), deadline.Token);
    }

    private static async Task VerifyConnectorAsync(AmazonS3Client cleanupClient, CloudConnectionRequest settings, CancellationToken ct)
    {
        var root = $"{settings.Prefix}/relaxkonos-connector-check-{Guid.NewGuid():N}";
        settings = settings with { Prefix = root };
        using var http = new HttpClient();
        using var connector = new S3Connector(settings, http);
        string[] keys = ["source/", "source/empty/", "source/file.txt", "moved/", "moved/empty/", "moved/file.txt",
            "roundtrip/", "roundtrip/empty/", "roundtrip/payload.txt"];
        async Task WriteAsync(string payload, bool replace)
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload));
            await connector.UploadAsync("source/file.txt", stream, ct, replace);
        }
        try
        {
            await connector.CreateFolderAsync("source/", ct);
            await connector.CreateFolderAsync("source/empty/", ct);
            await WriteAsync("original", replace: false);
            await WriteAsync("replaced", replace: true);
            await connector.MoveAsync("source/", "moved/", ct);
            var listed = await connector.ListAsync("", null, ct);
            if (listed.NextCursor is not null || listed.Entries.SingleOrDefault() is not { Name: "moved", IsDirectory: true })
                throw new InvalidOperationException("R2 did not move the directory.");
            var children = await connector.ListAsync("moved/", null, ct);
            if (!children.Entries.Any(entry => entry.Name == "empty" && entry.IsDirectory))
                throw new InvalidOperationException("R2 did not preserve the empty child directory.");
            using (var file = await connector.DownloadAsync("moved/file.txt", ct))
            using (var reader = new StreamReader(file.ResponseStream))
                if (await reader.ReadToEndAsync(ct) != "replaced")
                    throw new InvalidOperationException("R2 did not preserve the replacement bytes.");
            await connector.DeleteAsync("moved/", ct);
            var remaining = await connector.ListAsync("", null, ct);
            if (remaining.Entries.Count != 0 || remaining.NextCursor is not null)
                throw new InvalidOperationException("R2 did not delete the directory.");
            var temporary = Directory.CreateTempSubdirectory("relaxkonos-r2-");
            try
            {
                await CloudStorageChecks.RunRealTreeRoundTripAsync(settings, temporary.FullName, ct);
                await connector.DeleteAsync("roundtrip/", ct);
                remaining = await connector.ListAsync("", null, ct);
                if (remaining.Entries.Count != 0 || remaining.NextCursor is not null)
                    throw new InvalidOperationException("R2 round trip test objects were not deleted.");
            }
            finally { temporary.Delete(recursive: true); }
        }
        catch (AmazonS3Exception error)
        {
            throw new InvalidOperationException($"R2 connector acceptance failed with HTTP {(int)error.StatusCode} and code {error.ErrorCode}.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var key in keys)
            {
                try { await cleanupClient.DeleteObjectAsync(settings.Bucket!, $"{root}/{key}", cleanup.Token); }
                catch (AmazonS3Exception error)
                {
                    throw new InvalidOperationException($"R2 connector acceptance cleanup failed with HTTP {(int)error.StatusCode}.");
                }
            }
        }
        Console.WriteLine($"PASS: {settings.Name} replaced, moved, deleted and round-tripped directories with file bytes and empty children.");
    }

    private static string Required(string name, string provider = "R2") =>
        Environment.GetEnvironmentVariable($"RELAXKONOS_{provider}_{name}") is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing RELAXKONOS_{provider}_{name}.");
}
