package app.relaxkonos.mobile.servercenter

import java.io.InputStream
import java.io.OutputStream

/**
 * SSH 端点：主机、端口与登录用户。主机密钥与 SSH 凭据都绑定到这个三元组，
 * 因此同一台宿主的两个 SSH 用户不会互相覆盖对方的信任决定。与 C# `ServerCenterSshEndpoint` 一致。
 */
data class ServerCenterSshEndpoint(val host: String, val port: Int, val userName: String) {

    /** 主机密钥的端点键 `host:port`；不含用户，因为主机密钥属于宿主而不是账号。 */
    val endpointKey: String get() = ServerHostTrustRules.endpointKey(host, port)

    /** 展示用名称，永不包含凭据。 */
    val displayName: String get() = "${ServerHostTrustRules.normalizeHost(host)}:$port"

    companion object {
        fun create(host: String, port: Int, userName: String): ServerCenterSshEndpoint {
            require(host.isNotBlank()) { "SSH host is required." }
            require(port in 1..65535) { "SSH port must be between 1 and 65535." }
            require(userName.isNotBlank()) { "SSH user is required." }
            return ServerCenterSshEndpoint(ServerHostTrustRules.normalizeHost(host), port, userName.trim())
        }
    }
}

/** 一次远端命令的执行结果。原始输出只作为受限诊断附件，默认不展示、不上传。 */
data class ServerCenterSshCommandResult(
    val exitStatus: Int,
    val standardOutput: String,
    val standardError: String,
) {
    val succeeded: Boolean get() = exitStatus == 0
}

/**
 * 本次连接观测到的宿主密钥。用户核对指纹后由信任仓库固定；指纹不是秘密。
 *
 * [publicKeyBlob] 是 SSH wire 格式的公钥字节，与 C# `ServerCenterHostKeyObservation.PublicKeyBlob`
 * 及 OpenSSH 的 `SHA256:` 指纹口径一致，因此两端对同一把密钥得到同一指纹。
 */
class ServerCenterHostKeyObservation(
    val host: String,
    val port: Int,
    val algorithm: String,
    val publicKeyBlob: ByteArray,
) {
    val fingerprint: String get() = ServerHostTrustRules.fingerprint(publicKeyBlob)

    val groupedFingerprint: String get() = ServerHostTrustRules.groupedFingerprint(fingerprint)

    /** 只暴露非秘密字段；公钥字节不进日志。 */
    override fun toString(): String = "ServerCenterHostKeyObservation($host:$port, $algorithm, $fingerprint)"

    companion object {
        /**
         * 从 SSH wire 公钥字节读出算法名（前缀是一个长度前缀字符串）。
         * 自行解析而不是依赖库字段，保证两端的观测值与指纹口径一致。
         */
        fun algorithmOf(publicKeyBlob: ByteArray): String? {
            if (publicKeyBlob.size < 4) return null
            val length = ((publicKeyBlob[0].toInt() and 0xFF) shl 24) or
                ((publicKeyBlob[1].toInt() and 0xFF) shl 16) or
                ((publicKeyBlob[2].toInt() and 0xFF) shl 8) or
                (publicKeyBlob[3].toInt() and 0xFF)
            if (length <= 0 || 4 + length > publicKeyBlob.size) return null
            return String(publicKeyBlob, 4, length, Charsets.US_ASCII)
        }
    }
}

/**
 * 主机密钥未被本会话接受。它不是一个可重试的瞬时故障：
 * [ServerHostKeyTrust.Unknown] 需要用户核对指纹，[ServerHostKeyTrust.Changed]
 * 必须阻断写操作并由用户显式更新固定记录。
 */
class ServerCenterHostKeyRejectedException(
    val observation: ServerCenterHostKeyObservation,
    val trust: ServerHostKeyTrust,
) : Exception("The SSH host key was not accepted for this session.") {

    val problemCode: String =
        ServerHostTrustRules.problemCode(trust) ?: ServerDeploymentProblemCodes.HOST_KEY_UNKNOWN
}

/**
 * 一条 loopback 隧道：把远端 loopback 上的服务映射到本机一个由系统分配的端口。
 * 绑定地址固定为 `127.0.0.1`，不提供改绑到非回环地址的入口。
 */
interface ServerCenterSshTunnel : AutoCloseable {

    /** 本机监听端口，由操作系统分配。 */
    val localPort: Int

    /** 本次会话使用的基础地址，例如 `http://127.0.0.1:51000`。 */
    val localBaseUrl: String
}

/**
 * 客户端内置的 SSH/SFTP 传输。它只提供传输原语；「能执行什么」由服务器中心的操作层用固定枚举约束，
 * 界面不提供自定义远程命令。任何实现都不得调用系统 `ssh/scp/sftp` 命令或外部 SSH App 作为必需路径。
 *
 * [connect] 的主机密钥守卫只有返回 [ServerHostKeyTrust.Trusted] 才允许继续，
 * 不存在等价于 `StrictHostKeyChecking=no` 的静默接受路径。
 */
interface ServerCenterSshTransport : AutoCloseable {

    val isConnected: Boolean

    /** 本次连接观测到的宿主密钥；未连接时为 null。 */
    val observedHostKey: ServerCenterHostKeyObservation?

    suspend fun connect(
        endpoint: ServerCenterSshEndpoint,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    )

    /** 执行一条固定命令并等待结束。 */
    suspend fun run(command: String): ServerCenterSshCommandResult

    /**
     * 执行命令并可写入一行标准输入。sudo 口令只送入当前会话，
     * 不进入命令行参数、日志或磁盘。
     */
    suspend fun runWithInput(command: String, inputLine: String?): ServerCenterSshCommandResult

    /**
     * 经 SFTP 上传到受控暂存目录。目标路径由操作层给出，不接受用户输入。
     * [contentLength] 未知时不上报进度百分比——界面不得凭空编造进度。
     */
    suspend fun upload(
        content: InputStream,
        contentLength: Long?,
        remotePath: String,
        progress: ((Double) -> Unit)? = null,
    )

    /** 经 SFTP 下载（用于导出备份）。 */
    suspend fun download(remotePath: String, destination: OutputStream)

    /**
     * 打开一条到远端 loopback 端口的隧道。[remotePort] 必须是远端自身的回环端口，
     * 不接受任意主机名，避免把隧道变成通用的端口转发工具。
     */
    fun openLoopbackTunnel(remotePort: Int, basePath: String? = null): ServerCenterSshTunnel
}

/** 创建传输实例。每次会话一条独立连接，由会话负责释放。 */
interface ServerCenterSshTransportFactory {
    fun create(): ServerCenterSshTransport
}