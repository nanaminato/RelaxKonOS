namespace RelaxKonOS.Server.Identity;

/// <summary>JWT 配置。绑定 appsettings.json 的 "Jwt" 节。Secret 至少 32 字符（HMACSHA256 要求 ≥256 位）。</summary>
public sealed class JwtOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "RelaxKonOS.Server";
    public string Audience { get; set; } = "RelaxKonOS.Client";
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Maximum idle interval between refreshes.</summary>
    public TimeSpan RefreshTokenTtl { get; set; } = TimeSpan.FromDays(7);
    /// <summary>Hard upper bound for one in-memory sign-in session, even when it remains active.</summary>
    public TimeSpan RefreshTokenMaximumLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan FileCapabilityTokenTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MediaLeaseTtl { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MediaLeaseMaximumLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>appsettings.json 里的默认占位密钥，生产环境必须替换。</summary>
    public const string DefaultInsecureSecret = "REPLACE_IN_PRODUCTION_at_least_32_chars_long_random__";
}
