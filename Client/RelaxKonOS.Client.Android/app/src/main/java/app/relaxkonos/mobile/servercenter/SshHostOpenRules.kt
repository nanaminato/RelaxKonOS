package app.relaxkonos.mobile.servercenter

import app.relaxkonos.mobile.core.auth.CredentialGap
import app.relaxkonos.mobile.core.auth.SavedCredentialState

/**
 * 用户点开一台受管主机时，这一次点击能做什么。
 *
 * 与登录页的 `LoginDecision` 同一形态：动作只有一个，界面按结论决定是「直接连」还是
 * 「先要密码」。SSH 与登录的差别在于判定顺序：登录必须让服务端评判密码对错，所以表单里
 * 输入的密码优先于保存的密码；而点开一台主机本身就是「我要连它」的明确意图，因此只要
 * 保存的凭据可用就直接用它，不再让用户重敲一遍。
 */
enum class SshHostOpenAction {
    /** 有可解封的保存密码：先解封（指纹或锁屏），再用它握手。 */
    ConnectWithSavedPassword,

    /** 没有可用凭据：必须先输入密码，原因见 [SshHostOpenPlan.gap]。 */
    AskForPassword,
}

/** 一次点击的结论。[gap] 只在 [SshHostOpenAction.AskForPassword] 时非空。 */
data class SshHostOpenPlan(val action: SshHostOpenAction, val gap: CredentialGap?)

/**
 * 由「该主机保存凭据的状态」得出这一次点击的动作。
 *
 * 四种状态各有唯一结论，且三种「要输入密码」的原因必须分开表达：未保存、本机当前解不开、
 * 密钥永久失效对应的用户动作完全不同（直接输入 / 稍后重试或换个解封方式 / 输入后重新保存）。
 * 合并成一句「请重新输入密码」会把第三种说成第二种，正是 `LoginCredentials.Design.md` §4.1
 * 禁止的。
 */
fun planSshHostOpen(credential: SavedCredentialState): SshHostOpenPlan = when (credential) {
    SavedCredentialState.Available ->
        SshHostOpenPlan(SshHostOpenAction.ConnectWithSavedPassword, null)

    SavedCredentialState.Unavailable ->
        SshHostOpenPlan(SshHostOpenAction.AskForPassword, CredentialGap.Unavailable)

    SavedCredentialState.Invalidated ->
        SshHostOpenPlan(SshHostOpenAction.AskForPassword, CredentialGap.Invalidated)

    SavedCredentialState.Absent ->
        SshHostOpenPlan(SshHostOpenAction.AskForPassword, CredentialGap.Absent)
}

/**
 * 这次握手用的密码是从哪儿来的。
 *
 * 三个来源只在一件事上有区别：**还要不要写保险箱**。
 *
 * - [Typed]：用户刚在本机输入框里敲进去的。这是唯一值得问一次「要不要保存」的来源。
 * - [UnsealedFromVault]：刚从保险箱解封出来的。它本来就保存着，再问一次等于让同一次指纹授权
 *   做两遍——解封一遍、加密一遍；而且用户刚刚才用指纹证明过「是我」，紧接着再弹一次一模一样的
 *   指纹框，只会让人怀疑上一次是不是没生效。
 * - [SessionMemory]：本会话内存里上一次验证通过的副本。它存在的前提就是那次验证已经问过一次
 *   是否保存，之后每次静默复核都再问一遍就是骚扰。
 */
enum class SshPasswordOrigin { Typed, UnsealedFromVault, SessionMemory }

/**
 * 一次成功握手之后是否把这台主机的 SSH 密码写入设备保险箱。
 *
 * 三个条件缺一不可：密码是用户**本次输入**的（见 [SshPasswordOrigin]）、用户勾选了保存、且本机
 * 能保护保存的密码（有指纹，或退一步有锁屏）。本机没有可用的保护方式时宁可不保存，也不落到
 * 明文——SSH 凭据没有 debug 明文兜底（`ServerCenter.md` §3）。
 *
 * 未勾选**不会**删除已有记录：那是「忘记已保存密码」这个显式动作的职责，与登录页
 * `LoginCredentials.Design.md` §5.1 第 3 行、§6.3 一致。
 */
fun shouldSaveSshPassword(
    rememberRequested: Boolean,
    canProtectOnThisDevice: Boolean,
    origin: SshPasswordOrigin,
): Boolean = rememberRequested && canProtectOnThisDevice && origin == SshPasswordOrigin.Typed
