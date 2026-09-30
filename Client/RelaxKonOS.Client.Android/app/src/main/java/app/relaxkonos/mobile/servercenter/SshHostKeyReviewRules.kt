package app.relaxkonos.mobile.servercenter

/**
 * 用户在「核对这台主机的密钥」这一步要看到的那一次核对。
 *
 * 首次见面与密钥变更共用同一个确认动作——两者都必须由用户显式确认后才写入固定，
 * `ServerHostTrustRules` 禁止任何静默接受的入口——但它们要让用户判断的事情不同，所以不合并成
 * 一种：[previous] 为空时只需核对新指纹；非空时还必须同时看到被取代的那张旧指纹，否则用户
 * 无法分辨「同一个地址换了机器」和「同一台机器换了密钥」。
 */
data class SshHostKeyReview(
    val observation: ServerCenterHostKeyObservation,
    /** 被本次观测取代的固定记录；为空即「首次见面」。 */
    val previous: ServerHostKeyRecord?,
) {
    /** 这一步是「替换已固定的密钥」而不是「首次固定」。 */
    val replacesPinnedKey: Boolean get() = previous != null
}

/**
 * 本次握手结果是否需要用户核对主机密钥之后才能继续。
 *
 * 这不是「能不能连」的判定，而是「还差哪一步」。三种情况各有唯一结论：
 * - `Trusted` 已经通过，没有要问用户的；
 * - `Failed` 该显示失败原因，**不是**指纹对话框——把认证失败也弹成指纹核对，等于要求用户对一个
 *   与密钥无关的问题做信任决定；
 * - `NeedsTrust`/`KeyChanged` 都要求显式确认，区别只在是否需要并排展示旧指纹。
 */
fun planSshHostKeyReview(verification: ServerCenterSshVerification?): SshHostKeyReview? = when (verification) {
    is ServerCenterSshVerification.NeedsTrust ->
        SshHostKeyReview(verification.observation, null)

    is ServerCenterSshVerification.KeyChanged ->
        SshHostKeyReview(verification.observation, verification.previous)

    is ServerCenterSshVerification.Trusted, is ServerCenterSshVerification.Failed, null -> null
}
