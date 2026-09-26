package app.relaxkonos.mobile.servercenter

import java.util.Locale

/**
 * 部署契约的协议版本。与 C# 侧 `ServerDeploymentProtocol.Version` 必须一致；
 * 客户端据此拒绝不兼容的远端启动器。
 */
object ServerDeploymentProtocol {
    const val VERSION = 1
}

/** 远端部署启动器接受的有限动作集合。客户端不能提交任意命令、脚本路径、服务名或删除路径。 */
enum class ServerDeploymentKind { Probe, Install, Upgrade, Repair, Uninstall, Status, Rollback }

/** 部署操作的持久状态。远端持久记录是权威来源，实时事件只用于展示。 */
enum class ServerDeploymentState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

/** 单调递增的操作阶段。同一操作内阶段序号只增不减。 */
enum class ServerDeploymentPhase {
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
    Interrupted,
}

/** RelaxKonOS Server 的受管安装模式。 */
enum class ServerInstallMode { LinuxSystem, LinuxUser, WindowsSystem }

/** 服务端网络监听选项。默认仅 loopback。 */
enum class ServerNetworkProfile { Loopback, Lan, ReverseProxy }

/** 安装/升级包来源。离线包与指定 URL 都必须是已签名制品。 */
enum class ServerPackageSourceKind { OfficialStable, LocalBundle, DirectUrl }

/** 已确认的主机密钥状态。变化时必须阻断所有写操作。 */
enum class ServerHostKeyTrust { Unknown, Trusted, Changed }

/** 卸载时的数据处置。默认 [Retain]。 */
enum class ServerDataRetention { Retain, Delete }

/** 受管安装根的分类。卸载删除范围只能落在安装清单记载的根之内。 */
enum class ServerDataScope { Program, Configuration, Database, Secrets, Logs, Cache, State }

/** 一次登录连接的稳定身份种类。传输地址永远不是身份。 */
enum class ServerServiceIdKind { DirectUrl, ManagedInstallation }

/** 发布包种类。 */
enum class ServerReleasePackageKind { Server, UserServer }

/** 发布制品运行时标识（RID）。 */
enum class ServerRuntimeIdentifier { WinX64, WinArm64, LinuxX64, LinuxArm64 }

/**
 * 枚举与线协议字符串互转：操作及安装模式使用 camelCase；RID 与发布包类型
 * 使用发布格式的连字符名称，例如 `win-x64` 与 `user-server`。
 */
internal fun Enum<*>.wireName(): String = when (this) {
    ServerReleasePackageKind.UserServer -> "user-server"
    ServerRuntimeIdentifier.WinX64 -> "win-x64"
    ServerRuntimeIdentifier.WinArm64 -> "win-arm64"
    ServerRuntimeIdentifier.LinuxX64 -> "linux-x64"
    ServerRuntimeIdentifier.LinuxArm64 -> "linux-arm64"
    else -> name.replaceFirstChar { it.lowercase(Locale.ROOT) }
}

internal inline fun <reified T : Enum<T>> enumFromWire(value: String?): T? {
    if (value == null) return null
    return enumValues<T>().firstOrNull { it.wireName() == value }
}

/** 操作事件：远端启动器写出的机器可读进度记录。 */
data class ServerDeploymentEvent(
    val schemaVersion: Int,
    val operationId: String,
    val installationId: String?,
    val kind: ServerDeploymentKind,
    val phase: ServerDeploymentPhase,
    val state: ServerDeploymentState,
    val sequence: Long,
    val timestampUtc: String,
    val progress: Int? = null,
    val problemCode: String? = null,
    val safeMessage: String? = null,
)

/** 一次部署操作的权威记录。断线后按 operationId 读取它，而不是依据本地缓存推断成功。 */
data class ServerDeploymentOperation(
    val schemaVersion: Int,
    val operationId: String,
    val installationId: String?,
    val kind: ServerDeploymentKind,
    val phase: ServerDeploymentPhase,
    val state: ServerDeploymentState,
    val sequence: Long,
    val timestampUtc: String,
    val cancellable: Boolean,
    val progress: Int? = null,
    val problemCode: String? = null,
    val safeMessage: String? = null,
    val startedAtUtc: String? = null,
    val completedAtUtc: String? = null,
    val result: ServerDeploymentResult? = null,
    val snapshot: ServerHostSnapshot? = null,
    val probe: ServerHostProbe? = null,
)

/** 操作终态结果。成功必须同时具备安装标识、版本与服务健康证据。 */
data class ServerDeploymentResult(
    val installationId: String?,
    val mode: ServerInstallMode?,
    val version: String?,
    val previousVersion: String? = null,
    val installRoot: String? = null,
    val dataRoot: String? = null,
    val listenUrl: String? = null,
    val healthy: Boolean = false,
    val dataRetained: Boolean? = null,
    val dataCompatible: Boolean? = null,
    val serviceNames: List<String> = emptyList(),
    val completedAtUtc: String? = null,
)

