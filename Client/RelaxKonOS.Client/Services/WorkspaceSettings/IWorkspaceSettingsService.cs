using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

/// <summary>Workspace 用户偏好 HTTP 客户端抽象。typed HttpClient 实现（见 <see cref="WorkspaceSettingsService"/>）。
/// 与 <c>BrowserClient</c>/<c>ExplorerClient</c> 同模式：从 <c>IAuthSession</c> 取 <c>serverUrl</c> + <c>accessToken</c>
/// 构造绝对 URI 与 Authorization 头，不 mutate <c>HttpClient.BaseAddress</c>。错误统一为 <see cref="RelaxKonOS.Client.Services.Auth.RemoteOsAuthException"/>。</summary>
public interface IWorkspaceSettingsService
{
    /// <summary>读取当前 Workspace 的用户偏好（GET /workspaces/{id}/preferences）。</summary>
    Task<WorkspacePreferencesDto> GetAsync(string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default);

    /// <summary>保存用户偏好（PUT /workspaces/{id}/preferences）。返回服务端归一化后的 DTO。</summary>
    Task<WorkspacePreferencesDto> SaveAsync(string serverUrl, string accessToken, Guid workspaceId, WorkspacePreferencesDto preferences, CancellationToken ct = default);
}
