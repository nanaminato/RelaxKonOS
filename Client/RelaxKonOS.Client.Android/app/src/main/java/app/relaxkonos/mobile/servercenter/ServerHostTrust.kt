package app.relaxkonos.mobile.servercenter

import java.security.MessageDigest
import java.util.Locale

/**
 * 一条已确认的 SSH 主机密钥记录。按 `(host, port, algorithm)` 保存：同一台宿主可以同时提供
 * rsa 与 ed25519 两种主机密钥，它们是两条独立记录，改动其一不影响另一条。
 *
 * 主机密钥与指纹都不是秘密，可以明文保存；但必须抗意外覆盖，并在变化时阻断写操作。
 */
data class ServerHostKeyRecord(
    val host: String,
    val port: Int,
    val algorithm: String,
    /** 主机公钥原始字节（SSH wire 格式）的 Base64，用于与观测值逐字节比较。 */
    val publicKeyBase64: String,
    /** 展示与诊断用的 `SHA256:` 指纹；不参与匹配判断。 */
    val fingerprint: String,
    val confirmedAtEpochMillis: Long,
)

/**
 * SSH 主机密钥的固定规则。桌面与 Android 必须给出同一判断，否则同一台宿主会在两端得到不同的
 * 信任结论。任何实现都不得提供绕过入口（例如等价于 `StrictHostKeyChecking=no` 的静默接受）。
 */
object ServerHostTrustRules {

    /** SSH 默认端口。显式写出端口时也用它，保证 `host` 与 `host:22` 是同一端点。 */
    const val DEFAULT_PORT = 22

    /** 指纹前缀，与 OpenSSH 展示形式一致。 */
    const val FINGERPRINT_PREFIX = "SHA256:"

    /** 端点键：`host:port`。主机名统一小写，端口总是显式给出。 */
    fun endpointKey(host: String, port: Int): String = "${normalizeHost(host)}:$port"

    /** 规范化主机名：去空白、去 IPv6 方括号、转小写。DNS 名称不区分大小写。 */
    fun normalizeHost(host: String): String {
        val trimmed = host.trim()
        val unwrapped = if (trimmed.length >= 2 && trimmed.first() == '[' && trimmed.last() == ']') {
            trimmed.substring(1, trimmed.length - 1)
        } else {
            trimmed
        }
        return unwrapped.lowercase(Locale.ROOT)
    }

    /** 由主机公钥原始字节计算 `SHA256:` 指纹（Base64，去掉 `=` 填充），与 OpenSSH 输出一致。 */
    fun fingerprint(publicKeyBlob: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(publicKeyBlob)
        return FINGERPRINT_PREFIX + Base64Codec.encode(digest).trimEnd('=')
    }

    /** 指纹的展示形式：按 4 个字符分组，便于用户逐段核对。 */
    fun groupedFingerprint(fingerprint: String): String {
        val hasPrefix = fingerprint.startsWith(FINGERPRINT_PREFIX)
        val body = if (hasPrefix) fingerprint.substring(FINGERPRINT_PREFIX.length) else fingerprint
        val grouped = body.chunked(4).joinToString(" ")
        return if (hasPrefix) FINGERPRINT_PREFIX + grouped else grouped
    }

    /** 校验指纹形状：`SHA256:` 加 43 个 Base64 字符（256 位去掉填充后的长度）。 */
    fun isFingerprint(value: String?): Boolean {
        if (value.isNullOrBlank() || !value.startsWith(FINGERPRINT_PREFIX)) return false
        val body = value.substring(FINGERPRINT_PREFIX.length)
        if (body.length != 43) return false
        return body.all { it in '0'..'9' || it in 'a'..'z' || it in 'A'..'Z' || it == '+' || it == '/' }
    }

    /** 同端点同算法的既有记录。 */
    fun find(known: List<ServerHostKeyRecord>, host: String, port: Int, algorithm: String): ServerHostKeyRecord? {
        val key = endpointKey(host, port)
        return known.firstOrNull { endpointKey(it.host, it.port) == key && it.algorithm == algorithm }
    }

    /**
     * 判定本次观测到的宿主密钥。首次见到某端点或某算法返回 [ServerHostKeyTrust.Unknown]，
     * 必须由用户核对指纹后才可继续；与既有记录不一致返回 [ServerHostKeyTrust.Changed]，
     * 该状态下所有写操作都必须被阻断。
     */
    fun evaluate(
        known: List<ServerHostKeyRecord>,
        host: String,
        port: Int,
        algorithm: String,
        publicKeyBlob: ByteArray,
    ): ServerHostKeyTrust {
        val existing = find(known, host, port, algorithm) ?: return ServerHostKeyTrust.Unknown
        val observed = Base64Codec.encode(publicKeyBlob)
        return if (existing.publicKeyBase64 == observed) ServerHostKeyTrust.Trusted else ServerHostKeyTrust.Changed
    }

    /**
     * 写操作门禁。只有 [ServerHostKeyTrust.Trusted] 允许安装、升级、修复、卸载、回滚；
     * [ServerHostKeyTrust.Unknown] 在用户当次确认后即转为可信，因此它是「需要确认」而不是「禁止」。
     */
    fun blocksWriteOperations(trust: ServerHostKeyTrust): Boolean = trust == ServerHostKeyTrust.Changed

    /** 该信任状态对应的稳定问题码；[ServerHostKeyTrust.Trusted] 时为 null。 */
    fun problemCode(trust: ServerHostKeyTrust): String? = when (trust) {
        ServerHostKeyTrust.Unknown -> ServerDeploymentProblemCodes.HOST_KEY_UNKNOWN
        ServerHostKeyTrust.Changed -> ServerDeploymentProblemCodes.HOST_KEY_CHANGED
        ServerHostKeyTrust.Trusted -> null
    }

    /** 观测到密钥变化时替换既有记录的规则：同端点同算法只保留一条，且必须由用户显式确认。 */
    fun replace(known: List<ServerHostKeyRecord>, confirmed: ServerHostKeyRecord): List<ServerHostKeyRecord> {
        val key = endpointKey(confirmed.host, confirmed.port)
        return known.filterNot { endpointKey(it.host, it.port) == key && it.algorithm == confirmed.algorithm } +
            confirmed
    }
}