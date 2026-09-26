package app.relaxkonos.mobile.servercenter

import app.relaxkonos.mobile.security.encodeUtf8
import com.jcraft.jsch.ChannelExec
import com.jcraft.jsch.ChannelSftp
import com.jcraft.jsch.HostKey
import com.jcraft.jsch.HostKeyRepository
import com.jcraft.jsch.JSch
import com.jcraft.jsch.JSchException
import com.jcraft.jsch.Session
import com.jcraft.jsch.SftpProgressMonitor
import com.jcraft.jsch.UserInfo
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * 默认工厂：内置 JSch 传输，不依赖系统 `ssh/scp/sftp` 命令，也不要求用户另装 SSH App。
 */
class JschServerCenterSshTransportFactory : ServerCenterSshTransportFactory {
    override fun create(): ServerCenterSshTransport = JschServerCenterTransport()
}

/**
 * 基于内置 JSch 的传输实现。
 *
 * 主机密钥在握手的 [HostKeyRepository.check] 回调中判定：只有守卫返回
 * [ServerHostKeyTrust.Trusted] 才返回 `OK`；[ServerHostKeyTrust.Unknown] 返回 `NOT_INCLUDED`，
 * [ServerHostKeyTrust.Changed] 返回 `CHANGED`，配合 `StrictHostKeyChecking=yes` 让 JSch 中止握手。
 * 因此不存在等价于 `StrictHostKeyChecking=no` 的静默接受路径。
 */
class JschServerCenterTransport : ServerCenterSshTransport {

    private val gate = Mutex()
    private val tunnels = mutableListOf<JschLoopbackTunnel>()

    private var session: Session? = null
    private var endpoint: ServerCenterSshEndpoint? = null
    private var repository: GuardedHostKeyRepository? = null
    private var closed = false

    override val isConnected: Boolean get() = session?.isConnected == true

    override val observedHostKey: ServerCenterHostKeyObservation? get() = repository?.observation

    override suspend fun connect(
        endpoint: ServerCenterSshEndpoint,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    ) = withContext(Dispatchers.IO) {
        gate.withLock {
            check(!closed) { "The SSH transport is closed." }
            if (isConnected && this@JschServerCenterTransport.endpoint == endpoint) return@withLock
            disconnectLocked()

            val jsch = JSch()
            when (credential.kind) {
                SshCredentialKind.PrivateKey -> {
                    val keyBytes = encodeUtf8(credential.secret)
                    val passphraseBytes = credential.passphrase?.let { encodeUtf8(it) }
                    try {
                        // In-memory identity: the private key never touches the filesystem.
                        jsch.addIdentity(IDENTITY_NAME, keyBytes, null, passphraseBytes)
                    } finally {
                        keyBytes.fill(0)
                        passphraseBytes?.fill(0)
                    }
                }

                SshCredentialKind.Password -> Unit
            }

            val created = jsch.getSession(endpoint.userName, endpoint.host, endpoint.port)
            if (credential.kind == SshCredentialKind.Password) {
                val passwordBytes = encodeUtf8(credential.secret)
                try {
                    created.setPassword(passwordBytes)
                } finally {
                    passwordBytes.fill(0)
                }
                created.setUserInfo(PasswordUserInfo(credential.secret))
            }
            created.setConfig("StrictHostKeyChecking", "yes")
            // 现代 OpenSSH 默认禁用 ssh-rsa，服务器多用键盘交互或公钥；两者都要尝试。
            created.setConfig("PreferredAuthentications", "publickey,password,keyboard-interactive")

            val guard = GuardedHostKeyRepository(endpoint, hostKeyGuard)
            created.setHostKeyRepository(guard)

            try {
                created.connect(CONNECT_TIMEOUT_MILLIS)
            } catch (error: JSchException) {
                val observation = guard.observation
                created.disconnect()
                // JSch reports a rejected host key as a connection failure; translate it back into the
                // trust decision so the caller can drive the fingerprint confirmation flow.
                if (observation != null && guard.trust != ServerHostKeyTrust.Trusted) {
                    throw ServerCenterHostKeyRejectedException(observation, guard.trust)
                }
                throw error
            }

            this@JschServerCenterTransport.endpoint = endpoint
            this@JschServerCenterTransport.repository = guard
            this@JschServerCenterTransport.session = created
        }
    }

    override suspend fun run(command: String): ServerCenterSshCommandResult {
        require(command.isNotBlank()) { "The command must not be blank." }
        return withContext(Dispatchers.IO) { execute(command, input = null) }
    }

