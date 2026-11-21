using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.CloudStorage.Transport;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.ServerModule.CloudStorage;
using RelaxKonOS.ServerModule.CloudS3;
using RelaxKonOS.ServerModule.CloudWebDav;
using RelaxKonOS.ServerModuleHost;

internal static class CloudStorageChecks
{
    internal static async Task RunRealTreeRoundTripAsync(CloudConnectionRequest settings, string root, CancellationToken ct,
        string? parentReference = null)
    {
        var context = new TestContext(root);
        var kind = CloudProviderConnection.Kind(settings.Protocol);
        await using var provider = kind is null ? null : await StartProvidersAsync(context, ct,
            (kind.Value, kind == CloudFileProvider.WebDav ? new CloudWebDavModule() : new CloudS3Module()));
        var module = new CloudStorageModule();
        await module.StartAsync(context, ct);
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            module.MapEndpoints(app, context);
            await app.StartAsync(ct);
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var owner = Guid.NewGuid();
            SetOwner(client, owner);
            if (settings.AllowPrivateWebDav)
                client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-Role", "admin");
            var connection = await CreateAsync(client, settings);
            await CloudFileJobChecks.RunRealTreeRoundTripAsync(client, owner, connection.Id, root, parentReference, ct);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

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
            await using var providers = await StartProvidersAsync(context, default,
                (CloudFileProvider.WebDav, new CloudWebDavModule((_, _) => new HttpClient(upstream, disposeHandler: false))),
                (CloudFileProvider.S3, new CloudS3Module((_, _) => new HttpClient(upstream, disposeHandler: false))));
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
            upstream.DavListingError = HttpStatusCode.Unauthorized;
            using (var rejected = await client.GetAsync(route + "/entries"))
            {
                var problem = await rejected.Content.ReadFromJsonAsync<RelaxKonOS.Protocol.Common.ProblemDetails>();
                check(rejected.StatusCode == HttpStatusCode.UnprocessableEntity
                    && problem?.Type == CloudStorageApi.CredentialsRejectedProblem,
                    "WebDAV credential rejection is distinguishable from RelaxKonOS authentication");
            }
            upstream.DavListingError = HttpStatusCode.Forbidden;
            using (var forbidden = await client.GetAsync(route + "/entries"))
                check(forbidden.StatusCode == HttpStatusCode.Forbidden,
                    "ordinary upstream permission failures are not treated as expired credentials");
            upstream.DavListingError = null;
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
            using (var denied = await client.PostAsJsonAsync(route + "/entries/move?reference=" + file.Reference,
                       new CloudMoveRequest(null, "denied.txt")))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "a read-only connection rejects moving an entry");
            using (var denied = await client.DeleteAsync(route + "/entries?reference=" + file.Reference))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "a read-only connection rejects deleting an entry");
            SetOwner(client, Guid.NewGuid());
            using (var denied = await client.GetAsync(route + "/entries"))
                check(denied.StatusCode == HttpStatusCode.NotFound, "connection IDs cannot be used by another user");
            using (var denied = await client.DeleteAsync(route + "/entries?reference=" + file.Reference))
                check(denied.StatusCode == HttpStatusCode.NotFound, "deletion cannot use another user's connection ID");
            SetOwner(client, owner);
            using (var updated = await client.PutAsJsonAsync(route, dav with { EnableWrites = true }))
                updated.EnsureSuccessStatusCode();
            using (var created = await client.PostAsync(route + "/folders?name=new-folder", null))
                check(created.StatusCode == HttpStatusCode.OK
                    && (await created.Content.ReadFromJsonAsync<CloudCreatedFolder>()) is { Reference.Length: > 0 },
                    "WebDAV returns a reference to the created folder");
            using (var uploaded = await UploadAsync(client, route, "new.txt", check))
                check(uploaded.StatusCode == HttpStatusCode.OK, "WebDAV uses a conditional create-only upload");
            using (var conflict = await UploadAsync(client, route, "new.txt", check))
                check(conflict.StatusCode == HttpStatusCode.Conflict, "overwrite conflicts have a stable response");

            await VerifyLargeUploadAsync(client, route, check);
            await CloudFileJobChecks.RunAsync(client, owner, connection.Id, file.Reference, root, check, rejected => upstream.RejectDavDownload = rejected);
            check(upstream.DavUploadedTreePaths.SetEquals(["/root/tree-upload/", "/root/tree-upload/empty/", "/root/tree-upload/payload.txt"]),
                "WebDAV receives both directories and the nested file at the expected paths");
            using (var replaced = await UploadAsync(client, route, "space name.txt", check, replace: true))
                check(replaced.StatusCode == HttpStatusCode.OK, "WebDAV replaces a file with a matching strong ETag");
            using (var moved = await client.PostAsJsonAsync(route + "/entries/move?reference=" + file.Reference,
                       new CloudMoveRequest(null, "moved.txt")))
                check(moved.StatusCode == HttpStatusCode.NoContent, "WebDAV moves the selected file");
            using (var deleted = await client.DeleteAsync(route + "/entries?reference=bW92ZWQudHh0"))
                check(deleted.StatusCode == HttpStatusCode.NoContent, "WebDAV deletes the moved file");
            using (var denied = await client.DeleteAsync(route + "/entries?reference="))
                check(denied.StatusCode == HttpStatusCode.BadRequest, "WebDAV rejects deletion of the connection root");
            await VerifyPublishCancellationAsync(client, route, upstream, check);

            foreach (var (hostname, region) in new[]
                     {
                         ("s3.us-west-2.amazonaws.com", "us-west-2"), ("account.r2.cloudflarestorage.com", "auto")
                     })
            {
                var settings = new CloudConnectionRequest("Objects", "s3", "https://" + hostname, region,
                    "test-bucket", "scope", "TESTKEY", "test-secret", EnableWrites: true);
                var s3 = await CreateAsync(client, settings);
                var s3Route = $"/cloud/connections/{s3.Id:D}";
                upstream.S3ListingError = "ExpiredToken";
                using (var rejected = await client.GetAsync(s3Route + "/entries"))
                {
                    var credentials = await rejected.Content.ReadFromJsonAsync<RelaxKonOS.Protocol.Common.ProblemDetails>();
                    check(rejected.StatusCode == HttpStatusCode.UnprocessableEntity
                        && credentials?.Type == CloudStorageApi.CredentialsRejectedProblem,
                        "S3 auth errors survive the private provider boundary without returning credentials");
                }
                upstream.S3ListingError = null;
                var page = await client.GetFromJsonAsync<CloudDirectoryDto>(s3Route + "/entries");
                check(page is { Entries.Count: 2, NextCursor: "second-page" } && page.Entries.Any(entry => entry.IsDirectory),
                    $"{hostname} lists paged S3 keys and common prefixes");
                var last = await client.GetFromJsonAsync<CloudDirectoryDto>(s3Route + "/entries?cursor=second-page");
                check(last is { Entries.Count: 1, NextCursor: null }, $"{hostname} forwards the continuation token");
                var item = page!.Entries.Single(entry => !entry.IsDirectory);
                using (var existingFolder = await client.PostAsync(s3Route + "/folders?name=folder", null))
                    check(existingFolder.StatusCode == HttpStatusCode.Conflict,
                        $"{hostname} rejects creating a directory over a populated prefix without a marker");
                check(await client.GetStringAsync(s3Route + "/download?reference=" + item.Reference) == "object",
                    $"{hostname} streams the scoped S3 object");
                using var uploaded = await UploadAsync(client, s3Route, "new.txt", check);
                check(uploaded.StatusCode == HttpStatusCode.OK, $"{hostname} signs a create-only S3 upload");
                using var conflict = await UploadAsync(client, s3Route, "replace.txt", check);
                var problem = await conflict.Content.ReadFromJsonAsync<RelaxKonOS.Protocol.Common.ProblemDetails>();
                check(conflict.StatusCode == HttpStatusCode.Conflict && problem?.Title == CloudStorageApi.UploadReplaceAvailable,
                    $"{hostname} requires a replacement decision before overwriting");
                using var replaced = await UploadAsync(client, s3Route, "replace.txt", check, replace: true);
                check(replaced.StatusCode == HttpStatusCode.OK, $"{hostname} accepts explicit replacement");
                await VerifyLargeUploadAsync(client, s3Route, check);
                using var failedMove = await client.PostAsJsonAsync(s3Route + "/entries/move?reference=" + item.Reference,
                    new CloudMoveRequest(null, "occupied.txt"));
                check(failedMove.StatusCode == HttpStatusCode.Conflict && !upstream.S3DeletedSources.Contains(hostname),
                    $"{hostname} leaves the source intact when destination publication conflicts");
                upstream.ChangeS3SourceBeforeDelete = true;
                using var changed = await client.PostAsJsonAsync(s3Route + "/entries/move?reference=" + item.Reference,
                    new CloudMoveRequest(null, "source-changed.txt"));
                upstream.ChangeS3SourceBeforeDelete = false;
                check(changed.StatusCode == HttpStatusCode.Conflict && upstream.S3StaleDeletes.Contains(hostname)
                    && !upstream.S3DeletedSources.Contains(hostname),
                    $"{hostname} reports a conflict and keeps the new source when its ETag changes after publication");
                using var moved = await client.PostAsJsonAsync(s3Route + "/entries/move?reference=" + item.Reference,
                    new CloudMoveRequest(null, "moved.txt"));
                check(moved.StatusCode == HttpStatusCode.NoContent && upstream.S3DeletedSources.Contains(hostname),
                    $"{hostname} deletes the source after conditional publication succeeds");
                using var removed = await client.DeleteAsync(s3Route + "/entries?reference=bW92ZWQudHh0");
                check(removed.StatusCode == HttpStatusCode.NoContent, $"{hostname} deletes the moved object");
                var directory = page.Entries.Single(entry => entry.IsDirectory);
                using var removedDirectory = await client.DeleteAsync(s3Route + "/entries?reference=" + directory.Reference);
                check(removedDirectory.StatusCode == HttpStatusCode.NoContent
                    && upstream.S3DeletedDirectoryKeys[hostname].SetEquals(["/test-bucket/scope/folder/", "/test-bucket/scope/folder/child.txt"]),
                    $"{hostname} deletes the selected directory marker and children across listing pages");
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
                    check(folder.StatusCode == HttpStatusCode.OK
                        && (await folder.Content.ReadFromJsonAsync<CloudCreatedFolder>()) is { Reference.Length: > 0 },
                        $"{protocol} returns a reference to the created folder");
                using (var uploaded = await UploadAsync(client, writerRoute, "new.txt", check))
                    check(uploaded.StatusCode == HttpStatusCode.OK, $"{protocol} publishes a staged file");
                using (var conflict = await UploadAsync(client, writerRoute, "new.txt", check))
                    check(conflict.StatusCode == HttpStatusCode.Conflict, $"{protocol} rejects an existing destination");
                using (var replaced = await UploadAsync(client, writerRoute, "test.txt", check, replace: true))
                    check(replaced.StatusCode == HttpStatusCode.OK, "Baidu replaces the existing file with explicit intent");
                using (var moved = await client.PostAsJsonAsync(writerRoute + "/entries/move?reference=" + entry.Reference,
                           new CloudMoveRequest(null, "occupied.txt")))
                    check(moved.StatusCode == HttpStatusCode.Conflict, "Baidu maps an item-level conflict to HTTP 409");
                using (var moved = await client.PostAsJsonAsync(writerRoute + "/entries/move?reference=" + entry.Reference,
                           new CloudMoveRequest(null, "moved.txt")))
                    check(moved.StatusCode == HttpStatusCode.NoContent, "Baidu confirms successful move");
                using (var deleted = await client.DeleteAsync(writerRoute + "/entries?reference=" + entry.Reference))
                    check(deleted.StatusCode == HttpStatusCode.NoContent, "Baidu confirms successful deletion");
            }
            var davWriter = await CreateAsync(client, dav with { EnableWrites = true });
            upstream.RejectDavUpload = true;
            using (var rejected = await UploadAsync(client, $"/cloud/connections/{davWriter.Id:D}", "new.txt", check))
            {
                var problem = await rejected.Content.ReadFromJsonAsync<RelaxKonOS.Protocol.Common.ProblemDetails>();
                check(rejected.StatusCode == HttpStatusCode.UnprocessableEntity
                    && problem?.Type == CloudStorageApi.CredentialsRejectedProblem,
                    "a staged upload preserves provider credential rejection through commit polling");
            }
            upstream.RejectDavUpload = false;
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
                && !Directory.EnumerateFiles(Path.Combine(root, "uploads")).Any(), "credentials are encrypted and upload staging is cleaned up");
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

    private static async Task VerifyPublishCancellationAsync(HttpClient client, string route, Upstream upstream,
        Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var created = await client.PostAsJsonAsync(route + "/uploads?key=cancel-publish",
            new CreateUploadRequest("", "cancel.txt", 3), ct);
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<UploadSessionDto>(cancellationToken: ct))!;
        var endpoint = route + "/uploads/" + session.UploadId;
        using (var patch = await client.PatchAsync(endpoint + "?offset=0&length=3", new StringContent("new"), ct))
            patch.EnsureSuccessStatusCode();
        upstream.PauseNextDavUpload = true;
        using (var commit = await client.PostAsJsonAsync(endpoint + "/commit", new CommitUploadRequest(), ct))
            check(commit.StatusCode == HttpStatusCode.Accepted, "Cloud upload starts publishing before cancellation");
        await upstream.DavUploadStarted.Task.WaitAsync(ct);
        using (var abort = await client.DeleteAsync(endpoint, ct))
            check(abort.StatusCode == HttpStatusCode.Accepted, "Aborting an active cloud upload signals publication cancellation");
        await upstream.DavUploadCancelled.Task.WaitAsync(ct);
        check(true, "Cloud upload cancellation reaches the provider request");
        while (true)
        {
            using var result = await client.GetAsync(endpoint, ct);
            if (result.StatusCode == HttpStatusCode.NotFound) break;
            result.EnsureSuccessStatusCode();
            await Task.Delay(20, ct);
        }
        check(true, "Cancelled cloud publication releases its upload session");
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string route, string name, Action<bool, string> check, bool replace = false)
    {
        using var created = await client.PostAsJsonAsync($"{route}/uploads?key={Guid.NewGuid():N}&replace={replace}",
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
        private bool _davMoved;
        private bool _baiduUploaded;
        private bool _baiduMoved;
        internal HashSet<string> DavUploadedTreePaths { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> S3DeletedSources { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> S3StaleDeletes { get; } = new(StringComparer.Ordinal);
        internal bool ChangeS3SourceBeforeDelete { get; set; }
        private bool _r2ChangedAfterPublication;
        internal Dictionary<string, HashSet<string>> S3DeletedDirectoryKeys { get; } = new(StringComparer.Ordinal);
        internal HttpStatusCode? DavListingError { get; set; }
        internal string? S3ListingError { get; set; }
        internal bool RejectDavDownload { get; set; }
        internal bool RejectDavUpload { get; set; }
        internal bool PauseNextDavUpload { get; set; }
        internal TaskCompletionSource DavUploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DavUploadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "dav.example")
            {
                if (request.Method.Method == "PROPFIND")
                {
                    if (DavListingError is { } status) return new HttpResponseMessage(status);
                    if (uri.AbsolutePath.StartsWith("/root/tree-download/", StringComparison.Ordinal))
                    {
                        var tree = DavItem(uri.AbsolutePath, true);
                        if (uri.AbsolutePath == "/root/tree-download/")
                            tree += DavItem("/root/tree-download/empty/", true) + DavItem("/root/tree-download/payload.txt", false);
                        return Xml(HttpStatusCode.MultiStatus, $"<multistatus xmlns=\"DAV:\">{tree}</multistatus>");
                    }
                    var files = Enumerable.Range(0, 205).Select(index => DavItem($"/root/file-{index:D3}", false));
                    var responses = string.Concat(files.Append(DavItem(_davMoved ? "/root/moved.txt" : "/root/space%20name.txt", false))
                        .Append(DavItem("/outside.txt", false)).Append(DavItem("/root/", true))
                        .Append(DavItem("https://outside.example/root/external.txt", false)));
                    return Xml(HttpStatusCode.MultiStatus, $"<multistatus xmlns=\"DAV:\">{responses}</multistatus>");
                }
                if (request.Method.Method == "MKCOL")
                {
                    if (uri.AbsolutePath.StartsWith("/root/tree-upload/", StringComparison.Ordinal))
                        DavUploadedTreePaths.Add(uri.AbsolutePath);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                if (request.Method == HttpMethod.Put)
                {
                    if (RejectDavUpload) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    if (PauseNextDavUpload)
                    {
                        PauseNextDavUpload = false;
                        DavUploadStarted.TrySetResult();
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                        finally { if (ct.IsCancellationRequested) DavUploadCancelled.TrySetResult(); }
                    }
                    var stagedBytes = await request.Content!.ReadAsByteArrayAsync(ct);
                    if (uri.AbsolutePath == "/root/space%20name.txt")
                    {
                        check(request.Headers.IfMatch.Single().Tag == "\"dav-etag\""
                            && stagedBytes.AsSpan().SequenceEqual("new"u8), "WebDAV replacement sends the expected ETag and bytes");
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    }
                    check((uri.AbsolutePath.StartsWith("/root/.relaxkonos-upload-", StringComparison.Ordinal)
                            || uri.AbsolutePath.StartsWith("/root/tree-upload/.relaxkonos-upload-", StringComparison.Ordinal))
                        && request.Headers.IfNoneMatch.Single().Tag == "*"
                        && (stagedBytes.AsSpan().SequenceEqual("new"u8) || stagedBytes.AsSpan().SequenceEqual(LargePayload())),
                        "WebDAV PUT writes the complete payload only to a random staging path");
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                if (request.Method.Method == "MOVE")
                {
                    var destination = Uri.UnescapeDataString(request.Headers.GetValues("Destination").Single());
                    if (uri.AbsolutePath == "/root/space%20name.txt")
                    {
                        check(destination == "https://dav.example/root/moved.txt" && request.Headers.GetValues("Overwrite").Single() == "F",
                            "WebDAV move guards the destination against overwrite");
                        _davMoved = true;
                        return new HttpResponseMessage(HttpStatusCode.Created);
                    }
                    check(request.Headers.GetValues("Overwrite").Single() == "F"
                        && destination is "https://dav.example/root/new.txt" or "https://dav.example/root/large.bin"
                            or "https://dav.example/root/new (2).txt" or "https://dav.example/root/tree-upload/payload.txt",
                        "WebDAV MOVE publishes at the requested path without overwriting");
                    if (destination.EndsWith("/tree-upload/payload.txt", StringComparison.Ordinal))
                        DavUploadedTreePaths.Add("/root/tree-upload/payload.txt");
                    if (destination.EndsWith("/large.bin", StringComparison.Ordinal)
                        || destination.EndsWith("/new (2).txt", StringComparison.Ordinal)
                        || destination.EndsWith("/tree-upload/payload.txt", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.Created);
                    if (_davUploaded) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                    _davUploaded = true;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                if (request.Method == HttpMethod.Delete)
                {
                    if (uri.AbsolutePath == "/root/moved.txt" && _davMoved)
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    check(uri.AbsolutePath.StartsWith("/root/.relaxkonos-upload-", StringComparison.Ordinal),
                        "WebDAV conflict cleanup deletes only staging");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (request.Method == HttpMethod.Get && RejectDavDownload)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                if (uri.AbsolutePath == "/root/large.bin")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LargePayload()) };
                check(uri.AbsolutePath is "/root/space%20name.txt" or "/root/tree-download/payload.txt",
                    "WebDAV download preserves the confined encoded path");
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
                    if (fields["method"] == "filemanager")
                    {
                        check(form["async"] == "0" && form["ondup"] == "fail", "Baidu requests synchronous mutation without overwrite");
                        using var list = System.Text.Json.JsonDocument.Parse(form["filelist"].ToString());
                        var item = list.RootElement.EnumerateArray().Single();
                        if (fields["opera"] == "move")
                        {
                            check(item.GetProperty("path").GetString() == "/test.txt" && item.GetProperty("dest").GetString() == "/"
                                && item.GetProperty("ondup").GetString() == "fail", "Baidu sends the selected source and destination");
                            if (item.GetProperty("newname").GetString() == "occupied.txt")
                                return Json("""{"errno":0,"info":[{"errno":-8,"path":"/test.txt"}]}""");
                            _baiduMoved = true;
                        }
                        else check(fields["opera"] == "delete" && item.GetString() == "/moved.txt", "Baidu sends only the selected deletion path");
                        return Json("""{"errno":0,"info":[{"errno":0}]}""");
                    }
                    var replacement = form["path"] == "/test.txt";
                    check(form["rtype"] == (replacement ? "3" : "0"), "Baidu uses the same conflict policy for precreate and create");
                    if (form["isdir"] == "1")
                    {
                        check(form["path"] == "/folder", "Baidu creates the requested directory");
                        return Json("""{"errno":0,"path":"/folder"}""");
                    }
                    if (fields["method"] == "precreate")
                        return _baiduUploaded && !replacement ? Json("""{"errno":-8}""")
                            : Json("""{"errno":0,"return_type":1,"uploadid":"test-upload","block_list":[0]}""");
                    check(form["uploadid"] == "test-upload" && (replacement || form["path"] == "/new.txt"),
                        "Baidu completes the precreated upload at its intended destination");
                    _baiduUploaded = true;
                    return Json("""{"errno":0,"path":"/new.txt"}""");
                }
                if (_baiduMoved && fields["method"] == "filemetas")
                    return Json("""{"errno":0,"list":[{"fs_id":123,"path":"/moved.txt","server_filename":"moved.txt","isdir":0}]}""");
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
            if (S3ListingError is { } error)
                return Xml(HttpStatusCode.Forbidden, $"<Error><Code>{error}</Code></Error>");
            var signature = request.Headers.TryGetValues("Authorization", out var headers) ? headers.Single() : "";
            var scope = signature.Split('/').ElementAtOrDefault(2);
            check(signature.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal)
                && signature.Contains($"/{region}/s3/aws4_request", StringComparison.Ordinal),
                $"S3 connector signs with region {region} (observed {scope ?? "none"})");
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query.TryGetValue("prefix", out var prefix) && prefix == "scope/folder/"
                && query.TryGetValue("max-keys", out var maxKeys) && maxKeys == "1")
                return Xml(HttpStatusCode.OK, "<ListBucketResult><IsTruncated>false</IsTruncated>"
                    + "<Contents><Key>scope/folder/child.txt</Key><Size>1</Size></Contents></ListBucketResult>");
            if (prefix == "scope/folder/")
            {
                var lastPage = query.TryGetValue("continuation-token", out var folderCursor) && folderCursor == "folder-next";
                var folderResult = lastPage
                    ? "<IsTruncated>false</IsTruncated><Contents><Key>scope/folder/child.txt</Key><Size>1</Size><ETag>&quot;child&quot;</ETag></Contents>"
                    : "<IsTruncated>true</IsTruncated><NextContinuationToken>folder-next</NextContinuationToken>"
                        + "<Contents><Key>scope/folder/</Key><Size>0</Size><ETag>&quot;marker&quot;</ETag></Contents>";
                return Xml(HttpStatusCode.OK, "<ListBucketResult>" + folderResult + "</ListBucketResult>");
            }
            if (uri.AbsolutePath is "/test-bucket/scope/report.txt" or "/test-bucket/scope/moved.txt"
                or "/test-bucket/scope/folder/" or "/test-bucket/scope/folder/child.txt"
                && request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("object") };
                var changed = region == "auto" && _r2ChangedAfterPublication && ChangeS3SourceBeforeDelete;
                if (changed) S3StaleDeletes.Add(uri.Host);
                var tag = uri.AbsolutePath switch
                {
                    "/test-bucket/scope/folder/" => "\"marker\"",
                    "/test-bucket/scope/folder/child.txt" => "\"child\"",
                    _ => changed ? "\"new-etag\"" : "\"s3-etag\"",
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(tag);
                return response;
            }
            if (region == "auto" && uri.AbsolutePath == "/test-bucket/scope/report.txt"
                && request.Method == HttpMethod.Get && request.Headers.Range is not null)
            {
                check(request.Headers.Range.ToString() == "bytes=0-5"
                    && request.Headers.GetValues("If-Match").Single().Trim('"') == "s3-etag",
                    "R2 move downloads only the range from the expected source version");
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("object") };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 5, 6);
                return response;
            }
            if (uri.AbsolutePath is "/test-bucket/scope/moved.txt" or "/test-bucket/scope/occupied.txt" or "/test-bucket/scope/source-changed.txt")
            {
                if (request.Method == HttpMethod.Post && query.ContainsKey("uploads"))
                    return Xml(HttpStatusCode.OK, "<InitiateMultipartUploadResult><UploadId>copy-parts</UploadId></InitiateMultipartUploadResult>");
                if (request.Method == HttpMethod.Put && query.ContainsKey("partNumber"))
                {
                    if (region == "auto")
                    {
                        check(await request.Content!.ReadAsStringAsync(ct) == "object", "R2 move uploads the checked source bytes");
                        var response = new HttpResponseMessage(HttpStatusCode.OK);
                        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"part\"");
                        return response;
                    }
                    var sourceKey = Uri.UnescapeDataString(request.Headers.GetValues("x-amz-copy-source").Single()).TrimStart('/');
                    var sourceTag = request.Headers.GetValues("x-amz-copy-source-if-match").Single();
                    check(sourceKey == "test-bucket/scope/report.txt" && sourceTag.Trim('"') == "s3-etag",
                        $"S3 copy constrains the source key and ETag (key: {sourceKey}, tag: {sourceTag})");
                    return Xml(HttpStatusCode.OK, "<CopyPartResult><ETag>&quot;part&quot;</ETag></CopyPartResult>");
                }
                if (request.Method == HttpMethod.Post && query["uploadId"] == "copy-parts")
                {
                    check(request.Headers.IfNoneMatch.Single().Tag == "*", "S3 move conditionally publishes the destination");
                    _r2ChangedAfterPublication = region == "auto" && ChangeS3SourceBeforeDelete;
                    return uri.AbsolutePath.EndsWith("/occupied.txt", StringComparison.Ordinal)
                        ? Xml(HttpStatusCode.PreconditionFailed, "<Error><Code>PreconditionFailed</Code></Error>")
                        : Xml(HttpStatusCode.OK, "<CompleteMultipartUploadResult><ETag>&quot;moved&quot;</ETag></CompleteMultipartUploadResult>");
                }
                if (request.Method == HttpMethod.Delete && query.ContainsKey("uploadId"))
                    return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (uri.AbsolutePath is "/test-bucket/scope/folder/" or "/test-bucket/scope/folder/child.txt")
                {
                    var tag = uri.AbsolutePath.EndsWith('/') ? "\"marker\"" : "\"child\"";
                    check(request.Headers.IfMatch.Single().Tag == tag, "S3 recursive deletion uses the listed object's ETag");
                    if (!S3DeletedDirectoryKeys.TryGetValue(uri.Host, out var paths))
                        S3DeletedDirectoryKeys.Add(uri.Host, paths = new(StringComparer.Ordinal));
                    paths.Add(uri.AbsolutePath);
                    return new(HttpStatusCode.NoContent);
                }
                check(uri.AbsolutePath is "/test-bucket/scope/report.txt" or "/test-bucket/scope/moved.txt"
                    && request.Headers.IfMatch.Single().Tag == "\"s3-etag\"", "S3 deletes exactly the selected object with its expected ETag");
                if (uri.AbsolutePath.EndsWith("/report.txt", StringComparison.Ordinal))
                {
                    if (ChangeS3SourceBeforeDelete)
                    {
                        check(region != "auto", "R2 must detect a changed source before sending deletion");
                        S3StaleDeletes.Add(uri.Host);
                        return Xml(HttpStatusCode.PreconditionFailed, "<Error><Code>PreconditionFailed</Code></Error>");
                    }
                    S3DeletedSources.Add(uri.Host);
                }
                return new(HttpStatusCode.NoContent);
            }
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
                var replacement = uri.AbsolutePath == "/test-bucket/scope/replace.txt";
                var createOnly = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag == "*";
                check((replacement || uri.AbsolutePath == "/test-bucket/scope/new.txt" && createOnly)
                    && await request.Content!.ReadAsStringAsync(ct) == "new", "S3 PUT stays within the connection prefix");
                if (replacement && createOnly)
                    return Xml(HttpStatusCode.PreconditionFailed, "<Error><Code>PreconditionFailed</Code></Error>");
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
            + "<getetag>&quot;dav-etag&quot;</getetag></prop></propstat></response>";

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
        public string HostApiVersion => ModuleHostProtocol.HostApiVersion;
        public IModuleHostStorage Storage => this;
        public ICloudFileProviders CloudProviders { get; set; } = null!;
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

    private static async Task<WebApplication> StartProvidersAsync(TestContext context, CancellationToken ct,
        params (CloudFileProvider Kind, IModuleHostHttp Module)[] entries)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var provider = builder.Build();
        foreach (var entry in entries)
        {
            provider.MapGet(CloudProviderProtocol.RequestPath(entry.Kind), () => Results.NoContent());
            entry.Module.MapEndpoints(provider, context);
        }
        await provider.StartAsync(ct);
        context.CloudProviders = new CloudProviderClient(provider.Urls.Single(), new string('a', 64));
        return provider;
    }
}
