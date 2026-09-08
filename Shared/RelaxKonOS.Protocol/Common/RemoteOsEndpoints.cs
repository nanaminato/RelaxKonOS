namespace RelaxKonOS.Protocol.Common;

/// <summary>RelaxKonOS 通信端点常量（REST API 前缀与 SignalR Hub 路径）。Server 注册路由与 Client 拼接 URL 共用，避免字符串拼写错位。</summary>
public static class RemoteOsEndpoints
{
    /// <summary>REST API 版本前缀，所有 HTTP 端点以此为根（例如 <c>/api/v1.0/auth/login</c>）。</summary>
    public const string ApiVersionPrefix = "api/v1.0";

    /// <summary>Workspace SignalR Hub 路径。</summary>
    public const string WorkspaceHubPath = "/hubs/workspace";

    /// <summary>Guardian 日志 SignalR Hub 路径。</summary>
    public const string GuardianLogsHubPath = "/hubs/guardian-logs";

    /// <summary>系统性能实时推送 SignalR Hub 路径。</summary>
    public const string PerformanceHubPath = "/hubs/performance";
}