/**
 * 只读宿主快照。「API 可达 / SSH 可达 / 服务已安装」是三个独立事实；
 * [verifiedAtUtc] 是核验时间，离线缓存不得冒充实时健康。
 */
data class ServerHostSnapshot(
    val installed: Boolean,
    val verifiedAtUtc: String,
    val installationId: String? = null,
    val mode: ServerInstallMode? = null,
    val version: String? = null,
    val previousVersion: String? = null,
    val installRoot: String? = null,
    val dataRoot: String? = null,
    val listenUrl: String? = null,
    val healthy: Boolean = false,
    val dataRetained: Boolean? = null,
    val serviceNames: List<String> = emptyList(),
)

/** 与 C# `ServerHostProbeDto` 对齐的 SSH 宿主预检结果。 */
data class ServerHostProbe(
    val hostPlatform: String,
    val architecture: String,
    val runtimeIdentifier: ServerRuntimeIdentifier?,
    val osId: String?,
    val osVersion: String?,
    val osSupported: Boolean,
    val elevated: Boolean,
    val sudoAvailable: Boolean,
    val systemdAvailable: Boolean,
    val diskAvailableBytes: Long?,
    val requestedPort: Int?,
    val requestedPortAvailable: Boolean?,
    val existingInstallationId: String?,
    val existingMode: ServerInstallMode?,
    val existingVersion: String?,
    val existingInstalled: Boolean,
    val missingDependencies: List<String>,
    val verifiedAtUtc: String,
)

/** 三种安装模式的能力矩阵。绝对路径由探测结果给出。 */
data class ServerDeploymentModeCapabilities(
    val mode: ServerInstallMode,
    val hostPlatform: String,
    val requiresElevation: Boolean,
    val supportsSudoElevation: Boolean,
    val requiresElevatedSshToken: Boolean,
    val supportsSystemService: Boolean,
    val supportsRollback: Boolean,
    val supportsDataRetention: Boolean,
    val defaultLoopbackOnly: Boolean,
    val defaultInstallRootToken: String,
    val defaultDataRootToken: String,
    val healthPath: String,
    val serviceNames: List<String>,
)

/** 动作参数。所有字段都是固定枚举或受限字符串，绝不携带宿主路径。 */
data class ServerDeploymentOptions(
    val source: ServerPackageSourceKind,
    val network: ServerNetworkProfile,
    val retention: ServerDataRetention = ServerDataRetention.Retain,
    val mode: ServerInstallMode? = null,
    val version: String? = null,
    val packageUri: String? = null,
    val stagedPackageName: String? = null,
    val packageDigest: String? = null,
    val expectedInstallationId: String? = null,
    val serverPort: Int? = null,
    val confirmed: Boolean = false,
)

/** 远端部署启动器的唯一入口请求。[operationId] 同时作为幂等键。 */
data class ServerDeploymentRequest(
    val schemaVersion: Int,
    val operationId: String,
    val kind: ServerDeploymentKind,
    val options: ServerDeploymentOptions? = null,
)

/** 发布包内单个文件的摘要条目。 */
data class ServerReleaseFile(val path: String, val length: Long, val sha256: String)

/** 包内 manifest.json。 */
data class ServerReleaseManifest(
    val schemaVersion: Int,
    val packageKind: ServerReleasePackageKind,
    val version: String,
    val runtime: ServerRuntimeIdentifier,
    val supportedSystems: List<String>,
    val payload: Map<String, Map<String, String>>,
    val files: List<ServerReleaseFile>,
    val createdAtUtc: String? = null,
)

/** 发布签名。签名覆盖制品**原始文件字节**的 SHA-256，签名单独放在同名 `*.sig` 文件中。 */
data class ServerReleaseSignature(
    val schemaVersion: Int,
    val keyId: String,
    val algorithm: String,
    val signedSha256: String,
    val signature: String,
)

/** 官方稳定版目录项。签名是伴随文件，因此这里只描述包本身。 */
data class ServerReleaseDescriptor(
    val schemaVersion: Int,
    val packageKind: ServerReleasePackageKind,
    val version: String,
    val runtime: ServerRuntimeIdentifier,
    val url: String,
    val sha256: String,
)

/** 发布信任策略。未内置任何密钥时不得信任任何来源。 */
data class ServerReleaseTrustPolicy(
    val trustedKeyIds: List<String> = emptyList(),
    val allowDevelopmentSource: Boolean = false,
) {
    fun isTrustedKey(keyId: String): Boolean = trustedKeyIds.contains(keyId)

    val acceptsUntrustedSource: Boolean get() = allowDevelopmentSource

    companion object {
        /** 严格策略：不信任任何 keyId，也不允许开发来源。 */
        val Strict = ServerReleaseTrustPolicy()
    }
}

/** 发布校验的机器可读结果。 */
data class ServerReleaseVerification(
    val verified: Boolean,
    val verifiedAtUtc: String,
    val packageKind: ServerReleasePackageKind? = null,
    val version: String? = null,
    val runtime: ServerRuntimeIdentifier? = null,
    val keyId: String? = null,
    val problemCode: String? = null,
)
