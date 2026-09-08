using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>客户端认证会话。持有当前登录上下文（Tokens/User/Workspace/Session/Device/Role）；
/// 勾选“加密保存密码并自动登录”时，凭据会保存到当前平台的系统安全存储。供 LoginViewModel 与桌面 Shell 共享。</summary>
public interface IAuthSession
{
    AuthSessionState State { get; }
    string? ServerUrl { get; }
    AuthTokens? Tokens { get; }
    UserDto? CurrentUser { get; }
    ServerDescriptorDto? CurrentServer { get; }
    WorkspaceDto? CurrentWorkspace { get; }
    SessionDto? CurrentSession { get; }
    DeviceDto? CurrentDevice { get; }
    DeviceRole AssignedRole { get; }

    /// <summary>状态变化（Connecting / Authenticated / Unauthenticated）。</summary>
    event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    /// <summary>登录。成功后可分别记住服务器/用户名，或额外加密保存密码。</summary>
    Task<LoginResponse> LoginAsync(
        string serverUrl,
        LoginRequest request,
        bool rememberServer,
        bool rememberPassword,
        CancellationToken ct = default);

    /// <summary>Returns all connections remembered for the current operating-system user.</summary>
    Task<IReadOnlyList<SavedLoginProfile>> GetSavedProfilesAsync(CancellationToken ct = default);

    /// <summary>登出（吊销 RefreshToken，清空上下文）。</summary>
    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>用 RefreshToken 换新令牌对。失败返回 false 并重置会话。</summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a usable access token, refreshing it once when it is near expiry or when the supplied
    /// token has just been rejected by the server. Refresh tokens remain memory-only.
    /// </summary>
    Task<string?> GetAccessTokenAsync(TimeSpan renewBefore, string? rejectedAccessToken = null,
        CancellationToken ct = default);
}
