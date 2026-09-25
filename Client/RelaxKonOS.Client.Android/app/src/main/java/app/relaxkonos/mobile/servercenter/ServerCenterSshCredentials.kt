package app.relaxkonos.mobile.servercenter

import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultAccess
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.security.decodeUtf8
import app.relaxkonos.mobile.security.encodeUtf8

/** 已保存的 SSH 凭据种类。 */
enum class SshCredentialKind { Password, PrivateKey }

/**
 * 本次连接使用的 SSH 认证材料。密码、私钥文本与口令只在本进程内存中存在：
 * 是否保存由用户显式选择，且只能进入设备安全存储，绝不进入日志、诊断导出或工作区同步。
 *
 * 调用方在连接结束后必须调用 [clear]。
 */
class SshCredential(
    val kind: SshCredentialKind,
    val secret: CharArray,
    val passphrase: CharArray?,
) {
    /** 清零本次会话持有的秘密。幂等。 */
    fun clear() {
        secret.fill('\u0000')
        passphrase?.fill('\u0000')
    }

    /** 只暴露种类；秘密绝不进入字符串、日志或崩溃报告。 */
    override fun toString(): String = "SshCredential($kind)"
}

/**
 * SSH 凭据在保险箱载荷里的文本编码。
 *
 * 保险箱本身已加密，这里的 Base64 只用于消除歧义：私钥是多行文本，口令可能包含任意字符，
 * 用分隔符拼接会需要转义规则，而 Base64 让「哪里结束」永远明确。
 *
 * - 密码：`P\u001f<base64(secret)>`
 * - 私钥：`K\u001f<base64(passphrase)> \u001f<base64(privateKey)>`
 */
internal object SshCredentialCodec {
    private const val SEPARATOR = '\u001f'
    private const val PASSWORD = 'P'
    private const val PRIVATE_KEY = 'K'

    fun encode(credential: SshCredential): CharArray {
        val secret = Base64Codec.encode(encodeUtf8(credential.secret))
        val encoded = when (credential.kind) {
            SshCredentialKind.Password -> "$PASSWORD$SEPARATOR$secret"

            SshCredentialKind.PrivateKey ->
                "$PRIVATE_KEY$SEPARATOR" +
                    Base64Codec.encode(encodeUtf8(credential.passphrase ?: CharArray(0))) +
                    SEPARATOR + secret
        }
        return encoded.toCharArray()
    }

    /** 解出凭据；载荷不是本编码的形状时返回 null，调用方按「记录不可用」处理。 */
    fun decode(payload: CharArray): SshCredential? = try {
        val parts = String(payload).split(SEPARATOR)
        when (parts.firstOrNull()) {
            PASSWORD.toString() ->
                parts.getOrNull(1)?.let { SshCredential(SshCredentialKind.Password, decodeUtf8(Base64Codec.decode(it)), null) }

            PRIVATE_KEY.toString() -> {
                val passphrase = parts.getOrNull(1)?.let { decodeUtf8(Base64Codec.decode(it)) }
                val secret = parts.getOrNull(2)?.let { decodeUtf8(Base64Codec.decode(it)) }
                if (secret == null) {
                    passphrase?.fill('\u0000')
                    null
                } else {
                    SshCredential(
                        SshCredentialKind.PrivateKey,
                        secret,
                        passphrase?.takeIf { it.isNotEmpty() },
                    )
                }
            }

            else -> null
        }
    } catch (_: IllegalArgumentException) {
        null
    }
}

/**
 * Android 端已保存 SSH 凭据的仓库。
 *
 * 它使用**独立于登录与提权凭据**的保险箱域：[VaultKind.Ssh] 有自己的文件（`ssh-vault.bin`）
 * 与 Keystore alias（`rk.ssh.vault`），AAD 又按 `Ssh|host:port|user` 绑定，因此既不会与另两个
 * 保险箱互相命中，也不因用户名或密码看起来相同而复用记录。现有 debug-only 明文登录兜底
 * 不适用于 SSH（`RelaxKonOS.Mobile.ServerCenter.Design.md` §3）。
 */
class ServerCenterSshCredentialStore(
    private val vault: CredentialVault,
    private val access: VaultAccess,
) {
    /** 已保存凭据的密文记录，供宿主详情与账号安全页展示。记录里没有秘密。 */
    fun records(): List<VaultRecord> = vault.records(VaultKind.Ssh)

    /** 按 SSH 端点与用户查找记录。同一宿主的不同 SSH 用户是两条独立记录。 */
    fun record(host: String, port: Int, userName: String): VaultRecord? =
        vault.record(VaultKind.Ssh, endpointIdentity(host, port), userName)

    /** 保存或替换一条凭据。必须先取得用户对本次保存的显式确认（由 [VaultAccess] 完成）。 */
    suspend fun save(
        mode: VaultUnlockMode,
        host: String,
        port: Int,
        userName: String,
        credential: SshCredential,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
        nowEpochMillis: Long,
    ): VaultOperation<VaultRecord> {
        val payload = SshCredentialCodec.encode(credential)
        return try {
            access.save(
                kind = VaultKind.Ssh,
                mode = mode,
                serverUrl = endpointIdentity(host, port),
                account = userName,
                password = payload,
                activity = activity,
                title = title,
                subtitle = subtitle,
                negativeButton = negativeButton,
                nowEpochMillis = nowEpochMillis,
            )
        } finally {
            payload.fill('\u0000')
        }
    }

    /** 解密一条凭据。生物识别确认由 [VaultAccess] 负责，本方法不改变任何记录。 */
    suspend fun load(
        mode: VaultUnlockMode,
        record: VaultRecord,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
    ): VaultOperation<SshCredential> =
        access.load(mode, record, activity, title, subtitle, negativeButton).mapDecoded()

    /**
     * 在已授权的设备解锁窗口内读取，不弹确认。窗口过期、设备锁定或密钥失效都会返回失败，
     * 调用方据此回落到需要确认的 [load]。
     */
    fun loadWithoutPrompt(record: VaultRecord): VaultOperation<SshCredential> =
        access.loadWithoutPrompt(record).mapDecoded()

    /** 删除一条凭据（用户显式「不再保存该宿主的 SSH 凭据」）。不影响宿主资料与登录记录。 */
    fun forget(host: String, port: Int, userName: String) {
        vault.delete(VaultKind.Ssh, endpointIdentity(host, port), userName)
    }

    fun forget(record: VaultRecord) {
        vault.delete(record)
    }

    private fun endpointIdentity(host: String, port: Int): String =
        ServerHostTrustRules.endpointKey(host, port)

    /**
     * 载荷解不出合法凭据时按「不可用」处理：密文虽然通过了 GCM 认证，但内容不是本客户端写入的
     * SSH 凭据，继续使用只会把错误推到连接阶段。
     */
    private fun VaultOperation<CharArray>.mapDecoded(): VaultOperation<SshCredential> =
        when (this) {
            is VaultOperation.Success -> {
                val credential = SshCredentialCodec.decode(value)
                value.fill('\u0000')
                if (credential == null) {
                    VaultOperation.Failed(UnlockFailure.Tampered)
                } else {
                    VaultOperation.Success(credential)
                }
            }

            VaultOperation.Cancelled -> VaultOperation.Cancelled
            is VaultOperation.Failed -> this
        }
}