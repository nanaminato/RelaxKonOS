namespace RelaxKonOS.Protocol.Hubs;

/// <summary>性能 Hub 的 server→client 事件名。</summary>
public static class PerformanceHubEvents
{
    public const string OnPerformanceSnapshot = nameof(IPerformanceHubClient.OnPerformanceSnapshot);
}
