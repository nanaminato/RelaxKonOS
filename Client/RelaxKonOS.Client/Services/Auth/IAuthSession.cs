using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>客户端认证会话。持有当前登录上下文（Tokens/User/Workspace/Session/Device/Role）；
/// 勾选“加密保存密码并自动登录”时，凭据会保存到当前平台的系统安全存储。供 LoginViewModel 与桌面 Shell 共享。</summary>
public interface IAuthSession
{
    AuthSessionState State { get; }

    /// <summary>
    /// 稳定登录身份：直连为规范化的服务器 URL，受管 SSH 隧道为安装标识。
    /// 登录记录、凭据保险箱与客户端偏好都以它为键，换隧道端口不改变它。
    /// </summary>
    string? ServiceId { get; }

    /// <summary>
    /// 本次会话实际使用的 HTTP 地址。受管隧道换端口只更新它，绝不改变 <see cref="ServiceId"/>；
    /// 它不是身份，也不得作为长期保存的服务器地址或凭据键。
    /// </summary>
    string? EffectiveBaseUrl { get; }

    AuthTokens? Tokens { get; }
    UserDto? CurrentUser { get; }
    ServerDescriptorDto? CurrentServer { get; }
    WorkspaceDto? CurrentWorkspace { get; }
    SessionDto? CurrentSession { get; }
    DeviceDto? CurrentDevice { get; }
    DeviceRole AssignedRole { get; }

    /// <summary>
    /// 本登录身份能否在本 Server 上执行普通文件/终端/Git 操作（登录响应声明）。为 null 表示当次
    /// 登录没有拿到该声明。入口应在被打开前先读它，而不是等第一次操作以 503 失败后再解释原因。
    /// </summary>
    ServerExecutionEligibilityDto? ExecutionEligibility { get; }

    /// <summary>状态变化（Connecting / Authenticated / Unauthenticated）。</summary>
    event EventHandler<AuthSessionStateChangedEventArgs>? StateChanged;

    /// <summary>登录。请求发往 <see cref="ServerConnectionIdentity.EffectiveBaseUrl"/>，凭据以稳定身份保存。</summary>
    Task<LoginResponse> LoginAsync(
        ServerConnectionIdentity identity,
        LoginRequest request,
        bool rememberServer,
        bool rememberPassword,
        CancellationToken ct = default);

    /// <summary>
    /// 隧道重建或换端口后只更新传输地址。身份必须保持不变，否则会制造新的登录记录并丢掉保险箱关联。
    /// </summary>
    void UpdateConnection(ServerConnectionIdentity identity);

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