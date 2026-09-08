namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Terminal Hub 的 client→server invoke 方法名常量。Client 端 HubConnection.InvokeAsync 用。</summary>
public static class TerminalHubMethods
{
    /// <summary>
    /// 附加到远端 PTY 会话：sessionId 命中且属于当前用户则恢复（先发缓冲快照），否则新建 PTY 并 spawn shell。
    /// 返回 <c>AttachTerminalResponse</c>（实际会话 ID + 是否新建）。
    /// </summary>
    public const string Start = nameof(Start);

    /// <summary>向 PTY 写入用户输入字节（client→server）。</summary>
    public const string Input = nameof(Input);

    /// <summary>调整 PTY 尺寸（列/行/像素）。</summary>
    public const string Resize = nameof(Resize);

    /// <summary>关闭并释放 PTY（手动终止：杀掉该会话并从服务端移除）。</summary>
    public const string Close = nameof(Close);

    /// <summary>拉取当前用户的全部终端会话摘要（多实例列表）。</summary>
    public const string ListSessions = nameof(ListSessions);
}
