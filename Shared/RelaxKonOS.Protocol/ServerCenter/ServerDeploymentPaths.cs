namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 远端部署暂存目录的固定布局。客户端与远端启动器必须用同一套名字，客户端才能在不向启动器
/// 传递任何路径的前提下把包和启动器放到位。启动器只读写自己所在目录下的固定条目，绝不接受
/// 调用方提供的脚本路径、删除路径或服务名。
/// </summary>
public static class ServerDeploymentPaths
{
    /// <summary>Linux 启动器脚本名。</summary>
    public const string LinuxLauncherScriptName = "relaxkonos-deploy.sh";

    /// <summary>Windows 启动器脚本名。</summary>
    public const string WindowsLauncherScriptName = "RelaxKonOS-Deploy.ps1";

    /// <summary>启动器请求文件；客户端以紧凑单行 JSON 写入，权限仅限当前账号。</summary>
    public const string RequestFileName = "request.json";

    /// <summary>解包后的发布目录（或待校验的 ZIP 同名目录）所在子目录名。</summary>
    public const string PackageDirectoryName = "package";

    /// <summary>操作日志根目录；与 <c>package</c> 同级，卸载删除数据时不会连带删除。</summary>
    public const string JournalDirectoryName = "journal";

    /// <summary>持久操作记录子目录。</summary>
    public const string OperationsDirectoryName = "operations";

    /// <summary>单主机写操作锁的文件名，位于 <see cref="JournalDirectoryName"/> 内。</summary>
    public const string LockFileName = "deploy.lock";

    /// <summary>操作记录文件名：客户端断线后按 <c>operationId</c> 读取它，而不是依赖最后一帧事件。</summary>
    public static string OperationRecordFileName(Guid operationId) => $"{operationId:D}.json";

    /// <summary>操作事件流文件名：按 <c>sequence</c> 补取，允许中间缺口。</summary>
    public static string OperationEventsFileName(Guid operationId) => $"{operationId:D}.jsonl";

    /// <summary>幂等摘要文件名：记录该 <c>operationId</c> 首次提交时的请求摘要，用于识别冲突复用。</summary>
    public static string OperationDigestFileName(Guid operationId) => $"{operationId:D}.digest";

    /// <summary>受限诊断附件文件名：引擎原始 stdout/stderr，默认不展示、不上传。</summary>
    public static string OperationDiagnosticsFileName(Guid operationId) => $"{operationId:D}.log";

    /// <summary>包内 User Mode 部署引擎的相对路径。</summary>
    public static string LinuxUserEngineRelativePath => $"deployment/user/relaxkon";

    /// <summary>包内 Linux System Mode 安装引擎的相对路径。</summary>
    public static string LinuxSystemEngineRelativePath => "deployment/bootstrap/install-relaxkonos.sh";

    /// <summary>包内 Linux System Mode 卸载引擎的相对路径。</summary>
    public static string LinuxSystemUninstallEngineRelativePath => "deployment/bootstrap/uninstall-relaxkonos.sh";

    /// <summary>包内 Windows System Mode 安装引擎的相对路径。</summary>
    public static string WindowsSystemEngineRelativePath => @"deployment/bootstrap/Install-RelaxKonOS.ps1";

    /// <summary>包内 Windows System Mode 卸载引擎的相对路径。</summary>
    public static string WindowsSystemUninstallEngineRelativePath => @"deployment/bootstrap/Uninstall-RelaxKonOS.ps1";
}