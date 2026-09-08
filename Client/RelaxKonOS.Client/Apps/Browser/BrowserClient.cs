using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.Browser;

/// <summary>IBrowserClient 的 typed HttpClient 实现。
/// 不 mutate HttpClient.BaseAddress，每个请求用 <see cref="IAuthSession.ServerUrl"/> 构造绝对 URI（避免共享实例并发竞态）。
/// Authorization 头从 <see cref="IAuthSession.Tokens"/> 取；未登录抛 <see cref="InvalidOperationException"/>。
/// 失败读 ProblemDetails 抛 <see cref="RemoteOsAuthException"/>（与 <see cref="RemoteOsClient"/> / ExplorerClient 同源）。</summary>
public sealed class BrowserClient : IBrowserClient
{
    private readonly HttpClient _http;
    private readonly IAuthSession _session;

    public BrowserClient(HttpClient http, IAuthSession session)
    {
        _http = http;
        _session = session;
    }

    public Task<BrowserSettingsDto> GetSettingsAsync(CancellationToken ct = default)
        => SendAsync<BrowserSettingsDto>(HttpMethod.Get, BrowserApiRoutes.Settings, ct: ct);

    public Task<BrowserSettingsDto> SaveSettingsAsync(BrowserSettingsDto settings, CancellationToken ct = default)
        => SendAsync<BrowserSettingsDto>(HttpMethod.Put, BrowserApiRoutes.Settings, body: settings, ct: ct);

    public Task<IReadOnlyList<BookmarkDto>> ListBookmarksAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<BookmarkDto>>(HttpMethod.Get, BrowserApiRoutes.Bookmarks, ct: ct);

    public Task<BookmarkDto> AddBookmarkAsync(string title, string url, CancellationToken ct = default)
        => SendAsync<BookmarkDto>(HttpMethod.Post, BrowserApiRoutes.BookmarksCreate,
            body: new CreateBookmarkRequest(title, url), ct: ct);

    public async Task DeleteBookmarkAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete,
            BrowserApiRoutes.BookmarksDelete.Replace("{id}", id.ToString("D")), ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task ClearBookmarksAsync(CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, BrowserApiRoutes.BookmarksClear, ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public Task<IReadOnlyList<HistoryEntryDto>> ListHistoryAsync(int limit = 100, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<HistoryEntryDto>>(HttpMethod.Get, BrowserApiRoutes.History,
            query: ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct: ct);

    public Task<HistoryEntryDto> RecordVisitAsync(string title, string url, CancellationToken ct = default)
        => SendAsync<HistoryEntryDto>(HttpMethod.Post, BrowserApiRoutes.HistoryCreate,
            body: new CreateHistoryEntryRequest(title, url), ct: ct);

    public async Task DeleteHistoryAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete,
            BrowserApiRoutes.HistoryDelete.Replace("{id}", id.ToString("D")), ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, BrowserApiRoutes.HistoryClear, ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    // ---- helpers（与 ExplorerClient 同模式，保持一致的错误处理与 query 拼接）----

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
            throw new InvalidOperationException(LocalizedText.Get("browser.error.not_signed_in"));
        return _session.ServerUrl;
    }

    private static Uri BuildUri(string serverUrl, string route, (string Key, string? Value)? query = null)
    {
        var baseUri = new Uri(serverUrl, UriKind.Absolute);
        var uri = new Uri(baseUri, route.TrimStart('/'));
        if (query is null || string.IsNullOrEmpty(query.Value.Value)) return uri;
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

    private static ProblemDetails NoBodyProblem()
        => new("https://remoteos.app/problems/empty-response", LocalizedText.Get("common.error.empty_response_title"), 500, LocalizedText.Get("common.error.empty_response_detail"), null);
}
