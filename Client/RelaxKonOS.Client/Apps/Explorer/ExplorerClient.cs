using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>IExplorerClient 的 typed HttpClient 实现。
/// 不 mutate HttpClient.BaseAddress，每个请求用 <see cref="IAuthSession.ServerUrl"/> 构造绝对 URI（避免共享实例并发竞态）。
/// Authorization 头从 <see cref="IAuthSession.Tokens"/> 取；未登录抛 <see cref="InvalidOperationException"/>。
/// 失败读 ProblemDetails 抛 <see cref="RemoteOsAuthException"/>（与 <see cref="RemoteOsClient"/> 同源）。</summary>
public sealed class ExplorerClient : IExplorerClient
{
    private readonly HttpClient _http;
    private readonly IAuthSession _session;

    public ExplorerClient(HttpClient http, IAuthSession session)
    {
        _http = http;
        _session = session;
    }

    public Task<FileOperationDto> StartOperationAsync(StartFileOperationRequest request, CancellationToken ct = default)
        => SendAsync<FileOperationDto>(HttpMethod.Post, FileApiRoutes.Operations, body: request, ct: ct);
    public Task<IReadOnlyList<FileOperationDto>> ListOperationsAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<FileOperationDto>>(HttpMethod.Get, FileApiRoutes.Operations, ct: ct);
    public Task<FileOperationDto> GetOperationAsync(Guid id, CancellationToken ct = default)
        => SendAsync<FileOperationDto>(HttpMethod.Get, $"{FileApiRoutes.Operations}/{id}", ct: ct);
    public Task<FileOperationDto> CancelOperationAsync(Guid id, CancellationToken ct = default)
        => SendAsync<FileOperationDto>(HttpMethod.Post, $"{FileApiRoutes.Operations}/{id}/cancel", ct: ct);
    public Task<FileOperationDto> DecideOperationAsync(Guid id, FileOperationDecisionRequest request, CancellationToken ct = default)
        => SendAsync<FileOperationDto>(HttpMethod.Post, $"{FileApiRoutes.Operations}/{id}/decision", body: request, ct: ct);

    public Task<IReadOnlyList<DriveDto>> GetDrivesAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<DriveDto>>(HttpMethod.Get, FileApiRoutes.Drives, ct: ct);

    public Task<IReadOnlyList<SpecialLocationDto>> GetSpecialLocationsAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<SpecialLocationDto>>(HttpMethod.Get, FileApiRoutes.Special, ct: ct);

    public Task<DirectoryDto> GetDirectoryAsync(string? path, CancellationToken ct = default)
        => SendAsync<DirectoryDto>(HttpMethod.Get, FileApiRoutes.List, query: ("path", path), ct: ct);

    public async Task<FileSystemEntryDto?> GetInfoAsync(string path, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Get, FileApiRoutes.Info, query: ("path", path), ct: ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await ReadAsync<FileSystemEntryDto>(resp, ct);
    }

