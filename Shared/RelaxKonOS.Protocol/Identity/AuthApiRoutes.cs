using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>认证相关 REST 端点路由常量。路径已含 /api/v1.0 前缀。Server 注册路由与 Client 拼接 URL 共用。</summary>
public static class AuthApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;

    /// <summary>登录（POST，无需认证）。</summary>
    public const string Login = $"/{V1}/auth/login";

    /// <summary>刷新令牌（POST，无需认证）。</summary>
    public const string Refresh = $"/{V1}/auth/refresh";

    /// <summary>登出（POST，需 JWT）。</summary>
    public const string Logout = $"/{V1}/auth/logout";

    /// <summary>当前用户信息（GET，需 JWT）。</summary>
    public const string LoginAlias = $"/{V1}/auth/me/login-alias";
    public const string AliasPassword = LoginAlias + "/password";
    public const string DeleteAlias = LoginAlias + "/delete";
    public const string SystemLogin = $"/{V1}/auth/me/system-login";
    public const string Me = $"/{V1}/auth/me";
}
