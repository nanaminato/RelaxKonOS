using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>部署契约的协议版本。请求、事件与发布清单都携带它，客户端据此拒绝不兼容的启动器。</summary>
public static class ServerDeploymentProtocol
{
    public const int Version = 1;
}

/// <summary>
/// 操作事件：远端启动器写出的机器可读进度记录。字段与 Goal 文档第 5 节一致。
/// 原始 stdout/stderr 只作为受限诊断附件，默认不展示、不上传，且不得包含密码、token、私钥或完整环境变量。
/// </summary>
/// <param name="Progress">当前阶段内已验证的进度。为 null 表示没有可靠分母，UI 不得伪造百分比。</param>
/// <param name="SafeMessage">可安全展示给用户的短文案；不得包含凭据、路径以外的宿主细节或原始命令输出。</param>
public sealed record ServerDeploymentEventDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("installationId")] string? InstallationId,
    [property: JsonPropertyName("kind")] ServerDeploymentKind Kind,
    [property: JsonPropertyName("phase")] ServerDeploymentPhase Phase,
    [property: JsonPropertyName("state")] ServerDeploymentState State,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("problemCode")] string? ProblemCode,
    [property: JsonPropertyName("safeMessage")] string? SafeMessage);

/// <summary>
/// 一次部署操作的权威记录。它比实时事件更完整：客户端断线后按 <c>operationId</c> 读取该记录，
/// 而不是依据本地缓存的最后一帧事件推断成功。
/// </summary>
public sealed record ServerDeploymentOperationDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("installationId")] string? InstallationId,
    [property: JsonPropertyName("kind")] ServerDeploymentKind Kind,
    [property: JsonPropertyName("phase")] ServerDeploymentPhase Phase,
    [property: JsonPropertyName("state")] ServerDeploymentState State,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("problemCode")] string? ProblemCode,
    [property: JsonPropertyName("safeMessage")] string? SafeMessage,
    [property: JsonPropertyName("cancellable")] bool Cancellable,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset? StartedAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("result")] ServerDeploymentResultDto? Result = null,
    [property: JsonPropertyName("snapshot")] ServerHostSnapshotDto? Snapshot = null,
    [property: JsonPropertyName("probe")] ServerHostProbeDto? Probe = null);

/// <summary>
/// 操作终态结果。它描述宿主实际状态，而不是脚本退出码：成功必须同时具备安装标识、版本与服务健康证据。
/// </summary>
/// <param name="Healthy">经 SSH 侧访问目标 loopback 健康端点得到的实际结果；安装/升级成功必须为 true。</param>
/// <param name="DataRetained">卸载操作中数据是否被保留。非卸载操作为 null。</param>
/// <param name="DataCompatible">回滚/修复后数据是否与新程序版本兼容；false 表示需要人工处理数据。</param>
public sealed record ServerDeploymentResultDto(
    [property: JsonPropertyName("installationId")] string? InstallationId,
    [property: JsonPropertyName("mode")] ServerInstallMode? Mode,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("previousVersion")] string? PreviousVersion,
    [property: JsonPropertyName("installRoot")] string? InstallRoot,
    [property: JsonPropertyName("dataRoot")] string? DataRoot,
    [property: JsonPropertyName("listenUrl")] string? ListenUrl,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("dataRetained")] bool? DataRetained = null,
    [property: JsonPropertyName("dataCompatible")] bool? DataCompatible = null,
    [property: JsonPropertyName("serviceNames")] IReadOnlyList<string>? ServiceNames = null,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc = null);

/// <summary>
/// 只读宿主快照。它是「API 可达 / SSH 可达 / 服务已安装」三个独立事实中的「服务已安装」部分，
/// 每次探测都必须标注来源与时间，离线缓存不得冒充实时健康。
/// </summary>
/// <param name="VerifiedAtUtc">该快照的核验时间。客户端展示“通过 SSH 于 14:32 验证”时必须使用它。</param>
public sealed record ServerHostSnapshotDto(
    [property: JsonPropertyName("installationId")] string? InstallationId,
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("mode")] ServerInstallMode? Mode,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("previousVersion")] string? PreviousVersion,
    [property: JsonPropertyName("installRoot")] string? InstallRoot,
    [property: JsonPropertyName("dataRoot")] string? DataRoot,
    [property: JsonPropertyName("listenUrl")] string? ListenUrl,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("dataRetained")] bool? DataRetained,
    [property: JsonPropertyName("serviceNames")] IReadOnlyList<string>? ServiceNames,
    [property: JsonPropertyName("verifiedAtUtc")] DateTimeOffset VerifiedAtUtc);

