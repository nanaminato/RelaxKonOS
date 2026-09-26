package app.relaxkonos.mobile.servercenter

import java.net.URI
import java.util.Locale

/**
 * 一次登录连接的稳定身份与当前传输地址。
 *
 * [serviceId] 是稳定身份：直连时是规范化的持久服务器 URL，受管 SSH 隧道时是安装标识。
 * [effectiveBaseUrl] 是本次会话的实际 HTTP 地址：隧道时可能是动态 loopback 端口，
 * 永远不能作为凭据键，也不得作为长期保存的服务器身份。
 */
data class ServerConnectionIdentity(
    val kind: ServerServiceIdKind,
    val serviceId: String,
    val effectiveBaseUrl: String,
)

/**
 * 连接身份与传输地址的分离规则。登录记录与连接保险箱以 `(serviceId, identifier)` 为键；
 * 隧道恢复或换端口后只更新 `effectiveBaseUrl`，绝不创建新的登录记录。
 */
object ServerConnectionIdentityRules {

    /**
     * 规范化服务器 URL：小写 scheme 与 host、去除默认端口、去除末尾斜杠、丢弃查询与片段。
     * 同一物理服务器的等价写法必须得到同一 `serviceId`。
     */
    fun normalizeServerUrl(serverUrl: String): String {
        require(serverUrl.isNotBlank()) { "Server URL is required." }
        val uri = try {
            URI(serverUrl.trim())
        } catch (e: Exception) {
            throw IllegalArgumentException("Server URL must be absolute.", e)
        }
        require(uri.isAbsolute && uri.host != null) { "Server URL must be absolute." }
        val scheme = uri.scheme.lowercase(Locale.ROOT)
        val host = uri.host.lowercase(Locale.ROOT)
        val port = uri.port
        val isDefaultPort = port == -1 || (scheme == "http" && port == 80) || (scheme == "https" && port == 443)
        val authority = if (isDefaultPort) host else "$host:$port"
        val path = uri.path.orEmpty().trimEnd('/')
        return "$scheme://$authority$path"
    }

    /** 直连登录身份：规范化 URL 即稳定 serviceId。 */
    fun direct(serverUrl: String): ServerConnectionIdentity {
        val normalized = normalizeServerUrl(serverUrl)
        return ServerConnectionIdentity(ServerServiceIdKind.DirectUrl, normalized, normalized)
    }

    /**
     * 受管隧道登录身份：安装标识即稳定 serviceId，传输地址是当前 loopback 地址。
     * 换端口只改变 effectiveBaseUrl。
     */
    fun managedTunnel(installationId: String, effectiveBaseUrl: String): ServerConnectionIdentity {
        require(ServerInstallationId.isValid(installationId)) { "Installation id is invalid." }
        return ServerConnectionIdentity(
            kind = ServerServiceIdKind.ManagedInstallation,
            serviceId = installationId.trim(),
            effectiveBaseUrl = normalizeServerUrl(effectiveBaseUrl),
        )
    }

    /** 登录记录与保险箱的键。换隧道端口不改变它。 */
    fun credentialKey(serviceId: String, identifier: String): String = "$serviceId\u001f$identifier"

    /**
     * 隧道换端口后身份是否保持不变。客户端在重建隧道后必须满足该条件，
     * 否则会制造新的登录记录、并丢失原保险箱关联。
     */
    fun preservesIdentity(before: ServerConnectionIdentity, after: ServerConnectionIdentity): Boolean =
        before.kind == after.kind && before.serviceId == after.serviceId
}