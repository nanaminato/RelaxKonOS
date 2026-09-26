package app.relaxkonos.mobile.servercenter

/**
 * 一次宿主会话：一条 SSH 传输加上本次会话的连接解析结果。
 *
 * 会话自己持有受管隧道，因此换端口重建时不会遗留旧监听，也不会让旧地址继续被使用。
 * [target] 的安装标识是隧道的稳定身份，换端口不改变它，因此不会制造新的登录记录。
 */
class ServerCenterHostSession internal constructor(
    val target: ServerHostTarget,
    private val transport: ServerCenterSshTransport,
) : AutoCloseable {

    private var tunnel: ServerCenterSshTunnel? = null
    private var closed = false

    /** 本次会话使用的传输；只允许操作层通过它执行固定动作。 */
    val sshTransport: ServerCenterSshTransport get() = transport

    /** 握手阶段观测到的宿主密钥；未连接时为 null。 */
    val observedHostKey: ServerCenterHostKeyObservation? get() = transport.observedHostKey

    /** 本次会话的解析结果；尚未打开受管隧道时为 null，此时只能做预检与文件上传。 */
    var resolution: ServerConnectionResolution? = null
        private set

    val hasTunnel: Boolean get() = tunnel != null

    /**
     * 打开或重建受管隧道。重建只更新传输地址：`serviceId` 始终是安装标识，
     * 因此换端口不会产生新的登录记录，也不会丢掉保险箱关联。
     */
    fun openOrRebindTunnel(
        remotePort: Int,
        basePath: String?,
        nowEpochMillis: Long,
    ): ServerConnectionResolution {
        check(!closed) { "The host session is closed." }
        if (!ServerHostTargetRules.hasManagedInstallation(target)) {
            throw IllegalStateException(
                "A managed loopback tunnel needs a verified installation id; probe the host before tunnelling.",
            )
        }

        val installationId = target.installationId!!
        val previous = resolution

        // 先开新的再关旧的：失败时旧隧道仍然可用，会话不会因此不可用。
        val replacement = transport.openLoopbackTunnel(remotePort, basePath)
        val incumbent = tunnel
        tunnel = replacement

        val next = if (previous == null) {
            ServerTunnelRules.managedTunnel(installationId, replacement.localPort, basePath, nowEpochMillis)
        } else {
            ServerTunnelRules.rebindTunnel(previous, replacement.localPort, nowEpochMillis)
        }
        resolution = next

        incumbent?.close()
        return next
    }

    override fun close() {
        if (closed) return
        closed = true
        tunnel?.close()
        tunnel = null
        transport.close()
    }
}

/**
 * 连接解析器：把「宿主目标 + SSH 凭据」变成一条可用会话，并把「稳定登录身份」与
 * 「本次会话的 loopback 地址」分开维护。它不替用户决定信任，只把判定所需的固定记录准备好。
 */
interface ServerCenterConnectionResolver {

    /** 直连解析：不建立 SSH，也不产生隧道。稳定身份与传输地址相同。 */
    fun resolveDirect(serverUrl: String, nowEpochMillis: Long): ServerConnectionResolution

    /**
     * 读出该宿主的固定主机密钥，生成握手期使用的同步守卫。
     * SSH 握手回调是同步的，因此候选记录必须在连接前一次读出，判定本身保持纯函数。
     */
    fun prepareHostKeyGuard(target: ServerHostTarget): (ServerCenterHostKeyObservation) -> ServerHostKeyTrust

    /**
     * 建立一条宿主会话。主机密钥未被守卫接受时抛出 [ServerCenterHostKeyRejectedException]，
     * 绝不静默接受未知密钥。
     */
    suspend fun connect(
        target: ServerHostTarget,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    ): ServerCenterHostSession

    /** 按宿主标识建立会话，并把该宿主的最近使用时间刷新为 [nowEpochMillis]。 */
    suspend fun connect(
        hostId: String,
        credential: SshCredential,
        nowEpochMillis: Long,
    ): ServerCenterHostSession
}

/**
 * 默认实现：主机密钥固定记录与宿主目标来自本机仓库，传输由工厂创建。
 * 三者都不含登录凭据，也不与 Workspace 同步。
 */
class DefaultServerCenterConnectionResolver(
    private val hostKeyTrust: ServerHostKeyTrustStore,
    private val hostTargets: ServerHostTargetStore,
    private val transportFactory: ServerCenterSshTransportFactory,
) : ServerCenterConnectionResolver {

    override fun resolveDirect(serverUrl: String, nowEpochMillis: Long): ServerConnectionResolution =
        ServerTunnelRules.direct(serverUrl, nowEpochMillis)

    override fun prepareHostKeyGuard(
        target: ServerHostTarget,
    ): (ServerCenterHostKeyObservation) -> ServerHostKeyTrust {
        val endpoint = ServerCenterSshEndpoint.create(target.sshHost, target.sshPort, target.sshUserName)
        val known = hostKeyTrust.all()

        // A first contact stays Unknown and therefore refuses the handshake; the caller must show the
        // fingerprint, have the user confirm it, pin it, and only then reconnect.
        return { observation ->
            ServerHostTrustRules.evaluate(
                known, endpoint.host, endpoint.port, observation.algorithm, observation.publicKeyBlob,
            )
        }
    }

    override suspend fun connect(
        target: ServerHostTarget,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    ): ServerCenterHostSession {
        val endpoint = ServerCenterSshEndpoint.create(target.sshHost, target.sshPort, target.sshUserName)
        val transport = transportFactory.create()
        try {
            transport.connect(endpoint, credential, hostKeyGuard)
        } catch (error: Throwable) {
            transport.close()
            throw error
        }
        return ServerCenterHostSession(target, transport)
    }

    override suspend fun connect(
        hostId: String,
        credential: SshCredential,
        nowEpochMillis: Long,
    ): ServerCenterHostSession {
        require(hostId.isNotBlank()) { "A host id is required." }
        val target = hostTargets.find(hostId)
            ?: throw IllegalStateException("Unknown host target '$hostId'.")

        val guard = prepareHostKeyGuard(target)
        val session = connect(target, credential, guard)

        // The handshake succeeded, so this host was genuinely used; persist that so the host list can
        // order by recency. A failed write must not tear down a working session.
        try {
            hostTargets.markUsed(target.hostId, nowEpochMillis)
        } catch (_: Exception) {
            // Usage recency is cosmetic; the connection itself is fine.
        }

        return session
    }
}