/// <summary>
/// 三种安装模式的能力矩阵。客户端用它决定可选操作与前置条件，而不是靠猜测宿主行为。
/// 绝对路径由探测结果给出；这里的默认根只是用于确认页展示的规范化符号根。
/// </summary>
/// <param name="RequiresElevation">是否需要提升权限（Linux root/sudo，或 Windows 已提升管理员令牌）。</param>
/// <param name="SupportsSudoElevation">是否支持受控 sudo 提权（仅 Linux System Mode）。</param>
/// <param name="RequiresElevatedSshToken">Windows 是否要求 SSH 会话本身已持有服务管理权限。</param>
/// <param name="DefaultLoopbackOnly">默认是否只监听 loopback。</param>
/// <param name="HealthPath">SSH 侧访问 loopback 时使用的健康路径。</param>
public sealed record ServerDeploymentModeCapabilities(
    [property: JsonPropertyName("mode")] ServerInstallMode Mode,
    [property: JsonPropertyName("hostPlatform")] HostPlatformKind HostPlatform,
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation,
    [property: JsonPropertyName("supportsSudoElevation")] bool SupportsSudoElevation,
    [property: JsonPropertyName("requiresElevatedSshToken")] bool RequiresElevatedSshToken,
    [property: JsonPropertyName("supportsSystemService")] bool SupportsSystemService,
    [property: JsonPropertyName("supportsRollback")] bool SupportsRollback,
    [property: JsonPropertyName("supportsDataRetention")] bool SupportsDataRetention,
    [property: JsonPropertyName("defaultLoopbackOnly")] bool DefaultLoopbackOnly,
    [property: JsonPropertyName("defaultInstallRootToken")] string DefaultInstallRootToken,
    [property: JsonPropertyName("defaultDataRootToken")] string DefaultDataRootToken,
    [property: JsonPropertyName("healthPath")] string HealthPath,
    [property: JsonPropertyName("serviceNames")] IReadOnlyList<string> ServiceNames);

/// <summary>三种模式的能力矩阵权威定义。UI 与启动器共用它，避免各端各写一套判断。</summary>
public static class ServerDeploymentModeMatrix
{
    public static ServerDeploymentModeCapabilities LinuxSystem { get; } = new(
        ServerInstallMode.LinuxSystem, HostPlatformKind.Linux,
        RequiresElevation: true, SupportsSudoElevation: true, RequiresElevatedSshToken: false,
        SupportsSystemService: true, SupportsRollback: true, SupportsDataRetention: true,
        DefaultLoopbackOnly: true, DefaultInstallRootToken: "/opt/relaxkonos", DefaultDataRootToken: "/var/lib/relaxkonos",
        HealthPath: "/healthz", ServiceNames: ["relaxkonos-server.service", "relaxkonos-guardian.service"]);

    public static ServerDeploymentModeCapabilities LinuxUser { get; } = new(
        ServerInstallMode.LinuxUser, HostPlatformKind.Linux,
        RequiresElevation: false, SupportsSudoElevation: false, RequiresElevatedSshToken: false,
        SupportsSystemService: false, SupportsRollback: true, SupportsDataRetention: true,
        DefaultLoopbackOnly: true, DefaultInstallRootToken: "xdg-data/relaxkonos", DefaultDataRootToken: "xdg-data/relaxkonos",
        HealthPath: "/ready", ServiceNames: []);

    public static ServerDeploymentModeCapabilities WindowsSystem { get; } = new(
        ServerInstallMode.WindowsSystem, HostPlatformKind.Windows,
        RequiresElevation: true, SupportsSudoElevation: false, RequiresElevatedSshToken: true,
        SupportsSystemService: true, SupportsRollback: true, SupportsDataRetention: true,
        DefaultLoopbackOnly: true, DefaultInstallRootToken: @"%ProgramFiles%\RelaxKonOS", DefaultDataRootToken: @"%ProgramData%\RelaxKonOS",
        HealthPath: "/healthz", ServiceNames: ["RelaxKonOSServer", "RelaxKonOSGuardian", "RelaxKonOSPrivilegedHelper"]);

    public static IReadOnlyList<ServerDeploymentModeCapabilities> All { get; } = [LinuxSystem, LinuxUser, WindowsSystem];

    public static ServerDeploymentModeCapabilities For(ServerInstallMode mode) => mode switch
    {
        ServerInstallMode.LinuxSystem => LinuxSystem,
        ServerInstallMode.LinuxUser => LinuxUser,
        ServerInstallMode.WindowsSystem => WindowsSystem,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}