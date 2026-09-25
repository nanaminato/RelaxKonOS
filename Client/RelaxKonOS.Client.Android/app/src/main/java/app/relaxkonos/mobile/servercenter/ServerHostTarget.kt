package app.relaxkonos.mobile.servercenter

import java.security.MessageDigest

/**
 * 最近一次经 SSH 核验的部署状态。它是**带核验时间的缓存**，不是实时健康：
 * 界面必须展示 [verifiedAtEpochMillis]，且离线缓存不得冒充当前状态。
 *
 * @param installed 宿主上是否存在 RelaxKonOS 受管安装。
 * @param installationId 本次核验读到的安装标识；仅当 [installed] 为 true 时才可能非空。
 * @param healthy 核验时经 loopback 健康端点得到的实际结果。
 */
data class ServerHostVerifiedState(
    val installed: Boolean,
    val mode: ServerInstallMode?,
    val installationId: String?,
    val version: String?,
    val listenUrl: String?,
    val healthy: Boolean,
    val verifiedAtEpochMillis: Long,
)

/**
 * 宿主目标：用户显式添加的一台受管宿主。它是**宿主生命周期资料**，
 * 既不是 RelaxKonOS 登录记录（[app.relaxkonos.mobile.security.model.SavedLogin]），也不是 SSH 凭据。
 *
 * 一个宿主目标可以关联多个登录，也可以在首次安装前没有任何登录。删除宿主目标只影响本机管理资料；
 * 卸载服务端是需要远端确认的独立动作。SSH 凭据、主机指纹与操作回执只保存在本设备，不同步到 Workspace。
 *
 * @param hostId 本机稳定标识，由规范化端点派生；同一 `(host, port)` 只有一个目标。
 * @param displayName 用户起的宿主名。登录表单展示它，而不是临时 loopback 端口。
 * @param sshHost SSH 主机名或 IP，按规范化形式保存（小写、去 IPv6 方括号）。
 * @param sshPort SSH 端口，总是显式记录。
 * @param sshUserName 用于管理该宿主的 SSH 用户；SSH 凭据按它与端点绑定。
 * @param installationId 受管安装标识：安装成功后写入，作为受管隧道的稳定 serviceId；安装前为 null。
 * @param lastVerified 最近一次经 SSH 核验的部署状态；从未核验时为 null。
 */
data class ServerHostTarget(
    val hostId: String,
    val displayName: String,
    val sshHost: String,
    val sshPort: Int,
    val sshUserName: String,
    val installationId: String?,
    val lastVerified: ServerHostVerifiedState?,
    val createdAtEpochMillis: Long,
    val lastUsedAtEpochMillis: Long,
)

/**
 * 宿主目标在详情页的状态。API 可达、SSH 可达、服务已安装是三个独立事实，
 * 因此这里只描述宿主侧结论，不把「API 不可达」推断成「需要重新安装」。
 */
enum class ServerHostTargetStatus {
    /** 已添加但尚未完成任何 SSH 核验。 */
    Unverified,

    /** 首次见到该端点或该算法的主机密钥，必须由用户核对指纹。 */
    HostKeyUnknown,

    /** 主机密钥与固定记录不一致，所有写操作被阻断。 */
    HostKeyChanged,

    /** SSH 可达，但宿主上尚无 RelaxKonOS 受管安装。 */
    ReachableNotInstalled,

    /** 已安装且经 loopback 健康检查通过。 */
    InstalledHealthy,

    /** 已安装但服务异常，可修复或恢复上次版本。 */
    ServiceUnhealthy,
}

/**
 * 宿主目标的共用规则。桌面与 Android 必须给出同一标识与同一判断，
 * 否则同一台宿主会在两端被当成两条记录。
 */
object ServerHostTargetRules {

    /** 本机宿主标识前缀，便于与安装标识、登录标识区分。 */
    const val HOST_ID_PREFIX = "rkhost-"

    /** 用户起名的长度上限；超出即视为无效输入。 */
    const val MAX_DISPLAY_NAME_LENGTH = 64

    private const val HEX_DIGITS = "0123456789abcdef"

    /** 宿主目标的去重键：`host:port`。不含 SSH 用户，因为安装与主机密钥都属于宿主。 */
    fun endpointIdentity(host: String, port: Int): String = ServerHostTrustRules.endpointKey(host, port)

