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
            using (var denied = await client.PostAsync(route + "/upload?name=new.txt&length=0", new ByteArrayContent([])))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "a read-only connection rejects uploads");
            SetOwner(client, Guid.NewGuid());
            using (var denied = await client.GetAsync(route + "/entries"))
                check(denied.StatusCode == HttpStatusCode.NotFound, "connection IDs cannot be used by another user");
            SetOwner(client, owner);
            using (var updated = await client.PutAsJsonAsync(route, dav with { EnableWrites = true }))
                updated.EnsureSuccessStatusCode();
            using (var created = await client.PostAsync(route + "/folders?name=new-folder", null))
                check(created.StatusCode == HttpStatusCode.NoContent, "WebDAV creates a folder when write access is enabled");
            using (var uploaded = await client.PostAsync(route + "/upload?name=new.txt&length=3", new StringContent("new")))
                check(uploaded.StatusCode == HttpStatusCode.NoContent, "WebDAV uses a conditional create-only upload");
            using (var conflict = await client.PostAsync(route + "/upload?name=new.txt&length=3", new StringContent("new")))
                check(conflict.StatusCode == HttpStatusCode.Conflict, "overwrite conflicts have a stable response");

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
                using var uploaded = await client.PostAsync(s3Route + "/upload?name=new.txt&length=3", new StringContent("new"));
                check(uploaded.StatusCode == HttpStatusCode.NoContent, $"{hostname} signs a create-only S3 upload");
            }

            using (var revoked = await client.DeleteAsync(route)) revoked.EnsureSuccessStatusCode();
            using (var missing = await client.GetAsync(route + "/entries"))
                check(missing.StatusCode == HttpStatusCode.NotFound, "revocation stops new requests");
            var stored = context.Files["connections.bin"];
            var reloaded = new CloudStorageModule();
            await reloaded.StartAsync(context, default);
            check(stored[0] == 1 && context.Files["connections.key"].Length == 32, "connection storage has a versioned envelope");
            check(!Encoding.UTF8.GetString(stored).Contains("test-secret", StringComparison.Ordinal)
                && !Directory.EnumerateFiles(root).Any(), "credentials are encrypted and upload staging is cleaned up");
            await app.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
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
                    check(request.Headers.IfNoneMatch.Single().Tag == "*" && await request.Content!.ReadAsStringAsync(ct) == "new",
                        "WebDAV PUT sends the complete staging body and If-None-Match");
                    if (_davUploaded) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                    _davUploaded = true;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                check(uri.AbsolutePath == "/root/space%20name.txt", "WebDAV download preserves the confined encoded path");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("file-content") };
            }

            var region = uri.Host.Contains("r2.", StringComparison.Ordinal) ? "auto" : "us-west-2";
            var signature = request.Headers.TryGetValues("Authorization", out var headers) ? headers.Single() : "";
            var scope = signature.Split('/').ElementAtOrDefault(2);
            check(signature.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal)
                && signature.Contains($"/{region}/s3/aws4_request", StringComparison.Ordinal),
                $"S3 connector signs with region {region} (observed {scope ?? "none"})");
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
            var query = QueryHelpers.ParseQuery(uri.Query);
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
