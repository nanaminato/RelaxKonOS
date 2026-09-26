namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 卸载时的数据处置策略。默认卸载程序并保留数据；删除数据是显式、需二次确认、不可恢复的动作，
/// 且只能由同一受控部署引擎执行，删除范围仅限安装清单记载的根。
/// </summary>
public static class ServerDataRetentionPolicy
{
    /// <summary>卸载默认保留数据。直接复用 User Mode 现有 uninstall 会删除 XDG 数据，违背该默认语义。</summary>
    public const ServerDataRetention DefaultUninstall = ServerDataRetention.Retain;

    /// <summary>保留数据时被留下的范围。重新安装必须能识别并复用它们。</summary>
    public static IReadOnlyList<ServerDataScope> RetainedScopes { get; } =
        [ServerDataScope.Configuration, ServerDataScope.Database, ServerDataScope.Secrets, ServerDataScope.Logs, ServerDataScope.State];

    /// <summary>删除数据时被移除的范围，也就是确认页必须逐项列出的不可恢复内容。</summary>
    public static IReadOnlyList<ServerDataScope> IrrecoverableScopes { get; } =
        [ServerDataScope.Database, ServerDataScope.Secrets, ServerDataScope.Configuration, ServerDataScope.Logs, ServerDataScope.State];

    /// <summary>某次卸载实际会删除的范围。<see cref="ServerDataRetention.Retain"/> 时只剩程序文件。</summary>
    public static IReadOnlyList<ServerDataScope> DeletedScopes(ServerDataRetention retention) =>
        retention == ServerDataRetention.Delete ? IrrecoverableScopes : [];

    /// <summary>删除数据必须输入服务器名称二次确认；保留数据不需要。</summary>
    public static bool RequiresServerNameConfirmation(ServerDataRetention retention) =>
        retention == ServerDataRetention.Delete;

    /// <summary>提供导出备份入口的时机：任何可能删除数据的卸载都要先给出备份机会。</summary>
    public static bool OffersExportBeforeDelete(ServerDataRetention retention) =>
        retention == ServerDataRetention.Delete;
}