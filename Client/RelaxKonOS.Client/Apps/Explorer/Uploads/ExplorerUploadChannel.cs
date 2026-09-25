using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Uploads;

/// <summary>
/// The upload data plane over its own <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// Why this does not reuse the Explorer client: that client's pipeline includes
/// <c>AuthenticatedHttpHandler</c>, which reads the whole request body into a byte array before the first
/// send so it can replay the request after a 401. For a 20 GB file that is an out-of-memory failure, and
/// it also makes the progress bar reach 100% while the data is still in RAM. The resumable protocol makes
/// the buffer unnecessary: a refused token is retried by re-reading the session offset and continuing,
/// which needs no body copy at all.
/// </remarks>
public sealed class ExplorerUploadChannel(HttpClient http, IAuthSession session) : IExplorerUploadChannel
{
    public async Task<UploadSessionDto> CreateSessionAsync(CreateUploadRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(FileApiRoutes.Uploads))
        {
            Content = JsonContent.Create(request, options: RelaxKonOSJsonOptions.Default),
        };
        message.Headers.TryAddWithoutValidation(FileUploadProtocol.IdempotencyKeyHeader, idempotencyKey);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await ReadAsync<UploadSessionDto>(response, ct).ConfigureAwait(false);
    }

    public async Task<UploadSessionDto> GetSessionAsync(string uploadId, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(SessionRoute(uploadId)));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await ReadAsync<UploadSessionDto>(response, ct).ConfigureAwait(false);
    }

    public async Task<long> SendChunkAsync(string uploadId, long offset, long length, Stream source,
        Action? onInFlight, CancellationToken ct = default)
    {
        // The content owns the length, which is what lets the server locate the end of the chunk without
        // a form reader or a byte buffer.
        using var content = new ChunkContent(source, offset, length, onInFlight);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var message = new HttpRequestMessage(HttpMethod.Patch, BuildUri(SessionRoute(uploadId))) { Content = content };
        message.Headers.TryAddWithoutValidation(FileUploadProtocol.OffsetHeader,
            offset.ToString(CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct).ConfigureAwait(false);
        // The header is the server's own number. Falling back to local arithmetic would confirm bytes that
        // the server never flushed, which is exactly what resumption must not do.
        return ReadOffset(response) ?? offset + length;
    }

    public async Task<FileEntryDto> CommitAsync(string uploadId, string? contentHash, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri($"{SessionRoute(uploadId)}/commit"))
        {
            Content = JsonContent.Create(new CommitUploadRequest(contentHash), options: RelaxKonOSJsonOptions.Default),
        };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await ReadAsync<FileEntryDto>(response, ct).ConfigureAwait(false);
    }

    public async Task AbortAsync(string uploadId, CancellationToken ct = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, BuildUri(SessionRoute(uploadId)));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            // Abandoning is best effort by contract: the session's lifetime is what actually guarantees the
            // staging file goes away, so a failure here must not fail the user's cancel.
        }
    }

    public async Task<bool> RefreshSessionAsync(CancellationToken ct = default)
    {
        try { return await session.GetAccessTokenAsync(TimeSpan.Zero, ct: ct).ConfigureAwait(false) is not null; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException) { return false; }
    }

    private static string SessionRoute(string uploadId) => $"{FileApiRoutes.Uploads}/{Uri.EscapeDataString(uploadId)}";

    private Uri BuildUri(string route)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("explorer.error.not_signed_in"));
        return new Uri(new Uri(session.ServerUrl, UriKind.Absolute), route.TrimStart('/'));
    }

    private static long? ReadOffset(HttpResponseMessage response)
        => response.Headers.TryGetValues(FileUploadProtocol.OffsetHeader, out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            ? offset : null;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new UploadChannelException(null, (int)response.StatusCode, null, isTransport: true,
                LocalizedText.Get("common.error.empty_response_detail"));
    }

    /// <summary>
    /// Turns a refusal into the two facts the orchestrator needs. A 5xx without a RelaxKonOS problem code
    /// is a transport failure, not a verdict, matching how every other client call reads a 5xx.
    /// </summary>
    private static async Task<UploadChannelException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<UploadProblemBody>(RelaxKonOSJsonOptions.Default, ct)
                .ConfigureAwait(false);
            code = problem?.ProblemCode
                ?? (problem?.Type?.StartsWith("https://relaxkonos.app/problems/", StringComparison.Ordinal) == true
                    ? problem.Type["https://relaxkonos.app/problems/".Length..] : null);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException)
        {
            // A body that is not our contract names nothing, so the status is all there is to go on.
        }

        var status = (int)response.StatusCode;
        var isTransport = status >= 500 && code is null;
        return new UploadChannelException(code, status, ReadOffset(response), isTransport,
            code ?? $"HTTP {status} {response.ReasonPhrase}");
    }

    private sealed record UploadProblemBody(string? Type, string? Title, string? ProblemCode);

    /// <summary>
    /// A fixed-length view of one byte range of a seekable file. It reports bytes as they are handed to the
    /// socket, which is what both the progress estimate and the stall watchdog measure. The source stream is
    /// owned by the caller, which reuses it for the next chunk, so disposing this content never closes it.
    /// </summary>
    private sealed class ChunkContent(Stream source, long offset, long length, Action? onInFlight) : HttpContent
    {
        protected override bool TryComputeLength(out long contentLength)
        {
            contentLength = length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => CopyAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => CopyAsync(stream, cancellationToken);

        private async Task CopyAsync(Stream destination, CancellationToken cancellationToken)
        {
            // Seeking here rather than trusting the caller's position makes the content safe to serialize
            // again (a redirect or a framework retry would otherwise send an empty tail).
            if (!source.CanSeek) throw new InvalidOperationException("大文件分块上传要求可定位的本地文件。");
            source.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[81_920];
            long remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) throw new IOException("本地文件在传输过程中被截断。");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                onInFlight?.Invoke();
            }
        }
    }
}
