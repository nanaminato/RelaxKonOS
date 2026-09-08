namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Guardian 日志 Hub 的 server→client 事件。</summary>
public static class GuardianLogsHubEvents
{
    public const string OnLogSnapshot = nameof(IGuardianLogsHubClient.OnLogSnapshot);
}
