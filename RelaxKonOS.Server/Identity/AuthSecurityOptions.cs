namespace RelaxKonOS.Server.Identity;

/// <summary>登录入口的防暴力破解配置。密码仍由宿主 OS 验证；这些限制保护 RelaxKonOS HTTP 入口。</summary>
public sealed class AuthSecurityOptions
{
    public int EndpointPermitLimit { get; set; } = 10;
    public int EndpointWindowSeconds { get; set; } = 60;
    public int IpFailureLimit { get; set; } = 30;
    public int IpFailureWindowMinutes { get; set; } = 5;
    public int IpBlockMinutes { get; set; } = 5;
    public int AccountFailureRetentionHours { get; set; } = 24;

    /// <summary>仅当直连地址属于这些代理时才处理 X-Forwarded-For。空列表表示一律不信任转发头。</summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>受信反向代理所在的 CIDR 网络，例如 10.0.0.0/24。</summary>
    public List<string> TrustedNetworks { get; set; } = [];
}
