namespace RelaxKonOS.Client.Services.Auth;

/// <summary>客户端认证会话状态机：Unauthenticated → Connecting → Authenticated（登录失败回 Unauthenticated）。</summary>
public enum AuthSessionState
{
    Unauthenticated,
    Connecting,
    Authenticated,
}
