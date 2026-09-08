namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Terminal Hub 的 server→client 事件名常量。对应 ITerminalHubClient 接口方法名。Client 端 HubConnection.On&lt;T&gt; 用。</summary>
public static class TerminalHubEvents
{
    /// <summary>PTY 原始输出字节流。</summary>
    public const string OnOutput = nameof(ITerminalHubClient.OnOutput);

    /// <summary>PTY 子进程退出。</summary>
    public const string OnProcessExited = nameof(ITerminalHubClient.OnProcessExited);
}
