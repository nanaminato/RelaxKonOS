using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.ServerModule.CloudStorage;

internal static class CloudStorageChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await BaiduDeviceAuthorizationChecks.RunAsync(check);
        check(!CloudTransport.Allowed(IPAddress.Loopback, true)
            && !CloudTransport.Allowed(IPAddress.Parse("169.254.169.254"), true)
            && !CloudTransport.Allowed(IPAddress.Parse("::ffff:127.0.0.1"), true)
            && !CloudTransport.Allowed(IPAddress.Parse("192.168.0.9"), false)
            && CloudTransport.Allowed(IPAddress.Parse("192.168.0.9"), true)
            && CloudTransport.Allowed(IPAddress.Parse("8.8.8.8"), false),
            "outbound policy rejects loopback and metadata, and requires opt-in for private DAV");
        try { CloudTransport.PathFrom(CloudTransport.Reference("../private")); check(false, "reject traversal"); }
        catch (ArgumentException) { check(true, "entry references cannot traverse above the connection root"); }

        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var context = new TestContext(root);
            var upstream = new Upstream(check);
            var module = new CloudStorageModule((_, _) => new HttpClient(upstream, disposeHandler: false));
            await module.StartAsync(context, default);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            module.MapEndpoints(app, context);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            using (var unauthenticated = await client.GetAsync("/cloud/connections"))
                check(unauthenticated.StatusCode == HttpStatusCode.Forbidden, "connection API requires a proxy subject");
            var owner = Guid.NewGuid();
            SetOwner(client, owner);
            var dav = new CloudConnectionRequest("DAV", "webdav", "https://dav.example/root/", null, null, null, "test", "password");
            var connection = await CreateAsync(client, dav);
            var route = $"/cloud/connections/{connection.Id:D}";
            var listed = await client.GetStringAsync("/cloud/connections");
            check(listed.Contains("DAV", StringComparison.Ordinal) && !listed.Contains("password", StringComparison.Ordinal),
                "connection discovery never returns the credential");
            var first = await client.GetFromJsonAsync<CloudDirectoryDto>(route + "/entries");
            var next = await client.GetFromJsonAsync<CloudDirectoryDto>(route + "/entries?cursor=" + first!.NextCursor);
            check(first.Entries.Count == 200 && next?.Entries.Count == 6 && next.NextCursor is null
                && first.Entries.Concat(next!.Entries).Any(item => item.Name == "space name.txt")
                && !first.Entries.Concat(next.Entries).Any(item => item.Name.Contains("outside", StringComparison.Ordinal)),
                "WebDAV lists only direct children under the configured root and paginates escaped names");
            var file = next!.Entries.Single(entry => entry.Name == "space name.txt");
            var downloaded = await client.GetStringAsync(route + "/download?reference=" + file.Reference);
            check(downloaded == "file-content", "the module streams the authorized WebDAV file");
            using (var denied = await client.PostAsJsonAsync(route + "/uploads?key=denied", new CreateUploadRequest("", "new.txt", 0)))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "a read-only connection rejects uploads");
            SetOwner(client, Guid.NewGuid());
            using (var denied = await client.GetAsync(route + "/entries"))
                check(denied.StatusCode == HttpStatusCode.NotFound, "connection IDs cannot be used by another user");
            SetOwner(client, owner);
            using (var updated = await client.PutAsJsonAsync(route, dav with { EnableWrites = true }))
                updated.EnsureSuccessStatusCode();
            using (var created = await client.PostAsync(route + "/folders?name=new-folder", null))
                check(created.StatusCode == HttpStatusCode.NoContent, "WebDAV creates a folder when write access is enabled");
            using (var uploaded = await UploadAsync(client, route, "new.txt", check))
                check(uploaded.StatusCode == HttpStatusCode.OK, "WebDAV uses a conditional create-only upload");
            using (var conflict = await UploadAsync(client, route, "new.txt", check))
                check(conflict.StatusCode == HttpStatusCode.Conflict, "overwrite conflicts have a stable response");

            await VerifyLargeUploadAsync(client, route, check);

            foreach (var (hostname, region) in new[]
                     {
                         ("s3.us-west-2.amazonaws.com", "us-west-2"), ("account.r2.cloudflarestorage.com", "auto")
                     })
            {
                var settings = new CloudConnectionRequest("Objects", "s3", "https://" + hostname, region,
                    "test-bucket", "scope", "TESTKEY", "test-secret", EnableWrites: true);
                var s3 = await CreateAsync(client, settings);
                var s3Route = $"/cloud/connections/{s3.Id:D}";
                var page = await client.GetFromJsonAsync<CloudDirectoryDto>(s3Route + "/entries");
                check(page is { Entries.Count: 2, NextCursor: "second-page" } && page.Entries.Any(entry => entry.IsDirectory),
                    $"{hostname} lists paged S3 keys and common prefixes");
                var last = await client.GetFromJsonAsync<CloudDirectoryDto>(s3Route + "/entries?cursor=second-page");
                check(last is { Entries.Count: 1, NextCursor: null }, $"{hostname} forwards the continuation token");
                var item = page!.Entries.Single(entry => !entry.IsDirectory);
                check(await client.GetStringAsync(s3Route + "/download?reference=" + item.Reference) == "object",
                    $"{hostname} streams the scoped S3 object");
                using var uploaded = await UploadAsync(client, s3Route, "new.txt", check);
                check(uploaded.StatusCode == HttpStatusCode.OK, $"{hostname} signs a create-only S3 upload");
                await VerifyLargeUploadAsync(client, s3Route, check);
            }

            {
                const string protocol = "baidu-netdisk";
                var oauth = await CreateAsync(client, new(protocol, protocol, "https://untrusted.example", null,
                    null, null, "client-id", "client-secret", "refresh-test"));
                var oauthRoute = $"/cloud/connections/{oauth.Id:D}";
                check(!oauth.CanWrite, $"{protocol} imports a read-only connection");
                var page = (await client.GetFromJsonAsync<CloudDirectoryDto>(oauthRoute + "/entries"))!;
                check(page.Entries.Count == 1 && page.Entries[0].Name == "test.txt", $"{protocol} lists a file via its API");
                var entry = page.Entries.Single();
                var metadata = await client.GetFromJsonAsync<CloudEntryDto>(oauthRoute + "/info?reference=" + entry.Reference);
                check(metadata?.Name == entry.Name, $"{protocol} retrieves file metadata");
                check(await client.GetStringAsync(oauthRoute + "/download?reference=" + entry.Reference) == "oauth-content",
                    $"{protocol} streams a file using its provider download API");
                using var denied = await client.PostAsJsonAsync(oauthRoute + "/uploads?key=denied", new CreateUploadRequest("", "no.txt", 0));
                check(denied.StatusCode == HttpStatusCode.Forbidden, $"{protocol} rejects upload sessions");
                var writer = await CreateAsync(client, new("Writer", protocol, "", null, null, null,
                    "client-id", "client-secret", "refresh-test", EnableWrites: true));
                var writerRoute = $"/cloud/connections/{writer.Id:D}";
                check(writer.CanWrite, $"{protocol} can explicitly enable writes");
                using (var folder = await client.PostAsync(writerRoute + "/folders?name=folder", null))
                    check(folder.StatusCode == HttpStatusCode.NoContent, $"{protocol} creates a folder");
                using (var uploaded = await UploadAsync(client, writerRoute, "new.txt", check))
                    check(uploaded.StatusCode == HttpStatusCode.OK, $"{protocol} publishes a staged file");
                using (var conflict = await UploadAsync(client, writerRoute, "new.txt", check))
                    check(conflict.StatusCode == HttpStatusCode.Conflict, $"{protocol} rejects an existing destination");
            }
            check(!OAuthDriveConnector.AllowedBaiduDownload(new Uri("https://baidupcs.com.attacker.invalid/file"))
                && !OAuthDriveConnector.AllowedBaiduDownload(new Uri("http://d.pcs.baidu.com/file")),
                "Baidu downloads reject arbitrary domains and insecure redirect targets");

            using (var revoked = await client.DeleteAsync(route)) revoked.EnsureSuccessStatusCode();
            using (var missing = await client.GetAsync(route + "/entries"))
                check(missing.StatusCode == HttpStatusCode.NotFound, "revocation stops new requests");
            var stored = context.Files["connections.bin"];
            var reloaded = new CloudStorageModule();
            await reloaded.StartAsync(context, default);
            check(stored[0] == 1 && context.Files["connections.key"].Length == 32, "connection storage has a versioned envelope");
            check(!Encoding.UTF8.GetString(stored).Contains("test-secret", StringComparison.Ordinal)
                && !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(), "credentials are encrypted and upload staging is cleaned up");
            await app.StopAsync();
            await reloaded.StopAsync(default);
            await module.StopAsync(default);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyLargeUploadAsync(HttpClient client, string route, Action<bool, string> check)
    {
        var payload = LargePayload();
        const string key = "large-file-resume";
        var request = new CreateUploadRequest("", "large.bin", payload.Length);
        using var created = await client.PostAsJsonAsync(route + "/uploads?key=" + key, request);
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<UploadSessionDto>())!;
        var endpoint = route + "/uploads/" + session.UploadId;
        var firstLength = session.ChunkSize;
        using (var first = await client.PatchAsync(endpoint + $"?offset=0&length={firstLength}",
            new ByteArrayContent(payload, 0, firstLength)))
            first.EnsureSuccessStatusCode();
        await InterruptChunkAsync(client, endpoint, firstLength, payload);
        var afterDisconnect = (await client.GetFromJsonAsync<UploadSessionDto>(endpoint))!;
        check(afterDisconnect.Offset == firstLength, "a TCP disconnect during a chunk leaves the confirmed offset unchanged");
        using (var partial = await client.PatchAsync(endpoint + $"?offset={firstLength}&length={firstLength}",
            new ByteArrayContent(payload, firstLength, 1024)))
            check(partial.StatusCode == HttpStatusCode.BadRequest, "partial chunk is rejected");
        using (var reopened = await client.PostAsJsonAsync(route + "/uploads?key=" + key, request))
        {
            var resumed = (await reopened.Content.ReadFromJsonAsync<UploadSessionDto>())!;
            check(resumed.UploadId == session.UploadId && resumed.Offset == firstLength,
                "reopening a large upload after an interrupted chunk preserves the last complete offset");
        }
        for (var offset = firstLength; offset < payload.Length; offset += firstLength)
        {
            var size = Math.Min(firstLength, payload.Length - offset);
            using var response = await client.PatchAsync(endpoint + $"?offset={offset}&length={size}",
                new ByteArrayContent(payload, offset, size));
            response.EnsureSuccessStatusCode();
        }
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var response = await client.PostAsJsonAsync(endpoint + "/commit", new CommitUploadRequest(digest), timeout.Token);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                response.EnsureSuccessStatusCode();
                break;
            }
            await Task.Delay(20, timeout.Token);
        }
        var downloaded = await client.GetByteArrayAsync(route + "/download?reference=" + CloudTransport.Reference("large.bin"), timeout.Token);
        check(downloaded.AsSpan().SequenceEqual(payload), "a resumed file larger than 16 MiB downloads byte-for-byte");
        using var cleanup = await client.DeleteAsync(endpoint);
        cleanup.EnsureSuccessStatusCode();
    }

    private static async Task InterruptChunkAsync(HttpClient client, string endpoint, int offset, byte[] payload)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        var address = client.BaseAddress!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(address.Host, address.Port, timeout.Token);
        var owner = client.DefaultRequestHeaders.GetValues("X-RelaxKonOS-Module-User").Single();
        var headers = $"PATCH {endpoint}?offset={offset}&length=8192 HTTP/1.1\r\nHost: {address.Authority}\r\n"
            + $"X-RelaxKonOS-Module-User: {owner}\r\nContent-Length: 8192\r\nConnection: close\r\n\r\n";
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);
        await stream.WriteAsync(payload.AsMemory(offset, 1024), timeout.Token);
        tcp.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        try { await stream.CopyToAsync(Stream.Null, timeout.Token); }
        catch (IOException) { }
    }

    private static byte[] LargePayload()
    {
        var bytes = new byte[20 * 1024 * 1024 + 17];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string route, string name, Action<bool, string> check)
    {
        using var created = await client.PostAsJsonAsync(route + "/uploads?key=" + Guid.NewGuid().ToString("N"),
            new CreateUploadRequest("", name, 3));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<UploadSessionDto>())!;
        var endpoint = route + "/uploads/" + session.UploadId;
        using (var first = await client.PatchAsync(endpoint + "?offset=0&length=1", new StringContent("n")))
            first.EnsureSuccessStatusCode();
        using (var duplicate = await client.PatchAsync(endpoint + "?offset=0&length=1", new StringContent("n")))
            check(duplicate.StatusCode == HttpStatusCode.Conflict
                && duplicate.Headers.GetValues(FileUploadProtocol.OffsetHeader).Single() == "1",
                "a lost chunk reply can be reconciled from the authoritative offset");
        var resumed = (await client.GetFromJsonAsync<UploadSessionDto>(endpoint))!;
        check(resumed.Offset == 1, "session GET resumes from confirmed bytes");
        using (var second = await client.PatchAsync(endpoint + "?offset=1&length=2", new StringContent("ew")))
            second.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var response = await client.PostAsJsonAsync(endpoint + "/commit", new CommitUploadRequest(), timeout.Token);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                using var abort = await client.DeleteAsync(endpoint);
                return response;
            }
            response.Dispose();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void SetOwner(HttpClient client, Guid owner)
    {
        client.DefaultRequestHeaders.Remove("X-RelaxKonOS-Module-User");
        client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-User", owner.ToString("D"));
    }

    private static async Task<CloudConnectionDto> CreateAsync(HttpClient client, CloudConnectionRequest request)
    {
        using var response = await client.PostAsJsonAsync("/cloud/connections", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CloudConnectionDto>())!;
    }

    private sealed class Upstream(Action<bool, string> check) : HttpMessageHandler
    {
        private bool _davUploaded;
        private bool _baiduUploaded;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "dav.example")
            {
                if (request.Method.Method == "PROPFIND")
                {
                    var files = Enumerable.Range(0, 205).Select(index => DavItem($"/root/file-{index:D3}", false));
                    var responses = string.Concat(files.Append(DavItem("/root/space%20name.txt", false))
                        .Append(DavItem("/outside.txt", false)).Append(DavItem("/root/", true))
                        .Append(DavItem("https://outside.example/root/external.txt", false)));
                    return Xml(HttpStatusCode.MultiStatus, $"<multistatus xmlns=\"DAV:\">{responses}</multistatus>");
                }
                if (request.Method.Method == "MKCOL")
                    return new HttpResponseMessage(HttpStatusCode.Created);
                if (request.Method == HttpMethod.Put)
                {
                    var stagedBytes = await request.Content!.ReadAsByteArrayAsync(ct);
                    check(uri.AbsolutePath.StartsWith("/root/.relaxkonos-upload-", StringComparison.Ordinal)
                        && request.Headers.IfNoneMatch.Single().Tag == "*"
                        && (stagedBytes.AsSpan().SequenceEqual("new"u8) || stagedBytes.AsSpan().SequenceEqual(LargePayload())),
                        "WebDAV PUT writes the complete payload only to a random staging path");
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                if (request.Method.Method == "MOVE")
                {
                    var destination = request.Headers.GetValues("Destination").Single();
                    check(request.Headers.GetValues("Overwrite").Single() == "F"
                        && destination is "https://dav.example/root/new.txt" or "https://dav.example/root/large.bin",
                        "WebDAV MOVE publishes at the requested path without overwriting");
                    if (destination.EndsWith("/large.bin", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.Created);
                    if (_davUploaded) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                    _davUploaded = true;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                if (request.Method == HttpMethod.Delete)
                {
                    check(uri.AbsolutePath.StartsWith("/root/.relaxkonos-upload-", StringComparison.Ordinal),
                        "WebDAV conflict cleanup deletes only staging");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (uri.AbsolutePath == "/root/large.bin")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LargePayload()) };
                check(uri.AbsolutePath == "/root/space%20name.txt", "WebDAV download preserves the confined encoded path");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("file-content") };
            }

            if (uri.Host == "openapi.baidu.com")
            {
                var fields = QueryHelpers.ParseQuery(uri.Query);
                check(request.Method == HttpMethod.Get
                    && fields["grant_type"] == "refresh_token" && fields["refresh_token"] == "refresh-test",
                    "OAuth refresh uses the provider token endpoint and imported grant");
                return Json("""
                    {"access_token":"access-test","refresh_token":"refresh-rotated","expires_in":3600}
                    """);
            }
            if (uri.Host == "pan.baidu.com")
            {
                var fields = QueryHelpers.ParseQuery(uri.Query);
                check(fields["access_token"] == "access-test", "Baidu API receives the current access token");
                if (request.Method == HttpMethod.Post)
                {
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
                    check(form["rtype"] == "0", "Baidu create and precreate request conflict rejection");
                    if (form["isdir"] == "1")
                    {
                        check(form["path"] == "/folder", "Baidu creates the requested directory");
                        return Json("""{"errno":0,"path":"/folder"}""");
                    }
                    if (fields["method"] == "precreate")
                        return _baiduUploaded ? Json("""{"errno":-8}""")
                            : Json("""{"errno":0,"return_type":1,"uploadid":"test-upload","block_list":[0]}""");
                    check(form["uploadid"] == "test-upload" && form["path"] == "/new.txt",
                        "Baidu completes the precreated upload at its intended destination");
                    _baiduUploaded = true;
                    return Json("""{"errno":0,"path":"/new.txt"}""");
                }
                return Json("""
                    {"errno":0,"list":[{"fs_id":123,"path":"/test.txt","server_filename":"test.txt","size":13,"isdir":0,"dlink":"https://d.pcs.baidu.com/file"}]}
                    """);
            }
            if (uri.Host == "d.pcs.baidu.com")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var form = (MultipartFormDataContent)request.Content!;
                    check(QueryHelpers.ParseQuery(uri.Query)["partseq"] == "0"
                        && await form.Single().ReadAsStringAsync(ct) == "new", "Baidu uploads the expected multipart bytes");
                    return Json("""{"errno":0,"md5":"22af645d1859cb5ca6da0c484f1f37ea"}""");
                }
                check(QueryHelpers.ParseQuery(uri.Query)["access_token"] == "access-test"
                    && request.Headers.UserAgent.ToString() == "pan.baidu.com", "Baidu dlink uses its required download identity");
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://cdn.baidupcs.com/signed") },
                };
            }
            if (uri.Host == "cdn.baidupcs.com")
            {
                check(uri.Query.Length == 0 && request.Headers.Authorization is null,
                    "Baidu follows only trusted CDN redirects and does not add the token again");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("oauth-content") };
            }

            var region = uri.Host.Contains("r2.", StringComparison.Ordinal) ? "auto" : "us-west-2";
            var signature = request.Headers.TryGetValues("Authorization", out var headers) ? headers.Single() : "";
            var scope = signature.Split('/').ElementAtOrDefault(2);
            check(signature.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal)
                && signature.Contains($"/{region}/s3/aws4_request", StringComparison.Ordinal),
                $"S3 connector signs with region {region} (observed {scope ?? "none"})");
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (uri.AbsolutePath.EndsWith("/large.bin", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LargePayload()) };
                if (request.Method == HttpMethod.Post && query.ContainsKey("uploads"))
                    return Xml(HttpStatusCode.OK, "<InitiateMultipartUploadResult><UploadId>test-parts</UploadId></InitiateMultipartUploadResult>");
                if (request.Method == HttpMethod.Put && query.TryGetValue("partNumber", out var number))
                {
                    const int partSize = 16 * 1024 * 1024;
                    var bytes = LargePayload();
                    var offset = number == "1" ? 0 : partSize;
                    check(query["uploadId"] == "test-parts" && (await request.Content!.ReadAsByteArrayAsync(ct))
                        .AsSpan().SequenceEqual(bytes.AsSpan(offset, Math.Min(partSize, bytes.Length - offset))),
                        "S3 multipart sends exactly the expected byte range");
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-" + number + "\"");
                    return response;
                }
                check(request.Method == HttpMethod.Post && query["uploadId"] == "test-parts"
                    && request.Headers.IfNoneMatch.Single().Tag == "*", "S3 multipart completion is create-only");
                return Xml(HttpStatusCode.OK, "<CompleteMultipartUploadResult><ETag>\"complete\"</ETag></CompleteMultipartUploadResult>");
            }
            if (request.Method == HttpMethod.Put)
            {
                check(uri.AbsolutePath == "/test-bucket/scope/new.txt" && request.Headers.IfNoneMatch.Single().Tag == "*"
                    && await request.Content!.ReadAsStringAsync(ct) == "new", "S3 PUT is confined and conditional");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (uri.Query.Length == 0)
            {
                check(uri.AbsolutePath == "/test-bucket/scope/report.txt", "S3 download keeps the configured bucket prefix");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("object") };
            }
            check(uri.AbsolutePath.TrimEnd('/') == "/test-bucket" && query["prefix"] == "scope/"
                && query["list-type"] == "2",
                "ListObjectsV2 confines requests to the configured prefix");
            var tail = query.TryGetValue("continuation-token", out var token) && token == "second-page";
            var payload = tail ? "<IsTruncated>false</IsTruncated><Contents><Key>scope/last.txt</Key><Size>1</Size></Contents>"
                : "<IsTruncated>true</IsTruncated><NextContinuationToken>second-page</NextContinuationToken>"
                    + "<CommonPrefixes><Prefix>scope/folder/</Prefix></CommonPrefixes>"
                    + "<Contents><Key>scope/report.txt</Key><Size>6</Size></Contents>";
            return Xml(HttpStatusCode.OK, $"<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">{payload}</ListBucketResult>");
        }

        private static string DavItem(string href, bool folder) =>
            $"<response><href>{href}</href><propstat><status>HTTP/1.1 200 OK</status><prop>"
            + $"<resourcetype>{(folder ? "<collection/>" : "")}</resourcetype><getcontentlength>12</getcontentlength>"
            + "</prop></propstat></response>";

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Xml(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    }

    private sealed class TestContext(string root) : IModuleHostContext, IModuleHostStorage
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public string ModuleId => CloudStorageApi.ModuleId;
        public string ModuleDataPath => root;
        public string HostApiVersion => "2.0.0";
        public IModuleHostStorage Storage => this;
        public ILogger Logger => NullLogger.Instance;
        public IModulePrivilegeBroker Privileges => throw new NotSupportedException();
        public ValueTask<byte[]?> ReadAsync(string path, int maximumBytes, CancellationToken ct) =>
            ValueTask.FromResult(Files.GetValueOrDefault(path));
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            Files[path] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }
        public bool Delete(string path) => Files.Remove(path);
    }
}
