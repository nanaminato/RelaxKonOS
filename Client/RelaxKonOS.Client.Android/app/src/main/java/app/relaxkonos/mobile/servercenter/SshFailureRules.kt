package app.relaxkonos.mobile.servercenter

import com.jcraft.jsch.JSchException
import com.jcraft.jsch.JSchSessionDisconnectException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

/**
 * 一次 SSH 握手失败的原因类别。
 *
 * 它不是「错误信息」的包装，而是**结论**：同一句「无法验证 SSH 连接」把「手机到主机根本没连上」
 * 和「密码不对」说成同一件事，用户除了逐个猜测无事可做。归类只依赖异常类型与 SSH 库的固定消息
 * 前缀及结构化断开原因，绝不携带端点、用户名、密码或原始消息（见 [diagnosticName]）。
 *
 * [diagnosticName] 同时是 `adb logcat -s RelaxKonSsh:D` 里的稳定分类名，界面文案与诊断证据因此
 * 永远指向同一件事。
 */
enum class SshFailureReason(val diagnosticName: String) {
    /** 主机拒绝了账号或密码（SSH `Auth fail`）。 */
    AuthenticationRejected("authentication_failed"),

    /** 主机主动中止认证（SSH `Auth cancel`），通常是尝试次数超限或服务端策略拒绝。 */
    AuthenticationCancelled("authentication_cancelled"),

    /** TCP 连接超时：主机没有响应。地址不可达与端口被防火墙丢弃都会落到这里。 */
    TimedOut("connect_timed_out"),

    /** TCP 被拒绝或不可路由：端口上没有服务监听，或路径上被明确拒绝。 */
    ConnectionRefused("tcp_connect_failed"),

    /** 主机名无法解析。 */
    NameNotResolved("name_resolution_failed"),

    /** 端与客户端没有共同支持的算法或主机密钥类型。 */
    AlgorithmNegotiation("algorithm_negotiation_failed"),

    /** 主机密钥未被守卫接受，握手中止（正常情况下由指纹确认流程接管）。 */
    HostKeyRejected("host_key_rejected"),

    /** SSH 协议层中止，但库消息没有给出可判定的原因。 */
    HandshakeFailed("jsch_handshake_failed"),

    /** 不属于以上任何一类。 */
    Unexpected("unexpected_failure"),
}

/**
 * 握手异常的归类规则。纯函数，不含 Android 依赖，可在 JVM 里直接验证。
 *
 * 判定顺序不能随意调换：网络层原因（DNS → 超时 → 拒绝）优先于 SSH 库消息，因为 JSch 会把
 * `SocketTimeoutException`、`ConnectException` 包进 `JSchException`；反过来先看库消息，就会把
 * 「手机连不上主机」说成「密码错误」，正是这次要修掉的误导。
 */
object SshFailureRules {

    fun classify(error: Throwable): SshFailureReason {
        if (error.findCause<UnknownHostException>() != null) return SshFailureReason.NameNotResolved
        if (error.findCause<SocketTimeoutException>() != null) return SshFailureReason.TimedOut
        if (error.findCause<ConnectException>() != null ||
            error.findCause<NoRouteToHostException>() != null
        ) {
            return SshFailureReason.ConnectionRefused
        }
        val jsch = error.findCause<JSchException>()
        return if (jsch == null) SshFailureReason.Unexpected else classifyJsch(jsch)
    }

    /**
     * 只比对库消息的固定前缀，不复制消息内容：服务端与代理都可能把端点数据写进消息，
     * 而界面与日志都不该出现它们。
     */
    private fun classifyJsch(error: JSchException): SshFailureReason = when {
        // RFC 4253 disconnect codes: 14 exhausts authentication methods, 15 rejects the user.
        // OpenSSH uses protocol-error code 2 for MaxAuthTries; only its explicit fixed description
        // establishes an authentication rejection. Other protocol errors keep their own category.
        error is JSchSessionDisconnectException &&
            (error.reasonCode == 14 || error.reasonCode == 15 ||
                error.description == "Too many authentication failures") -> SshFailureReason.AuthenticationRejected
        error is JSchSessionDisconnectException && error.reasonCode == 13 -> SshFailureReason.AuthenticationCancelled
        error.message?.startsWith("Auth fail") == true -> SshFailureReason.AuthenticationRejected
        error.message?.startsWith("Auth cancel") == true -> SshFailureReason.AuthenticationCancelled
        error.message?.startsWith("Algorithm negotiation fail") == true -> SshFailureReason.AlgorithmNegotiation
        error.message?.startsWith("reject HostKey") == true -> SshFailureReason.HostKeyRejected
        else -> SshFailureReason.HandshakeFailed
    }

    private inline fun <reified T : Throwable> Throwable.findCause(): T? {
        var current: Throwable? = this
        while (current != null) {
            if (current is T) return current
            current = current.cause
        }
        return null
    }
}