    override suspend fun runWithInput(command: String, inputLine: String?): ServerCenterSshCommandResult {
        require(command.isNotBlank()) { "The command must not be blank." }
        // The one line of input is fed through the channel's stdin, never as a command argument, so a
        // sudo password stays out of the process list, the logs and the disk.
        val input = if (inputLine.isNullOrEmpty()) null else (inputLine + "\n").toByteArray(Charsets.UTF_8)
        return withContext(Dispatchers.IO) { execute(command, input) }
    }

    override suspend fun upload(
        content: InputStream,
        contentLength: Long?,
        remotePath: String,
        progress: ((Double) -> Unit)?,
    ) {
        require(remotePath.isNotBlank()) { "The remote path is required." }
        withContext(Dispatchers.IO) {
            val sftp = openSftp()
            try {
                val monitor = progress?.let { FractionProgressMonitor(contentLength, it) }
                sftp.put(content, remotePath, monitor, ChannelSftp.OVERWRITE)
            } finally {
                sftp.disconnect()
            }
        }
    }

    override suspend fun download(remotePath: String, destination: OutputStream) {
        require(remotePath.isNotBlank()) { "The remote path is required." }
        withContext(Dispatchers.IO) {
            val sftp = openSftp()
            try {
                sftp.get(remotePath, destination)
            } finally {
                sftp.disconnect()
            }
        }
    }

    override fun openLoopbackTunnel(remotePort: Int, basePath: String?): ServerCenterSshTunnel {
        require(remotePort in 1..65535) { "The remote port must be between 1 and 65535." }
        // Hard constraint: a tunnel that binds anywhere but loopback would expose an
        // authenticated-only service to the local network. There is no configuration switch for this.
        check(ServerTunnelRules.isAcceptableTunnelBindAddress(ServerTunnelRules.LOOPBACK_HOST)) {
            "The tunnel bind address must be loopback."
        }

        val active = requireSession()
        val localPort = try {
            // bind_address first, then local port (0 = OS-assigned), then the remote loopback target.
            active.setPortForwardingL(
                ServerTunnelRules.LOOPBACK_HOST,
                ServerTunnelRules.EPHEMERAL_PORT,
                ServerTunnelRules.LOOPBACK_HOST,
                remotePort,
            )
        } catch (error: JSchException) {
            throw IllegalStateException("Unable to open the loopback tunnel.", error)
        }

        val tunnel = JschLoopbackTunnel(this, localPort, basePath)
        tunnels += tunnel
        return tunnel
    }

    override fun close() {
        if (closed) return
        closed = true
        disconnectLocked()
    }

    internal fun closeTunnel(tunnel: JschLoopbackTunnel) {
        tunnels.remove(tunnel)
        try {
            session?.delPortForwardingL(tunnel.localPort)
        } catch (_: JSchException) {
            // A tunnel whose session is already gone is not an error worth surfacing.
        }
    }

    private fun requireSession(): Session = session?.takeIf { it.isConnected }
        ?: throw IllegalStateException("The SSH connection is not established.")

    private fun openSftp(): ChannelSftp {
        val channel = requireSession().openChannel("sftp") as ChannelSftp
        channel.connect(CHANNEL_CONNECT_TIMEOUT_MILLIS)
        return channel
    }

    /**
     * 执行一条命令并等待结束。stdout 与 stderr 由两个读取线程并行排空：
     * 单线程先读完一路再去读另一路，会在命令写满未读管道时死锁。
     */
    private fun execute(command: String, input: ByteArray?): ServerCenterSshCommandResult {
        val channel = requireSession().openChannel("exec") as ChannelExec
        try {
            channel.setCommand(command)
            channel.setInputStream(input?.let { ByteArrayInputStream(it) })

            // Streams are wired before connect so the channel pumps them as soon as it opens.
            val stdout = channel.inputStream
            val stderr = channel.errStream
            channel.connect(CHANNEL_CONNECT_TIMEOUT_MILLIS)

            val out = ByteArrayOutputStream()
            val err = ByteArrayOutputStream()
            val outReader = drain(stdout, out)
            val errReader = drain(stderr, err)
            outReader.join()
            errReader.join()
            settle(channel)

            return ServerCenterSshCommandResult(
                exitStatus = channel.exitStatus,
                standardOutput = String(out.toByteArray(), Charsets.UTF_8),
                standardError = String(err.toByteArray(), Charsets.UTF_8),
            )
        } finally {
            channel.disconnect()
        }
    }

    private fun drain(stream: InputStream, sink: ByteArrayOutputStream): Thread {
        val thread = Thread {
            val buffer = ByteArray(8192)
            try {
                while (true) {
                    val read = stream.read(buffer)
                    if (read < 0) break
                    if (read > 0) sink.write(buffer, 0, read)
                }
            } catch (_: Exception) {
                // The channel closing under us ends the drain; whatever arrived is kept.
            }
        }
        thread.isDaemon = true
        thread.start()
        return thread
    }

