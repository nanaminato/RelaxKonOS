namespace RelaxKonOS.Server.Domain;

/// <summary>可跨重启保留的账号失败状态。IP 和账号+IP 状态短期保存在内存，以避免记录大量攻击者数据。</summary>
public sealed class AccountFailureState
{
    public string AccountKey { get; set; } = string.Empty;
    public int FailureCount { get; set; }
    public DateTimeOffset FirstFailureAt { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }
    public DateTimeOffset? BlockedUntil { get; set; }
}

/// <summary>认证安全审计事件。不得包含密码、令牌或原始请求内容。</summary>
public sealed class AuthenticationSecurityEvent
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? AccountKey { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
