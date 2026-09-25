package app.relaxkonos.mobile.servercenter

import java.net.URI

/** 本次会话的传输方式。它只描述「怎么到达」，不参与登录身份。 */
enum class ServerConnectionTransportKind { Direct, SshTunnel }

/**
 * 连接解析器对外的当前结论：稳定身份 [identity] 加上本次会话实际使用的地址。
 *
 * 登录记录、保险箱与 API 会话都以 [ServerConnectionIdentity.serviceId] 为键；
 * [ServerConnectionIdentity.effectiveBaseUrl] 只用于本会话发请求。
 *
 * @param localPort 受管隧道绑定的 loopback 端口；直连时为 null。
 * @param resolvedAtEpochMillis 该解析结果的生成时间。隧道重建或换端口后必须刷新。
 */
data class ServerConnectionResolution(
    val identity: ServerConnectionIdentity,
    val transport: ServerConnectionTransportKind,
    val localPort: Int?,
    val resolvedAtEpochMillis: Long,
)

/**
 * loopback 隧道与连接解析的共用规则。客户端内置的 SSH 转发只能绑定 loopback：把服务暴露到
 * 非 loopback 地址会把「已登录用户才有权访问的服务」变成局域网可达，必须由用户显式选择
 * 受信任 TLS 入口，而不是隧道默认行为。
 */
object ServerTunnelRules {

    /** 隧道唯一允许绑定的地址。不要用 `localhost`，它可能解析到非回环地址。 */
    const val LOOPBACK_HOST = "127.0.0.1"

    /** IPv6 回环地址，仅用于识别，不作为默认绑定目标。 */
    const val LOOPBACK_HOST_V6 = "::1"

    /** 端口 0 表示由操作系统分配空闲端口；这正是隧道应使用的方式，避免固定端口冲突。 */
    const val EPHEMERAL_PORT = 0

    /** 是否回环地址（IPv4 `127.0.0.0/8`、IPv6 `::1`，以及 IPv4 映射形式）。 */
    fun isLoopbackHost(host: String?): Boolean {
        if (host.isNullOrBlank()) return false
        val candidate = host.trim().removePrefix("[").removeSuffix("]")
        if (candidate.equals(LOOPBACK_HOST_V6, ignoreCase = true)) return true
        // `::ffff:127.0.0.1` 与裸 IPv4 都取最后一段点分表示来判定，不做 DNS 解析。
        val dotted = candidate.substringAfterLast(':')
        val parts = dotted.split('.')
        if (parts.size != 4) return false
        val octets = parts.map { it.toIntOrNull() ?: return false }
        if (octets.any { it !in 0..255 }) return false
        return octets[0] == 127
    }

    /**
     * 隧道监听地址是否可接受。任何非回环地址都必须被拒绝，这是硬约束，不提供配置开关。
     */
    fun isAcceptableTunnelBindAddress(host: String?): Boolean = isLoopbackHost(host)

    /**
     * 由隧道本地端口与基础路径构造本次会话的 HTTP 地址。隧道终止的是明文回环连接，
     * 因此只使用 `http`；TLS 由远端监听配置决定，不由本地隧道伪造。
     */
    fun buildLoopbackBaseUrl(port: Int, basePath: String? = null): String {
        require(port in 1..65535) { "Tunnel port must be between 1 and 65535." }
        val path = if (basePath.isNullOrBlank()) "" else "/" + basePath.trim().trim('/')
        return "http://$LOOPBACK_HOST:$port$path"
    }

    /** 解析回环地址的端口；不是回环地址或没有显式端口时返回 null。 */
    fun tryGetLoopbackPort(baseUrl: String?): Int? {
        if (baseUrl.isNullOrBlank()) return null
        val uri = try {
            URI(baseUrl.trim())
        } catch (_: Exception) {
            return null
        }
        if (!uri.isAbsolute || !isLoopbackHost(uri.host)) return null
        // java.net.URI 对未写端口的地址返回 -1，因此这里不需要额外区分默认端口。
        return uri.port.takeIf { it in 1..65535 }
    }

    /**
     * 重建隧道后的解析结果。必须保持稳定身份不变——否则会制造新的登录记录并丢掉保险箱关联。
     * 端口变化只更新传输地址。
     */
    fun rebindTunnel(
        previous: ServerConnectionResolution,
        newLocalPort: Int,
        resolvedAtEpochMillis: Long,
    ): ServerConnectionResolution {
        require(previous.transport == ServerConnectionTransportKind.SshTunnel) {
            "Only a tunnel resolution can be rebound to a new local port."
        }
        // 通过身份规则重建，保证传输地址与首次解析时一样被规范化；serviceId 原样保留。
        val identity = ServerConnectionIdentityRules.managedTunnel(
            previous.identity.serviceId,
            buildLoopbackBaseUrl(newLocalPort, basePathOf(previous.identity.effectiveBaseUrl)),
        )
        require(ServerConnectionIdentityRules.preservesIdentity(previous.identity, identity)) {
            "Rebinding a tunnel must not change the login identity."
        }
        return previous.copy(
            identity = identity,
            localPort = newLocalPort,
            resolvedAtEpochMillis = resolvedAtEpochMillis,
        )
    }

    /** 直连解析结果：稳定身份与传输地址相同，不使用隧道。 */
    fun direct(serverUrl: String, resolvedAtEpochMillis: Long): ServerConnectionResolution =
        ServerConnectionResolution(
            identity = ServerConnectionIdentityRules.direct(serverUrl),
            transport = ServerConnectionTransportKind.Direct,
            localPort = null,
            resolvedAtEpochMillis = resolvedAtEpochMillis,
        )

    /** 受管隧道解析结果：安装标识是稳定身份，loopback 地址只是本次传输地址。 */
    fun managedTunnel(
        installationId: String,
        localPort: Int,
        basePath: String?,
        resolvedAtEpochMillis: Long,
    ): ServerConnectionResolution = ServerConnectionResolution(
        identity = ServerConnectionIdentityRules.managedTunnel(
            installationId,
            buildLoopbackBaseUrl(localPort, basePath),
        ),
        transport = ServerConnectionTransportKind.SshTunnel,
        localPort = localPort,
        resolvedAtEpochMillis = resolvedAtEpochMillis,
    )

    private fun basePathOf(baseUrl: String): String? = try {
        URI(baseUrl).path
    } catch (_: Exception) {
        null
    }
}