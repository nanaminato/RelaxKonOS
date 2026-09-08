using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.WorkspaceSettings;

/// <summary><see cref="IWorkspaceSettingsService"/> 的 typed HttpClient 实现。
/// 不 mutate <c>HttpClient.BaseAddress</c>，每个请求用绝对 URI（避免共享实例并发竞态）。
/// 失败读 ProblemDetails 抛 <see cref="RemoteOsAuthException"/>（与 BrowserClient/ExplorerClient 同源）。</summary>
public sealed class WorkspaceSettingsService : IWorkspaceSettingsService
{
    private readonly HttpClient _http;
    private readonly IAuthSession _session;
    private readonly ShellSettings _settings;

    public WorkspaceSettingsService(HttpClient http, IAuthSession session, ShellSettings settings)
    {
        _http = http;
        _session = session;
        _settings = settings;
    }

    public Task<WorkspacePreferencesDto> GetAsync(string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default)
        => SendAsync<WorkspacePreferencesDto>(HttpMethod.Get, serverUrl, accessToken, workspaceId, null, ct);

    public async Task<WorkspacePreferencesDto> SaveAsync(string serverUrl, string accessToken, Guid workspaceId, WorkspacePreferencesDto preferences, CancellationToken ct = default)
    {
        var saved = await SendAsync<WorkspacePreferencesDto>(HttpMethod.Put, serverUrl, accessToken, workspaceId, preferences, ct);
        if (_session.State == AuthSessionState.Authenticated && _session.ServerUrl == serverUrl
            && _session.CurrentWorkspace?.Id == workspaceId && _session.Tokens?.AccessToken == accessToken)
            _settings.AcknowledgePreferences(preferences.Revision, saved.Revision);
        return saved;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string serverUrl, string accessToken, Guid workspaceId, object? body, CancellationToken ct)
    {
        var route = WorkspaceApiRoutes.Preferences.Replace("{id}", workspaceId.ToString("D"));
        using var req = new HttpRequestMessage(method, new Uri(new Uri(serverUrl), route.TrimStart('/')))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
        };
        if (body is not null)
            req.Content = JsonContent.Create(body, options: RemoteOsJsonOptions.Default);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, ct)
            ?? throw new RemoteOsAuthException(NoBodyProblem());
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
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
