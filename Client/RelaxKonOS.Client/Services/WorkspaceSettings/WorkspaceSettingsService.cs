using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

/// <summary><see cref="IWorkspaceSettingsService"/> 的 typed HttpClient 实现。
/// 不 mutate <c>HttpClient.BaseAddress</c>，每个请求用绝对 URI（避免共享实例并发竞态）。
/// 失败读 ProblemDetails 抛 <see cref="RelaxKonOSAuthException"/>（与 BrowserClient/ExplorerClient 同源）。</summary>
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
            req.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new RelaxKonOSAuthException(NoBodyProblem());
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        ProblemDetails? problem = null;
        try { problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, ct); }
        catch { /* 非 JSON 错误体回退通用错误 */ }
        throw problem is null
            ? new RelaxKonOSAuthException(new ProblemDetails(
                "https://relaxkonos.app/problems/http-error", $"HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode, resp.ReasonPhrase, null))
            : new RelaxKonOSAuthException(problem);
    }

    private static ProblemDetails NoBodyProblem()
        => new("https://relaxkonos.app/problems/empty-response", LocalizedText.Get("common.error.empty_response_title"), 500, LocalizedText.Get("common.error.empty_response_detail"), null);
}
