using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 只读宿主预检快照。它是「API 可达 / SSH 可达 / 服务已安装」三个独立事实中与宿主有关的全部内容，
/// 由远端启动器在 SSH 侧读取，客户端据此决定可选操作与前置条件，而不是靠 IP、URL 或 DNS 猜测。
/// </summary>
/// <param name="Elevated">当前 SSH 会话是否已经具备 root（Linux）或已提升管理员（Windows）权限。</param>
/// <param name="SudoAvailable">Linux System Mode 是否具备可用的 sudo/visudo，用于受控提权。</param>
/// <param name="DiskAvailableBytes">安装根所在文件系统的可用字节数；无法确定时为 null，UI 不得伪造。</param>
/// <param name="RequestedPort">本次请求希望使用的服务端口；未指定时为 null。</param>
/// <param name="RequestedPortAvailable">请求端口当前是否可用；未指定端口时为 null。</param>
/// <param name="MissingDependencies">缺少的宿主依赖名，供客户端展示可执行的宿主侧前置步骤。</param>
public sealed record ServerHostProbeDto(
    [property: JsonPropertyName("hostPlatform")] HostPlatformKind HostPlatform,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("runtimeIdentifier")] ServerRuntimeIdentifier? RuntimeIdentifier,
    [property: JsonPropertyName("osId")] string? OsId,
    [property: JsonPropertyName("osVersion")] string? OsVersion,
    [property: JsonPropertyName("osSupported")] bool OsSupported,
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("sudoAvailable")] bool SudoAvailable,
    [property: JsonPropertyName("systemdAvailable")] bool SystemdAvailable,
    [property: JsonPropertyName("diskAvailableBytes")] long? DiskAvailableBytes,
    [property: JsonPropertyName("requestedPort")] int? RequestedPort,
    [property: JsonPropertyName("requestedPortAvailable")] bool? RequestedPortAvailable,
    [property: JsonPropertyName("existingInstallationId")] string? ExistingInstallationId,
    [property: JsonPropertyName("existingMode")] ServerInstallMode? ExistingMode,
    [property: JsonPropertyName("existingVersion")] string? ExistingVersion,
    [property: JsonPropertyName("existingInstalled")] bool ExistingInstalled,
    [property: JsonPropertyName("missingDependencies")] IReadOnlyList<string> MissingDependencies,
    [property: JsonPropertyName("verifiedAtUtc")] DateTimeOffset VerifiedAtUtc);

/// <summary>
/// 宿主平台的受支持矩阵。启动器与客户端共用同一判断，避免各端各写一套「这台机器能不能装」的逻辑。
/// </summary>
public static class ServerHostPlatformSupport
{
    /// <summary>Linux System Mode 仅接受这些发行版，其他系统必须由用户显式确认后继续。</summary>
    public static IReadOnlyList<string> SupportedLinuxSystems { get; } =
        ["debian-12", "ubuntu-22.04", "ubuntu-24.04", "ubuntu-26.04"];

    public static bool IsSupportedLinuxSystem(string? osId, string? osVersion) =>
        osId is not null && osVersion is not null
        && SupportedLinuxSystems.Contains($"{osId}-{osVersion}", StringComparer.Ordinal);

    /// <summary>把 <c>uname -m</c> 或 Windows 的 <c>PROCESSOR_ARCHITECTURE</c> 归一化为 RID。</summary>
    public static ServerRuntimeIdentifier? ResolveRuntimeIdentifier(HostPlatformKind platform, string? machine) => machine switch
    {
        "x86_64" or "amd64" or "AMD64" => platform == HostPlatformKind.Windows
            ? ServerRuntimeIdentifier.WinX64 : ServerRuntimeIdentifier.LinuxX64,
        "aarch64" or "arm64" or "ARM64" => platform == HostPlatformKind.Windows
            ? ServerRuntimeIdentifier.WinArm64 : ServerRuntimeIdentifier.LinuxArm64,
        _ => null
    };
}