    /** 两路输出已 EOF，通常此刻通道已关闭；短暂等待让退出状态落到通道上。 */
    private fun settle(channel: ChannelExec) {
        val deadline = System.currentTimeMillis() + CHANNEL_SETTLE_MILLIS
        while (!channel.isClosed && System.currentTimeMillis() < deadline) {
            Thread.sleep(10)
        }
    }

    private fun disconnectLocked() {
        for (tunnel in tunnels.toList()) {
            try {
                session?.delPortForwardingL(tunnel.localPort)
            } catch (_: JSchException) {
                // The session is going away; the port dies with it either way.
            }
        }
        tunnels.clear()

        repository = null
        endpoint = null
        session?.disconnect()
        session = null
    }

    private companion object {
        const val IDENTITY_NAME = "relaxkonos-servercenter"
        const val CONNECT_TIMEOUT_MILLIS = 20_000
        const val CHANNEL_CONNECT_TIMEOUT_MILLIS = 20_000
        const val CHANNEL_SETTLE_MILLIS = 5_000L
    }
}

/**
 * JSch 的 loopback 隧道句柄。它只暴露本机端口与基础地址；远端目标固定为远端自身的
 * `127.0.0.1`，因此不能把隧道当成通用端口转发工具使用。
 */
internal class JschLoopbackTunnel(
    private val owner: JschServerCenterTransport,
    override val localPort: Int,
    basePath: String?,
) : ServerCenterSshTunnel {

    override val localBaseUrl: String = ServerTunnelRules.buildLoopbackBaseUrl(localPort, basePath)

    private var closed = false

    override fun close() {
        if (closed) return
        closed = true
        owner.closeTunnel(this)
    }
}

/**
 * 把主机密钥判定接进 JSch 的握手。
 *
 * 它只做判定与记录，不保存任何 known_hosts：固定的主机密钥由本机信任仓库管理，
 * 本仓库不得成为第二个、可被静默写入的信任来源。
 */
private class GuardedHostKeyRepository(
    private val endpoint: ServerCenterSshEndpoint,
    private val guard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
) : HostKeyRepository {

    var observation: ServerCenterHostKeyObservation? = null
        private set

    var trust: ServerHostKeyTrust = ServerHostKeyTrust.Unknown
        private set

    override fun check(host: String, key: ByteArray): Int {
        val seen = ServerCenterHostKeyObservation(
            host = endpoint.host,
            port = endpoint.port,
            algorithm = ServerCenterHostKeyObservation.algorithmOf(key).orEmpty(),
            publicKeyBlob = key.copyOf(),
        )
        observation = seen
        trust = guard(seen)
        return when (trust) {
            ServerHostKeyTrust.Trusted -> HostKeyRepository.OK
            ServerHostKeyTrust.Changed -> HostKeyRepository.CHANGED
            ServerHostKeyTrust.Unknown -> HostKeyRepository.NOT_INCLUDED
        }
    }

    override fun add(hostKey: HostKey, userInfo: UserInfo?) = Unit

    override fun remove(host: String, type: String) = Unit

    override fun remove(host: String, type: String, key: ByteArray) = Unit

    override fun getKnownHostsRepositoryID(): String = ""

    override fun getHostKey(): Array<HostKey> = emptyArray()

    override fun getHostKey(host: String, type: String): Array<HostKey> = emptyArray()
}

/**
 * 为键盘交互式认证提供本次会话的密码。
 *
 * JSch 的键盘交互接口以 `String` 取密码，因此这里会把会话持有的 `CharArray` 转成一次瞬时
 * `String`；密码不落盘、不进日志，也不进入任何持久对象。
 */
private class PasswordUserInfo(private val password: CharArray) : UserInfo {

    override fun getPassphrase(): String? = null

    override fun getPassword(): String = String(password)

    override fun promptPassword(message: String): Boolean = true

    override fun promptPassphrase(message: String): Boolean = false

    override fun promptYesNo(message: String): Boolean = false

    override fun showMessage(message: String) = Unit
}

/**
 * 把 SFTP 的字节计数换算成进度。只有调用方给出了长度才上报比例，
 * 否则界面宁可不动，也不编造一个看似真实的百分比。
 */
private class FractionProgressMonitor(
    private val contentLength: Long?,
    private val report: (Double) -> Unit,
) : SftpProgressMonitor {

    override fun init(op: Int, src: String?, dest: String?, max: Long) = Unit

    override fun count(count: Long): Boolean {
        val total = contentLength
        if (total != null && total > 0) {
            report((count.toDouble() / total).coerceIn(0.0, 1.0))
        }
        return true
    }

    override fun end() = Unit
}