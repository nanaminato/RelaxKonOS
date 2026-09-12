using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RelaxKonOS.Protocol.Capabilities;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Identity;

/// <summary>JWT 令牌签发服务。签发短期 AccessToken（REST/Hub 鉴权）与长期 RefreshToken（换新）。
/// AccessToken claims 携带 sub/name/workspace_id/device_id/role/jti，
/// RefreshToken 为随机 32 字节并登记到 AuthSessionStore。见 Protocol.md §7。</summary>
public sealed class JwtTokenService
{
    private readonly JwtOptions _opt;
    private readonly AuthSessionStore _sessions;

    public JwtTokenService(IOptions<JwtOptions> opt, AuthSessionStore sessions)
    {
        _opt = opt.Value;
        _sessions = sessions;
    }

    /// <summary>签发令牌对。role 决定 JWT 中的 role claim（首个登录设备为 Controller）。</summary>
    public AuthTokens Issue(User user, Workspace workspace, Device device, DeviceRole role,
        Guid sessionId, string authenticationMethod, DateTimeOffset authenticatedAt, long securityVersion, DateTimeOffset? absoluteExpiresAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var absoluteExp = absoluteExpiresAt ?? now.Add(_opt.RefreshTokenMaximumLifetime);
        var accessExp = Min(now.Add(_opt.AccessTokenTtl), absoluteExp);
        var refreshExp = Min(now.Add(_opt.RefreshTokenTtl), absoluteExp);

        var currentSessionId = sessionId;
        var claims = new[]
        {
            new Claim("sid", sessionId.ToString("D")),
            new Claim("amr", authenticationMethod),
            new Claim("auth_time", authenticatedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim("security_version", securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, user.Username),
            new Claim("workspace_id", workspace.Id.ToString()),
            new Claim("device_id", device.Id.ToString()),
            new Claim("role", role.ToString().ToLowerInvariant()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: accessExp.UtcDateTime,
            signingCredentials: creds);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        // RefreshToken：随机 32 字节，登记到当前内存会话的吊销簿。
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions.Register(currentSessionId, refreshToken, user.Id, workspace.Id, device.Id, refreshExp, absoluteExp, authenticationMethod, authenticatedAt, securityVersion);

        return new AuthTokens(accessToken, refreshToken, accessExp, refreshExp);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    /// <summary>Issues a token restricted to the server file API for one external application.</summary>
    public FileCapabilityTokenDto IssueFileCapability(
        Guid userId,
        Guid workspaceId,
        Guid deviceId,
        string appId,
        IReadOnlyCollection<string> scopes, long securityVersion)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_opt.FileCapabilityTokenTtl);
        var claims = new List<Claim>
        {
            new("security_version", securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("workspace_id", workspaceId.ToString()),
            new("device_id", deviceId.ToString()),
            new("app_id", appId),
            new(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        claims.AddRange(scopes.Distinct(StringComparer.Ordinal).Select(scope => new Claim(RelaxKonOSAuthSchemes.ScopeClaim, scope)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new FileCapabilityTokenDto(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
