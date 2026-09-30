package app.relaxkonos.mobile.servercenter

import app.relaxkonos.mobile.security.encodeUtf8
import com.jcraft.jsch.ChannelExec
import com.jcraft.jsch.ChannelShell
import com.jcraft.jsch.ChannelSftp
import com.jcraft.jsch.SftpATTRS
import com.jcraft.jsch.HostKey
import com.jcraft.jsch.HostKeyRepository
import com.jcraft.jsch.JSch
import com.jcraft.jsch.JSchException
import com.jcraft.jsch.Session
import com.jcraft.jsch.SftpProgressMonitor
import com.jcraft.jsch.UIKeyboardInteractive
import com.jcraft.jsch.UserInfo
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.InputStreamReader
import java.io.IOException
import java.io.OutputStream
import java.util.concurrent.atomic.AtomicBoolean
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
            // This repository deliberately exposes no JSch known_hosts list: trust is decided by
            // GuardedHostKeyRepository.check and our separately persisted pins. JSch's default
            // pre-KEX "prefer known host key types" optimization still calls getHostKey() to
            // reorder algorithms, which is both meaningless here and crashes on Android in this
            // JSch path before any host key is received. Disable only that optimization; strict
            // host-key verification below remains enabled.
            created.setConfig("prefer_known_host_key_types", "no")
            // JSch 2.x deliberately omits the legacy ssh-rsa host-key *signature* algorithm from
            // its default offer. SSH.NET (the desktop transport) and Termius still negotiate it
            // with older OpenSSH servers, so retain it as the final compatibility option here.
            // This does not weaken host identity handling: every received key still goes through
            // GuardedHostKeyRepository and StrictHostKeyChecking before authentication starts.
            created.setConfig(
                "server_host_key",
                listOf(created.getConfig("server_host_key"), "ssh-rsa")
                    .filterNotNull()
                    .filter { it.isNotBlank() }
                    .joinToString(","),
            )
            // Keep the password flow identical to the desktop SSH launcher.  In particular, do not
            // offer public-key authentication for a password-only session: restrictive sshd/PAM
            // configurations can count that attempt toward MaxAuthTries before the password method
            // is reached. Private-key sessions still add an in-memory identity above and explicitly
            // opt into publickey.
            created.setConfig(
                "PreferredAuthentications",
                if (credential.kind == SshCredentialKind.PrivateKey) {
                    "publickey,password,keyboard-interactive"
                } else {
                    "password,keyboard-interactive"
                },
            )

            SshDiagnostics.trace(
                "connect.begin",
                "credential=${credential.kind} auth=${created.getConfig("PreferredAuthentications")} " +
                    "strict_host_key=yes known_key_reordering=no",
            )

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
                    SshDiagnostics.trace(
                        "host_key.rejected",
                        "trust=${guard.trust} algorithm=${observation.algorithm}",
                    )
                    throw ServerCenterHostKeyRejectedException(observation, guard.trust)
                }
                SshDiagnostics.failure("connect.failed", error)
                throw error
            } catch (error: Exception) {
                // JSch can surface Android-provider and socket failures as ordinary runtime
                // exceptions rather than JSchException. The coordinator intentionally reduces
                // all of them to one safe UI sentence, so preserve the classified type in the
                // debug-only diagnostic sink before propagating it.
                created.disconnect()
                SshDiagnostics.failure("connect.failed", error)
                throw error
            }

            SshDiagnostics.trace(
                "connect.authenticated",
                "kex=${created.kexAlgorithm} host_key=${created.serverHostKeyAlgorithm} " +
                    "cipher_c2s=${created.cipherAlgorithmC2S} cipher_s2c=${created.cipherAlgorithmS2C}",
            )
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

    override suspend fun openTerminal(): ServerCenterSshTerminal = withContext(Dispatchers.IO) {
        val channel = requireSession().openChannel("shell") as ChannelShell
        try {
            channel.setPty(true)
            channel.setPtyType("dumb")
            channel.setPtySize(80, 24, 640, 384)
            val terminal = JschInteractiveTerminal(channel, channel.inputStream, channel.outputStream)
            channel.connect(CHANNEL_CONNECT_TIMEOUT_MILLIS)
            terminal
        } catch (error: Exception) {
            channel.disconnect()
            throw error
        }
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

    override suspend fun listDirectory(remotePath: String): List<SshFileEntry> = withContext(Dispatchers.IO) {
        val sftp = openSftp()
        try {
            @Suppress("UNCHECKED_CAST")
            val entries = sftp.ls(remotePath) as java.util.Vector<*>
            entries.asSequence().map { it as ChannelSftp.LsEntry }
                .filterNot { it.filename == "." || it.filename == ".." }
                .map { entry ->
                    val attributes = entry.attrs
                    SshFileEntry(
                        path = childPath(remotePath, entry.filename),
                        name = entry.filename,
                        isDirectory = attributes.isDir,
                        isSymbolicLink = attributes.isLink,
                        size = if (attributes.isDir) null else attributes.size,
                        modifiedAtEpochMillis = attributes.mTime.toLong().takeIf { it > 0 }?.times(1_000),
                    )
                }
                .sortedWith(compareByDescending<SshFileEntry> { it.isDirectory }.thenBy { it.name.lowercase() })
                .toList()
        } finally {
            sftp.disconnect()
        }
    }

    override suspend fun createDirectory(remotePath: String) = withContext(Dispatchers.IO) {
        val sftp = openSftp()
        try {
            sftp.mkdir(remotePath)
        } finally {
            sftp.disconnect()
        }
    }

    override suspend fun delete(remotePath: String, recursive: Boolean) = withContext(Dispatchers.IO) {
        val sftp = openSftp()
        try {
            val attributes = sftp.lstat(remotePath)
            if (attributes.isDir && !attributes.isLink) {
                if (recursive) deleteDirectoryTree(sftp, remotePath)
                else sftp.rmdir(remotePath)
            } else {
                sftp.rm(remotePath)
            }
        } finally {
            sftp.disconnect()
        }
    }

    override suspend fun rename(sourcePath: String, destinationPath: String) = withContext(Dispatchers.IO) {
        val sftp = openSftp()
        try {
            sftp.rename(sourcePath, destinationPath)
        } finally {
            sftp.disconnect()
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

    private fun deleteDirectoryTree(sftp: ChannelSftp, path: String) {
        @Suppress("UNCHECKED_CAST")
        val entries = sftp.ls(path) as java.util.Vector<*>
        for (item in entries) {
            val entry = item as ChannelSftp.LsEntry
            if (entry.filename == "." || entry.filename == "..") continue
            val child = childPath(path, entry.filename)
            val attrs: SftpATTRS = entry.attrs
            if (attrs.isDir && !attrs.isLink) deleteDirectoryTree(sftp, child) else sftp.rm(child)
        }
        sftp.rmdir(path)
    }

    private fun childPath(parent: String, name: String): String =
        if (parent == "/") "/$name" else parent.trimEnd('/') + "/" + name

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
            val outputOverflowed = AtomicBoolean(false)
            val outputFailed = AtomicBoolean(false)
            val outReader = drain(stdout, out, outputOverflowed, outputFailed)
            val errReader = drain(stderr, err, outputOverflowed, outputFailed)
            outReader.join()
            errReader.join()
            settle(channel)
            if (outputFailed.get()) throw IOException("SSH command output could not be read completely.")
            if (outputOverflowed.get()) throw IOException("SSH command output exceeds its size limit.")

            return ServerCenterSshCommandResult(
                exitStatus = channel.exitStatus,
                standardOutput = String(out.toByteArray(), Charsets.UTF_8),
                standardError = String(err.toByteArray(), Charsets.UTF_8),
            )
        } finally {
            channel.disconnect()
        }
    }

    private fun drain(stream: InputStream, sink: ByteArrayOutputStream, overflowed: AtomicBoolean,
        failed: AtomicBoolean): Thread {
        val thread = Thread {
            val buffer = ByteArray(8192)
            try {
                while (true) {
                    val read = stream.read(buffer)
                    if (read < 0) break
                    if (read > 0) {
                        val remaining = MAX_COMMAND_OUTPUT_BYTES - sink.size()
                        if (read > remaining) overflowed.set(true)
                        if (remaining > 0) sink.write(buffer, 0, minOf(read, remaining))
                    }
                }
            } catch (_: Exception) {
                failed.set(true)
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
        const val MAX_COMMAND_OUTPUT_BYTES = 1024 * 1024
    }
}

/** PTY streams stay live across commands, so shell state such as the working directory is retained. */
internal class JschInteractiveTerminal(
    private val channel: ChannelShell,
    input: InputStream,
    private val output: OutputStream,
) : ServerCenterSshTerminal {
    private val reader = InputStreamReader(input, Charsets.UTF_8)
    private val writerGate = Mutex()

    override suspend fun read(): String? = withContext(Dispatchers.IO) {
        val buffer = CharArray(4096)
        val count = reader.read(buffer)
        if (count < 0) null else String(buffer, 0, count)
    }

    override suspend fun write(value: String) = withContext(Dispatchers.IO) {
        writerGate.withLock {
            check(channel.isConnected) { "The SSH terminal is disconnected." }
            output.write(value.toByteArray(Charsets.UTF_8))
            output.flush()
        }
    }

    override suspend fun resize(columns: Int, rows: Int) = withContext(Dispatchers.IO) {
        // setPtySize sends an SSH packet synchronously. JSch swallows its exceptions, so a
        // main-thread socket write can silently leave the cipher out of sync with the peer.
        writerGate.withLock {
            if (channel.isConnected) channel.setPtySize(columns, rows, 0, 0)
        }
    }

    override fun close() {
        channel.disconnect()
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
        SshDiagnostics.trace("host_key.observed", "trust=$trust algorithm=${seen.algorithm}")
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
internal class PasswordUserInfo(private val password: CharArray) : UserInfo, UIKeyboardInteractive {

    override fun getPassphrase(): String? = null

    override fun getPassword(): String = String(password)

    override fun promptPassword(message: String): Boolean = true

    override fun promptPassphrase(message: String): Boolean = false

    override fun promptYesNo(message: String): Boolean = false

    override fun showMessage(message: String) = Unit

    /**
     * OpenSSH/PAM often exposes a password-only login as keyboard-interactive rather than the
     * SSH "password" method.  Merely listing keyboard-interactive in PreferredAuthentications is
     * insufficient: JSch also needs this callback to return the response.
     *
     * Only hidden prompts can receive the password.  Refusing visible or multi-question prompts
     * avoids accidentally submitting the SSH password as an OTP, a consent answer, or some other
     * challenge the UI cannot represent safely.
     */
    override fun promptKeyboardInteractive(
        destination: String?,
        name: String?,
        instruction: String?,
        prompt: Array<out String>?,
        echo: BooleanArray?,
    ): Array<String>? {
        val accepted = prompt != null && echo != null && prompt.size == 1 && echo.size == 1 && !echo[0]
        SshDiagnostics.trace(
            "auth.keyboard_interactive",
            "questions=${prompt?.size ?: 0} hidden=${echo?.all { !it } ?: false} accepted=$accepted",
        )
        if (!accepted) return null
        return arrayOf(String(password))
    }

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
