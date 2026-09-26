using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 远端部署启动器接受的有限动作集合。客户端永远不能提交任意命令、脚本路径、服务名或删除路径；
/// 每个动作由启动器映射到既有部署引擎的固定入口。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerDeploymentKind>))]
public enum ServerDeploymentKind
{
    /// <summary>只读探测：OS、架构、磁盘、依赖、已有安装标识、服务管理器、可用端口与实际权限。</summary>
    Probe,

    /// <summary>首次安装。目标主机不得已有 RelaxKonOS 受管安装。</summary>
    Install,

    /// <summary>升级到指定目标版本。要求 <c>ExpectedInstallationId</c> 与宿主一致。</summary>
    Upgrade,

    /// <summary>重放已验证的当前版本与配置，不任意修改宿主资源。</summary>
    Repair,

    /// <summary>卸载。默认保留数据，只有显式选择才删除受管数据根。</summary>
    Uninstall,

    /// <summary>只读状态：安装标识、模式、版本、服务与健康。</summary>
    Status,

    /// <summary>把程序版本切回上一个已验证版本。数据兼容性由 <see cref="ServerDeploymentResultDto"/> 报告。</summary>
    Rollback
}

/// <summary>部署操作的持久状态。远端持久记录是权威来源，实时事件只用于展示。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerDeploymentState>))]
public enum ServerDeploymentState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

/// <summary>
/// 单调递增的操作阶段。序号（<see cref="ServerDeploymentEventDto.Sequence"/>）在同一操作内严格递增，
/// 客户端重连后按 <c>operationId + sequence</c> 补取，不依赖实时事件是否连续。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerDeploymentPhase>))]
public enum ServerDeploymentPhase
{
    Queued,
    ValidatingRequest,
    AcquiringLock,
    Preflight,
    Staging,
    Transferring,
    VerifyingPackage,
    Snapshotting,
    Stopping,
    Activating,
    Starting,
    HealthChecking,
    RollingBack,
    Finalizing,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

/// <summary>RelaxKonOS Server 的受管安装模式。三种模式的能力差异见 <see cref="ServerDeploymentModeMatrix"/>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerInstallMode>))]
public enum ServerInstallMode { LinuxSystem, LinuxUser, WindowsSystem }

/// <summary>服务端网络监听选项。默认仅 loopback；跨设备直连必须显式选择可信 TLS 入口。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerNetworkProfile>))]
public enum ServerNetworkProfile { Loopback, Lan, ReverseProxy }

/// <summary>安装/升级包的来源。离线包与指定 URL 都必须是已签名的发布制品。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerPackageSourceKind>))]
public enum ServerPackageSourceKind { OfficialStable, LocalBundle, DirectUrl }

/// <summary>已确认的主机密钥状态。变化时必须阻断所有写操作。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerHostKeyTrust>))]
public enum ServerHostKeyTrust { Unknown, Trusted, Changed }

/// <summary>卸载时的数据处置。默认 <see cref="Retain"/>，删除数据需要二次确认。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerDataRetention>))]
public enum ServerDataRetention { Retain, Delete }

/// <summary>受管安装根的分类。卸载删除范围只能落在安装清单记载的根之内。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerDataScope>))]
public enum ServerDataScope { Program, Configuration, Database, Secrets, Logs, Cache, State }

/// <summary>
/// 一次登录连接的稳定身份种类。直连沿用规范化的服务器 URL；受管 SSH 隧道使用安装标识。
/// 传输地址（<c>effectiveBaseUrl</c>）永远不是身份，也不得长期保存。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerServiceIdKind>))]
public enum ServerServiceIdKind { DirectUrl, ManagedInstallation }

/// <summary>发布包的种类。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerReleasePackageKind>))]
public enum ServerReleasePackageKind
{
    Server,
    [JsonStringEnumMemberName("user-server")] UserServer
}

/// <summary>发布制品的运行时标识（RID）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerRuntimeIdentifier>))]
public enum ServerRuntimeIdentifier
{
    [JsonStringEnumMemberName("win-x64")] WinX64,
    [JsonStringEnumMemberName("win-arm64")] WinArm64,
    [JsonStringEnumMemberName("linux-x64")] LinuxX64,
    [JsonStringEnumMemberName("linux-arm64")] LinuxArm64
}

/// <summary>宿主操作系统，复用 <see cref="HostPlatformKind"/> 以保持协议一致。</summary>
public static class ServerDeploymentHostPlatform
{
    public const HostPlatformKind Windows = HostPlatformKind.Windows;
    public const HostPlatformKind Linux = HostPlatformKind.Linux;
}
