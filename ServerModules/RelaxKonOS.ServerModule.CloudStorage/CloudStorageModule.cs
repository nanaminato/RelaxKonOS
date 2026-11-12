using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

public sealed partial class CloudStorageModule : IModuleHostEntry, IModuleHostHttp
{
    private const string Store = "connections.bin";
    private const string KeyPath = "connections.key";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int EnvelopeSize = 1 + NonceSize + TagSize;
    private const int MaxStoreBytes = 1024 * 1024;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim[] _oauthWrites = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    // Serialize refreshes per connection stripe, not per provider or user.
    private readonly SemaphoreSlim[] _oauthGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<Guid, (CloudConnectionRequest Settings, string Token, DateTimeOffset Expires)> _oauthTokens = new();
    private CloudUploadSessions _uploads = null!;
    private readonly Func<Uri, bool, HttpClient> _transport;

    public CloudStorageModule() : this(CloudTransport.Create) { }

    internal CloudStorageModule(Func<Uri, bool, HttpClient> transport) => _transport = transport;
    private StoredConnection[] _connections = [];
    private byte[] _key = [];

    public string Id => CloudStorageApi.ModuleId;

    public async ValueTask StartAsync(IModuleHostContext context, CancellationToken ct)
    {
        _context = context;
        _uploads = new CloudUploadSessions(context.ModuleDataPath);
        _uploads.StartSweep();
        var bytes = await context.Storage.ReadAsync(Store, MaxStoreBytes, ct);
        var key = await context.Storage.ReadAsync(KeyPath, 32, ct);
        if (key is null && bytes is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            await context.Storage.WriteAsync(KeyPath, key, ct);
        }
        _key = key is { Length: 32 } ? key : throw new InvalidDataException(KeyPath);
        await LoadAuthorizationAsync(ct);
        if (bytes is null) return;
        if (bytes.Length <= EnvelopeSize || bytes[0] != 1) throw new InvalidDataException(Store);
        var plaintext = new byte[bytes.Length - EnvelopeSize];
        try
        {
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Decrypt(bytes.AsSpan(1, NonceSize), bytes.AsSpan(EnvelopeSize), bytes.AsSpan(1 + NonceSize, TagSize), plaintext);
            _connections = JsonSerializer.Deserialize<StoredConnection[]>(plaintext) ?? throw new InvalidDataException(Store);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public ValueTask StopAsync(CancellationToken ct) => new(_uploads.StopAsync(ct));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context)
    {
        var group = endpoints.MapGroup("/cloud");
        MapAuthorizationEndpoints(group);
        group.MapGet("/connections", (Delegate)((HttpContext http) => WithUser(http, user =>
            Task.FromResult<IResult>(Results.Ok(Volatile.Read(ref _connections)
                .Where(item => item.Owner == user).Select(Describe).ToArray())))));
        group.MapPost("/connections", (CloudConnectionRequest request, HttpContext http) => WithUser(http, async user =>
        {
            var settings = Validate(request, IsController(http));
            var item = new StoredConnection(Guid.NewGuid(), user, settings, Guid.NewGuid());
            await ChangeAsync(context, items =>
            {
                if (items.Length >= 128) throw new ArgumentException("connection limit");
                return [.. items, item];
            }, http.RequestAborted);
            return Results.Ok(Describe(item));
        }));
        group.MapPut("/connections/{id:guid}", (Guid id, CloudConnectionRequest request, HttpContext http) =>
            WithUser(http, async user =>
            {
                var settings = Validate(request, IsController(http));
                var item = new StoredConnection(id, user, settings, Guid.NewGuid());
                await ChangeAsync(context, items =>
                {
                    if (!items.Any(current => current.Id == id && current.Owner == user))
                        throw new KeyNotFoundException();
                    return items.Select(current => current.Id == id ? item : current).ToArray();
                }, http.RequestAborted);
                return Results.Ok(Describe(item));
            }));
        group.MapDelete("/connections/{id:guid}", (Guid id, HttpContext http) => WithUser(http, async user =>
        {
            await ChangeAsync(context, items => items.Where(item => item.Id != id || item.Owner != user).ToArray(), http.RequestAborted);
            return Results.NoContent();
        }));
        group.MapGet("/connections/{id:guid}/entries", (Guid id, string? reference, string? cursor, HttpContext http) =>
            WithConnection(http, id, reference, async (settings, client, path, ct, oauth) =>
            {
                if (cursor?.Length > 8192) throw new ArgumentException("cursor");
                if (oauth is not null) return Results.Ok(await oauth.ListAsync(path, cursor, ct));
                if (settings.Protocol == "webdav")
                    return Results.Ok(await new WebDavConnector(settings, client).ListAsync(path, cursor, ct));
                using var connector = new S3Connector(settings, client);
                return Results.Ok(await connector.ListAsync(path, cursor, ct));
            }));
        group.MapGet("/connections/{id:guid}/info", (Guid id, string? reference, HttpContext http) =>
            WithConnection(http, id, reference, async (settings, client, path, ct, oauth) =>
            {
                if (oauth is not null)
                {
                    var metadata = await oauth.InfoAsync(path, ct);
                    return metadata is null ? Results.NotFound() : Results.Ok(metadata);
                }
                if (settings.Protocol == "webdav")
                {
                    var info = await new WebDavConnector(settings, client).InfoAsync(path, ct);
                    return info is null ? Results.NotFound() : Results.Ok(info);
                }
                using var connector = new S3Connector(settings, client);
                var entry = await connector.InfoAsync(path, ct);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }));
        group.MapPost("/connections/{id:guid}/folders", (Guid id, string? reference, string name, HttpContext http) =>
            WithConnection(http, id, reference, async (settings, client, parent, ct, oauth) =>
            {
                if (!settings.EnableWrites) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var path = ChildPath(parent, name) + "/";
                if (oauth is not null)
                    await WithOAuthWriteAsync(id, path, () => oauth.CreateFolderAsync(path, ct), ct);
                else if (settings.Protocol == "webdav")
                    await new WebDavConnector(settings, client).CreateFolderAsync(path, ct);
                else
                {
                    using var connector = new S3Connector(settings, client);
                    await connector.UploadAsync(path, Stream.Null, ct);
                }
                return Results.NoContent();
            }));
        const string uploads = "/connections/{id:guid}/uploads";
        group.MapPost(uploads, (Guid id, string key, CreateUploadRequest request, HttpContext http) =>
            WithUpload(http, id, (item, revision) => _uploads.CreateAsync(item.Owner, id, revision, key,
                ChildPath(CloudTransport.PathFrom(request.TargetDirectoryPath), request.FileName), request.Length,
                request.LastModifiedUtc, http.RequestAborted)));
        group.MapGet(uploads + "/{uploadId}", (Guid id, string uploadId, HttpContext http) =>
            UploadOperation(http, id, uploadId, "get"));
        group.MapMethods(uploads + "/{uploadId}", ["PATCH"],
            (Guid id, string uploadId, long offset, long length, HttpContext http) =>
                UploadOperation(http, id, uploadId, "append", offset, length));
        group.MapDelete(uploads + "/{uploadId}", (Guid id, string uploadId, HttpContext http) =>
            UploadOperation(http, id, uploadId, "abort"));
        group.MapPost(uploads + "/{uploadId}/commit", (Guid id, string uploadId, CommitUploadRequest? request, HttpContext http) =>
            UploadOperation(http, id, uploadId, "commit", contentHash: request?.ContentHash));
        group.MapGet("/connections/{id:guid}/download", (Guid id, string? reference, HttpContext http) =>
            WithConnection(http, id, reference, async (settings, client, path, ct, oauth) =>
            {
                if (oauth is not null)
                {
                    await oauth.DownloadAsync(path, (fileName, content) =>
                        Results.Stream(content, "application/octet-stream", fileName).ExecuteAsync(http), ct);
                    return Results.Empty;
                }
                var name = path.Split('/')[^1];
                if (settings.Protocol == "webdav")
                {
                    using var response = await new WebDavConnector(settings, client).DownloadAsync(path, ct);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    await Results.Stream(stream, "application/octet-stream", name).ExecuteAsync(http);
                }
                else
                {
                    using var connector = new S3Connector(settings, client);
                    using var response = await connector.DownloadAsync(path, ct);
                    await Results.Stream(response.ResponseStream, "application/octet-stream", name).ExecuteAsync(http);
                }
                return Results.Empty;
            }));
    }

    private Task<IResult> WithUpload(HttpContext http, Guid id, Func<StoredConnection, string, Task<IResult>> action) =>
        WithUser(http, async user =>
        {
            var item = Volatile.Read(ref _connections).FirstOrDefault(connection => connection.Id == id && connection.Owner == user);
            if (item is null) return Results.NotFound();
            if (!item.Settings.EnableWrites) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var revision = OAuthDriveConnector.Supports(item.Settings.Protocol) ? item.Revision.ToString("N")
                : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item.Settings)));
            return await action(item, revision);
        });

    private Task<IResult> UploadOperation(HttpContext http, Guid id, string uploadId, string action,
        long? offset = null, long? length = null, string? contentHash = null) =>
        WithUpload(http, id, (item, revision) => _uploads.HandleAsync(http, item.Owner, id, revision, uploadId,
            action, offset, length, contentHash, async (path, stream, stopping) =>
            {
                if (!Volatile.Read(ref _connections).Any(current => current.Id == item.Id && current.Revision == item.Revision
                    && current.Settings.EnableWrites))
                    throw new InvalidOperationException("cloud-storage-connection-changed");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                deadline.CancelAfter(TimeSpan.FromHours(2));
                var settings = item.Settings;
                if (OAuthDriveConnector.Supports(settings.Protocol))
                {
                    await WithOAuthWriteAsync(id, path, async () =>
                    {
                        var token = await GetOAuthTokenAsync(id, item.Owner, settings.Protocol, item.Revision, deadline.Token);
                        await new OAuthDriveConnector(settings.Protocol, token, _transport).UploadAsync(path, stream, deadline.Token);
                    }, deadline.Token);
                    return;
                }
                using var client = _transport(new Uri(settings.Endpoint), settings.AllowPrivateWebDav);
                client.Timeout = Timeout.InfiniteTimeSpan;
                if (settings.Protocol == "webdav")
                    await new WebDavConnector(settings, client).UploadAsync(path, stream, deadline.Token);
                else
                {
                    using var connector = new S3Connector(settings, client);
                    await connector.UploadAsync(path, stream, deadline.Token);
                }
            }));

    private Task<IResult> WithConnection(HttpContext http, Guid id, string? reference,
        Func<CloudConnectionRequest, HttpClient, string, CancellationToken, OAuthDriveConnector?, Task<IResult>> action) =>
        WithUser(http, async user =>
        {
            var item = Volatile.Read(ref _connections).FirstOrDefault(connection => connection.Id == id && connection.Owner == user);
            if (item is null) return Results.NotFound();
            using var client = _transport(new Uri(item.Settings.Endpoint), item.Settings.AllowPrivateWebDav);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            if (!http.Request.Path.Value!.EndsWith("/download", StringComparison.Ordinal))
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var path = CloudTransport.PathFrom(reference);
            OAuthDriveConnector? oauth = null;
            if (OAuthDriveConnector.Supports(item.Settings.Protocol) && (HttpMethods.IsGet(http.Request.Method) || item.Settings.EnableWrites))
            {
                var token = await GetOAuthTokenAsync(id, user, item.Settings.Protocol, item.Revision, deadline.Token);
                oauth = new OAuthDriveConnector(item.Settings.Protocol, token, _transport);
            }
            return await action(item.Settings, client, path, deadline.Token, oauth);
        });

    private async Task WithOAuthWriteAsync(Guid connection, string path, Func<Task> write, CancellationToken ct)
    {
        var stripe = (uint)HashCode.Combine(connection, path.TrimEnd('/')) % (uint)_oauthWrites.Length;
        var gate = _oauthWrites[stripe];
        await gate.WaitAsync(ct);
        try { await write(); }
        finally { gate.Release(); }
    }

    private IModuleHostContext _context = null!;

    private async Task<string> GetOAuthTokenAsync(Guid id, Guid owner, string protocol, Guid revision, CancellationToken ct)
    {
        var gate = _oauthGates[(uint)id.GetHashCode() % (uint)_oauthGates.Length];
        await gate.WaitAsync(ct);
        try
        {
            var connections = Volatile.Read(ref _connections);
            foreach (var stale in _oauthTokens.Keys.Where(key => !connections.Any(item => item.Id == key)).ToArray())
                _oauthTokens.TryRemove(stale, out _);
            var item = connections.FirstOrDefault(connection => connection.Id == id && connection.Owner == owner)
                ?? throw new KeyNotFoundException();
            var settings = item.Settings;
            if (settings.Protocol != protocol || item.Revision != revision) throw new KeyNotFoundException();
            if (_oauthTokens.TryGetValue(id, out var cached) && ReferenceEquals(cached.Settings, settings)
                && cached.Expires > DateTimeOffset.UtcNow.AddSeconds(30))
                return cached.Token;
            var (token, refresh, expires) = await OAuthDriveConnector.RefreshAsync(settings, _transport, ct);
            if (refresh != settings.SessionToken)
            {
                settings = settings with { SessionToken = refresh };
                var replacement = item with { Settings = settings };
                await ChangeAsync(_context, current =>
                {
                    if (!current.Contains(item)) throw new KeyNotFoundException();
                    return current.Select(connection => connection == item ? replacement : connection).ToArray();
                }, CancellationToken.None);
            }
            if (!Volatile.Read(ref _connections).Any(connection => connection.Id == id && ReferenceEquals(connection.Settings, settings)))
                throw new KeyNotFoundException();
            _oauthTokens[id] = (settings, token, expires);
            return token;
        }
        finally { gate.Release(); }
    }

    private static async Task<IResult> WithUser(HttpContext http, Func<Guid, Task<IResult>> action)
    {
        if (!Guid.TryParse(http.Request.Headers["X-RelaxKonOS-Module-User"].ToString(), out var user))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        try { return await action(user); }
        catch (Exception error) when (http.Response.HasStarted && error is IOException or HttpRequestException or AmazonServiceException)
        {
            http.Abort();
            return Results.Empty;
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { return Results.Problem(statusCode: 400, title: "cloud-storage-invalid-request"); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        { return Results.Problem(statusCode: 504, title: "cloud-storage-timeout"); }
        catch (Exception error) when (error is HttpRequestException or AmazonServiceException or System.Xml.XmlException or JsonException or InvalidDataException)
        {
            var status = error switch
            {
                HttpRequestException request => request.StatusCode,
                AmazonServiceException aws => aws.StatusCode,
                _ => null,
            };
            return status switch
            {
                HttpStatusCode.NotFound => Results.NotFound(),
                HttpStatusCode.Forbidden => Results.Problem(statusCode: 403, title: "cloud-storage-upstream-denied"),
                HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict =>
                    Results.Problem(statusCode: 409, title: "cloud-storage-conflict"),
                _ => Results.Problem(statusCode: 502, title: "cloud-storage-upstream-failed"),
            };
        }
    }

    private async Task ChangeAsync(IModuleHostContext context, Func<StoredConnection[], StoredConnection[]> change, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            var updated = change(_connections);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(updated);
            try
            {
                if (plaintext.Length > MaxStoreBytes - EnvelopeSize) throw new ArgumentException("connection limit");
                var bytes = new byte[plaintext.Length + EnvelopeSize];
                bytes[0] = 1;
                RandomNumberGenerator.Fill(bytes.AsSpan(1, NonceSize));
                using var cipher = new AesGcm(_key, TagSize);
                cipher.Encrypt(bytes.AsSpan(1, NonceSize), plaintext, bytes.AsSpan(EnvelopeSize), bytes.AsSpan(1 + NonceSize, TagSize));
                await context.Storage.WriteAsync(Store, bytes, ct);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            Volatile.Write(ref _connections, updated);
        }
        finally { _write.Release(); }
    }

    private static string ChildPath(string parent, string name)
    {
        if (parent.Length > 0 && !parent.EndsWith('/') || string.IsNullOrWhiteSpace(name)
            || name.Contains('/') || !CloudTransport.ValidPath(name)
            || !CloudTransport.ValidPath(parent + name))
            throw new ArgumentException("entry name");
        return parent + name;
    }

    private static CloudConnectionDto Describe(StoredConnection item) =>
        new(item.Id, item.Settings.Name, item.Settings.Protocol, item.Settings.EnableWrites);

    private static bool IsController(HttpContext http) => http.Request.Headers["X-RelaxKonOS-Module-Role"] == "admin";

    private static CloudConnectionRequest Validate(CloudConnectionRequest input, bool controller)
    {
        if (OAuthDriveConnector.Supports(input.Protocol))
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Name.Any(char.IsControl)
                || !ValidOAuthField(input.UserName, 512) || !ValidOAuthField(input.Secret, 2048)
                || !ValidOAuthField(input.SessionToken, 8192) || input.AllowPrivateWebDav)
                throw new ArgumentException("oauth connection");
            return input with
            {
                Name = input.Name.Trim(),
                Endpoint = input.Protocol == "google-drive" ? "https://www.googleapis.com" : "https://pan.baidu.com",
                Region = null, Bucket = null, Prefix = null,
            };
        }
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Name.Any(char.IsControl)
            || input.Protocol is not ("webdav" or "s3") || input.Endpoint is not { Length: > 0 and <= 2048 }
            || !Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(input.UserName) || input.UserName.Length > 256 || input.UserName.Any(char.IsControl)
            || string.IsNullOrEmpty(input.Secret) || input.Secret.Length > 2048 || input.Secret.Any(char.IsControl))
            throw new ArgumentException("connection");
        if (input.Protocol == "webdav")
        {
            if (input.UserName.Contains(':') || input.AllowPrivateWebDav && !controller)
                throw new ArgumentException("webdav authorization");
            return input with { Name = input.Name.Trim(), Endpoint = endpoint.AbsoluteUri, Region = null,
                Bucket = null, Prefix = null, SessionToken = null };
        }
        if (endpoint.AbsolutePath != "/" || input.AllowPrivateWebDav
            || input.Region is not { Length: > 0 and <= 64 } region || !Regex.IsMatch(region, "^[a-z0-9-]+$")
            || input.Bucket is not { Length: >= 3 and <= 63 } bucket
            || !Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9.-]*[a-z0-9]$")
            || input.Prefix is { } prefix && !CloudTransport.ValidPath(prefix)
            || input.SessionToken is { Length: > 8192 } || input.SessionToken?.Any(char.IsControl) == true)
            throw new ArgumentException("s3 authorization");
        return input with { Name = input.Name.Trim(), Endpoint = endpoint.GetLeftPart(UriPartial.Authority) };
    }

    private static bool ValidOAuthField(string? value, int limit) =>
        value is { Length: > 0 } && value.Length <= limit && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private sealed record StoredConnection(Guid Id, Guid Owner, CloudConnectionRequest Settings, Guid Revision);
}
