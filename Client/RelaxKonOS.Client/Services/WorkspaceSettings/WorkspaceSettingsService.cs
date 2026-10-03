using RelaxKonOS.Client.Services.Diagnostics;
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
        var serviceId = _session.ServiceId;
        var sessionId = _session.CurrentSession?.Id;
        var isCurrentTarget = _session.State == AuthSessionState.Authenticated
            && _session.EffectiveBaseUrl == serverUrl && _session.CurrentWorkspace?.Id == workspaceId;
        var saved = await SendAsync<WorkspacePreferencesDto>(HttpMethod.Put, serverUrl, accessToken, workspaceId, preferences, ct);
        // Access tokens can rotate while the authenticated handler sends the request. Acknowledge
        // by stable login identity, while rejecting responses from an earlier login/workspace.
        if (isCurrentTarget && serviceId is not null && _session.State == AuthSessionState.Authenticated
            && _session.ServiceId == serviceId && _session.CurrentSession?.Id == sessionId
            && _session.CurrentWorkspace?.Id == workspaceId)
            _settings.AcknowledgePreferences(preferences.Revision, saved.Revision);
        return saved;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string serverUrl, string accessToken, Guid workspaceId, object? body, CancellationToken ct)
    {
        var route = WorkspaceApiRoutes.Preferences.Replace("{id}", workspaceId.ToString("D"));
        var requestId = Guid.NewGuid();
        LanguageSwitchDiagnostics.Record("http.begin", new { requestId, method = method.Method, workspaceId, language = (body as WorkspacePreferencesDto)?.Language, revision = (body as WorkspacePreferencesDto)?.Revision });
        using var req = new HttpRequestMessage(method, new Uri(new Uri(serverUrl), route.TrimStart('/')))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
        };
        if (body is not null)
            req.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        Guid? correlationId = null;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("X-RelaxKonOS-Correlation-Id", out var values)
                && Guid.TryParse(values.FirstOrDefault(), out var id)) correlationId = id;
            LanguageSwitchDiagnostics.Record("http.completed", new { requestId, status = (int)resp.StatusCode, correlationId });
            if (!resp.IsSuccessStatusCode) await EnsureSuccessAsync(resp, ct);
            return await resp.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct)
                ?? throw new RelaxKonOSAuthException(NoBodyProblem());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            WorkspacePreferencesDiagnostics.Record(method == HttpMethod.Put ? "save" : "load",
                workspaceId, (body as WorkspacePreferencesDto)?.Revision, exception, correlationId);
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        ProblemDetails? problem = null;
        try { problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (System.Text.Json.JsonException) { /* 非 JSON 错误体回退通用错误 */ }
        catch (NotSupportedException) { /* 非 JSON 错误体回退通用错误 */ }
        throw problem is null || string.IsNullOrWhiteSpace(problem.Title)
            ? new RelaxKonOSAuthException(new ProblemDetails(
                "https://relaxkonos.app/problems/http-error", $"HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode, resp.ReasonPhrase, null))
            : new RelaxKonOSAuthException(problem with { Status = (int)resp.StatusCode });
    }

    private static ProblemDetails NoBodyProblem()
        => new("https://relaxkonos.app/problems/empty-response", LocalizedText.Get("common.error.empty_response_title"), 500, LocalizedText.Get("common.error.empty_response_detail"), null);
}
