using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Client.Apps.TaskManager;

/// <summary>ITaskManagerClient 的 typed HttpClient 实现（与 BrowserClient / ExplorerClient 同源模式）。
/// 不 mutate HttpClient.BaseAddress，每个请求用 <see cref="IAuthSession.ServerUrl"/> 构造绝对 URI。
/// Authorization 头从 <see cref="IAuthSession.Tokens"/> 取；未登录抛 <see cref="InvalidOperationException"/>。
/// 失败读 ProblemDetails 抛 <see cref="RemoteOsAuthException"/>。</summary>
public sealed class TaskManagerClient : ITaskManagerClient
{
    private readonly HttpClient _http;
    private readonly IAuthSession _session;

    public TaskManagerClient(HttpClient http, IAuthSession session)
    {
        _http = http;
        _session = session;
    }

    public Task<PerformanceInfoDto> GetPerformanceInfoAsync(CancellationToken ct = default)
        => SendAsync<PerformanceInfoDto>(HttpMethod.Get, SystemMonitorApiRoutes.PerformanceInfo, ct: ct);

    public Task<PerformanceRealtimeSnapshotDto> GetPerformanceSnapshotAsync(CancellationToken ct = default)
        => SendAsync<PerformanceRealtimeSnapshotDto>(HttpMethod.Get, SystemMonitorApiRoutes.PerformanceSnapshot, ct: ct);

    public Task<IReadOnlyList<PerformanceRealtimeSnapshotDto>> GetPerformanceHistoryAsync(int seconds = 60, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<PerformanceRealtimeSnapshotDto>>(HttpMethod.Get,
            $"{SystemMonitorApiRoutes.PerformanceHistory}?seconds={Math.Clamp(seconds, 1, 60)}", ct: ct);

    public Task<IReadOnlyList<NetworkAddressDto>> GetNetworkAddressesAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<NetworkAddressDto>>(HttpMethod.Get, SystemMonitorApiRoutes.NetworkAddresses, ct: ct);

    public Task<ProcessPageDto> QueryProcessesAsync(int page = 1, int pageSize = 100, string? filter = null,
        string? sort = null, bool descending = true, CancellationToken ct = default)
    {
        var query = new List<string>
        {
            "page=" + Math.Max(1, page).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "pageSize=" + Math.Clamp(pageSize, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "direction=" + (descending ? "desc" : "asc"),
        };
        if (!string.IsNullOrWhiteSpace(filter)) query.Add("filter=" + Uri.EscapeDataString(filter));
        if (!string.IsNullOrWhiteSpace(sort)) query.Add("sort=" + Uri.EscapeDataString(sort));
        return SendAsync<ProcessPageDto>(HttpMethod.Get, SystemMonitorApiRoutes.ProcessQuery + "?" + string.Join("&", query), ct: ct);
    }

    public Task<KillProcessResultDto> KillProcessAsync(int processId, bool force = false, CancellationToken ct = default)
        => SendAsync<KillProcessResultDto>(HttpMethod.Delete,
            SystemMonitorApiRoutes.ProcessKill.Replace("{id}", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            query: ("force", force ? "true" : null), ct: ct);

    // ---- helpers（与 BrowserClient 同模式）----

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
            throw new InvalidOperationException(LocalizedText.Get("task_manager.error.not_signed_in"));
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