    public async Task<(Stream Stream, string FileName)?> DownloadAsync(string path, CancellationToken ct = default)
    {
        var resp = await SendRawAsync(HttpMethod.Get, FileApiRoutes.Download, query: ("path", path), ct: ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            resp.Dispose();
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            try { await EnsureSuccessAsync(resp, ct); }
            finally { resp.Dispose(); }
        }
        var fileName = ContentDispositionFileName(resp.Content.Headers.ContentDisposition) ?? "download";
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return (new ResponseOwnedStream(stream, resp), fileName);
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Get, FileApiRoutes.Content, query: ("path", path), ct: ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public Task<FileElevationResult> ElevateFileAccessAsync(string path, FileElevationCapability capability, string? password = null, CancellationToken ct = default)
        => SendAsync<FileElevationResult>(HttpMethod.Post, FileApiRoutes.Elevation,
            body: new FileElevationRequest(path, password, Capability: capability), ct: ct);

    public Task<FileElevationResult> ElevateFileOperationAsync(IReadOnlyList<string> directoryPaths, FileElevationCapability capability, string? password = null, CancellationToken ct = default)
    {
        if (directoryPaths.Count == 0) throw new ArgumentException("At least one directory path is required.", nameof(directoryPaths));
        return SendAsync<FileElevationResult>(HttpMethod.Post, FileApiRoutes.Elevation,
            body: new FileElevationRequest(directoryPaths[0], password, directoryPaths.Skip(1).ToArray(), IncludeDescendants: true, Capability: capability), ct: ct);
    }

    public async Task<FileEntryDto> WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var serverUrl = RequireSession();
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildUri(serverUrl, FileApiRoutes.Content, ("path", path)))
        {
            Content = new ByteArrayContent(content),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Tokens!.AccessToken);
        using var resp = await _http.SendAsync(req, ct);
        return await ReadAsync<FileEntryDto>(resp, ct);
    }

    public async Task<FilePropertiesDto?> GetPropertiesAsync(string path, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Get, FileApiRoutes.Properties, query: ("path", path), ct: ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await ReadAsync<FilePropertiesDto>(resp, ct);
    }

    public Task<FilePropertiesDto> SetUnixPermissionsAsync(string path, int unixMode, CancellationToken ct = default)
        => SendAsync<FilePropertiesDto>(HttpMethod.Put, FileApiRoutes.Permissions,
            body: new UpdateUnixPermissionsRequest(path, unixMode), ct: ct);

    public Task<FileSystemEntryDto> CreateDirectoryAsync(string path, CancellationToken ct = default)
        => SendAsync<FileSystemEntryDto>(HttpMethod.Post, FileApiRoutes.Directory, query: ("path", path), ct: ct);

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, FileApiRoutes.Delete, query: ("path", path), ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken ct = default)
        => SendAsync<FileSystemEntryDto>(HttpMethod.Post, FileApiRoutes.Rename,
            body: new RenameRequest(sourcePath, newName), ct: ct);

    public Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
        => SendAsync<FileSystemEntryDto>(HttpMethod.Post, FileApiRoutes.Move,
            body: new MoveRequest(sourcePath, destinationPath, overwrite), ct: ct);

    public Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
        => SendAsync<FileSystemEntryDto>(HttpMethod.Post, FileApiRoutes.Copy,
            body: new CopyRequest(sourcePath, destinationPath, overwrite), ct: ct);

    public async Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var serverUrl = RequireSession();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ProgressStreamContent(content, progress);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(serverUrl, FileApiRoutes.Upload, ("path", targetDirectoryPath)))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _session.Tokens!.AccessToken) },
            Content = form,
        };
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await ReadAsync<FileEntryDto>(resp, ct);
    }

    /// <summary>Streams multipart content while reporting bytes accepted by the HTTP request body.</summary>
    private sealed class ProgressStreamContent(Stream source, IProgress<long>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            if (!source.CanSeek) { length = 0; return false; }
            length = source.Length - source.Position;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => CopyContentToAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => CopyContentToAsync(stream, cancellationToken);

        private async Task CopyContentToAsync(Stream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[81_920];
            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                progress?.Report(sent);
            }
        }
    }

    // ---- helpers ----

    private async Task<T> SendAsync<T>(HttpMethod method, string route,
        (string Key, string? Value)? query = null, object? body = null, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(method, route, query, body, ct);
        return await ReadAsync<T>(resp, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string route,
        (string Key, string? Value)? query = null, object? body = null, CancellationToken ct = default)
    {
        var serverUrl = RequireSession();
        using var req = new HttpRequestMessage(method, BuildUri(serverUrl, route, query));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Tokens!.AccessToken);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: RemoteOsJsonOptions.Default);
        return await _http.SendAsync(req, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode) await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, ct)
            ?? throw new RemoteOsAuthException(NoBodyProblem());
    }

    private string RequireSession()
    {
        if (_session.State != AuthSessionState.Authenticated || _session.Tokens is null || _session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("explorer.error.not_signed_in"));
        return _session.ServerUrl;
    }

    private static Uri BuildUri(string serverUrl, string route, (string Key, string? Value)? query = null)
    {
        var baseUri = new Uri(serverUrl, UriKind.Absolute);
        var uri = new Uri(baseUri, route.TrimStart('/'));
        if (query is null || string.IsNullOrEmpty(query.Value.Value)) return uri;
        // 手动拼接 query string（避免依赖 Microsoft.AspNetCore.Http.QueryString，客户端不可用）
        var qb = Uri.EscapeDataString(query.Value.Key) + "=" + Uri.EscapeDataString(query.Value.Value!);
        return new UriBuilder(uri) { Query = qb }.Uri;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        ProblemDetails? problem = null;
        try { problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(RemoteOsJsonOptions.Default, ct); }
        catch { /* 非 JSON 错误体回退通用错误 */ }
        throw problem is null
            ? new RemoteOsAuthException(new ProblemDetails(
                "https://remoteos.app/problems/http-error", $"HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode, resp.ReasonPhrase, null))
            : new RemoteOsAuthException(problem);
    }

    private static string? ContentDispositionFileName(ContentDispositionHeaderValue? cd)
        => cd?.FileNameStar ?? cd?.FileName;

    /// <summary>Keeps the HTTP response alive for the full lifetime of its download stream.</summary>
    private sealed class ResponseOwnedStream(Stream stream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => stream.CanWrite;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() => stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => stream.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => stream.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => stream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => stream.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => stream.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => stream.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private static ProblemDetails NoBodyProblem()
        => new("https://remoteos.app/problems/empty-response", LocalizedText.Get("common.error.empty_response_title"), 500, LocalizedText.Get("common.error.empty_response_detail"), null);
}