    /**
     * 由规范化端点派生的稳定本机标识。它不含秘密，也不随端口以外的任何输入变化，
     * 因此重复添加同一宿主不会产生第二条记录。
     */
    fun hostId(host: String, port: Int): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(endpointIdentity(host, port).toByteArray(Charsets.UTF_8))
        // 逐字节取无符号值转十六进制：与 C# `Convert.ToHexString(...).ToLowerInvariant()` 一致。
        val hex = buildString(16) {
            for (index in 0 until 8) {
                val value = digest[index].toInt() and 0xFF
                append(HEX_DIGITS[value ushr 4])
                append(HEX_DIGITS[value and 0x0F])
            }
        }
        return HOST_ID_PREFIX + hex
    }

    /** 是否为本规则生成的宿主标识。 */
    fun isHostId(value: String?): Boolean {
        if (value.isNullOrBlank() || !value.startsWith(HOST_ID_PREFIX)) return false
        val hex = value.substring(HOST_ID_PREFIX.length)
        return hex.length == 16 && hex.all { it in '0'..'9' || it in 'a'..'f' }
    }

    /** 规范化展示名：去空白并限长；为空时回落到端点键，绝不编造主机名。 */
    fun normalizeDisplayName(displayName: String?, host: String, port: Int): String {
        val trimmed = displayName?.trim()
        if (trimmed.isNullOrEmpty()) return endpointIdentity(host, port)
        return if (trimmed.length <= MAX_DISPLAY_NAME_LENGTH) trimmed else trimmed.take(MAX_DISPLAY_NAME_LENGTH)
    }

    /**
     * 校验 SSH 端点输入。端口必须是显式有效的 1–65535，用户名不得为空：
     * 没有用户名的「宿主目标」无法建立受管连接，必须在添加时就拒绝。
     */
    fun isValidEndpoint(host: String?, port: Int, userName: String?): Boolean =
        !host.isNullOrBlank() && port in 1..65535 && !userName.isNullOrBlank()

    /**
     * 新建宿主目标。SSH 用户与展示名在此规范化，安装标识与核验状态留空——
     * 它们只能由安装或探测结果写入，不能由用户表单直接声称。
     */
    fun create(
        host: String,
        port: Int,
        userName: String,
        displayName: String?,
        nowEpochMillis: Long,
    ): ServerHostTarget {
        require(isValidEndpoint(host, port, userName)) {
            "An SSH host, port and user name are required."
        }
        val normalizedHost = ServerHostTrustRules.normalizeHost(host)
        return ServerHostTarget(
            hostId = hostId(normalizedHost, port),
            displayName = normalizeDisplayName(displayName, normalizedHost, port),
            sshHost = normalizedHost,
            sshPort = port,
            sshUserName = userName.trim(),
            installationId = null,
            lastVerified = null,
            createdAtEpochMillis = nowEpochMillis,
            lastUsedAtEpochMillis = nowEpochMillis,
        )
    }

    /**
     * 用一次核验结果更新宿主目标。安装标识只在核验确实给出一个**合法**标识时改写：
     * 探测失败或宿主上尚无安装（null）不得清掉已知的受管标识，否则会丢失隧道身份。
     */
    fun applyVerifiedState(
        target: ServerHostTarget,
        verified: ServerHostVerifiedState,
        nowEpochMillis: Long,
    ): ServerHostTarget {
        val installationId = if (verified.installed && ServerInstallationId.isValid(verified.installationId)) {
            verified.installationId!!.trim()
        } else {
            target.installationId
        }
        return target.copy(
            installationId = installationId,
            lastVerified = verified,
            lastUsedAtEpochMillis = nowEpochMillis,
        )
    }

    /** 受管隧道所需的安装标识是否已就绪。 */
    fun hasManagedInstallation(target: ServerHostTarget): Boolean =
        ServerInstallationId.isValid(target.installationId)

    /**
     * 该宿主是否可与某个登录关联。只有受管安装标识一致才成立：
     * 相同 IP、URL 文本或 DNS 解析都不足以合并，否则会把两台不同安装当成同一台。
     */
    fun matchesInstallation(target: ServerHostTarget, serviceId: String?): Boolean {
        val managed = target.installationId?.trim() ?: return false
        return ServerInstallationId.isValid(managed) && managed == serviceId
    }

    /** 宿主详情页的状态标签，供两端复用同一套判断。 */
    fun status(target: ServerHostTarget, trust: ServerHostKeyTrust): ServerHostTargetStatus {
        if (trust == ServerHostKeyTrust.Changed) return ServerHostTargetStatus.HostKeyChanged
        if (trust == ServerHostKeyTrust.Unknown) return ServerHostTargetStatus.HostKeyUnknown
        val verified = target.lastVerified ?: return ServerHostTargetStatus.Unverified
        if (!verified.installed) return ServerHostTargetStatus.ReachableNotInstalled
        return if (verified.healthy) ServerHostTargetStatus.InstalledHealthy else ServerHostTargetStatus.ServiceUnhealthy
    }
}