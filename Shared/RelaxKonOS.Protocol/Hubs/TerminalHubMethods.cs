namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Terminal Hub 的 client→server invoke 方法名常量。Client 端 HubConnection.InvokeAsync 用。</summary>
public static class TerminalHubMethods
{
    /// <summary>
    /// 附加到远端 PTY 会话：sessionId 命中且属于当前用户则恢复（先发缓冲快照），否则新建 PTY 并 spawn shell。
    /// 返回 <c>AttachTerminalResponse</c>（实际会话 ID + 是否新建）。
    /// </summary>
    /// <remarks>
    /// 客户端必须<b>显式传两个</b>参数：<c>Start(request, sessionId)</c>；不恢复既有会话时第二个参数传 <c>null</c>。
    /// SignalR 按参数个数匹配 Hub 方法、<b>不</b>应用 C# 默认值，少传一个参数会让整次调用失败，
    /// 而调用方只会看到笼统的 invoke 错误（Android 的“终端无法连接”就是这样产生的）。
    /// </remarks>
    public const string Start = nameof(Start);

    /// <summary>只附加现有且归属当前用户的会话；不存在时失败，绝不创建新 PTY。</summary>
    public const string AttachExisting = nameof(AttachExisting);

    /// <summary>向 PTY 写入用户输入字节（client→server）。</summary>
    public const string Input = nameof(Input);

    /// <summary>调整 PTY 尺寸（列/行/像素）。</summary>
    public const string Resize = nameof(Resize);

    /// <summary>
    /// 关闭并释放指定 PTY（手动终止：杀掉该会话并从服务端移除）。
    /// </summary>
    /// <remarks>
    /// 会话 ID 由客户端给出，因此服务端必须校验归属：只有会话的主人能关闭它。
    /// 关闭当前已附加的会话后，该连接不再指向任何会话，后续 <c>Input</c>/<c>Resize</c> 成为空操作。
    /// </remarks>
    public const string CloseSession = nameof(CloseSession);

    /// <summary>拉取当前用户的全部终端会话摘要（多实例列表）。</summary>
    public const string ListSessions = nameof(ListSessions);
}
