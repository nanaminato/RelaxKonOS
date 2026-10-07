package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.CredentialGap
import app.relaxkonos.mobile.core.auth.SavedCredentialState
import app.relaxkonos.mobile.core.auth.credentialState
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.servercenter.ServerCenterCoordinator
import app.relaxkonos.mobile.servercenter.ServerCenterSshVerification
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.servercenter.ServerHostTargetRules
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.servercenter.SshHostOpenAction
import app.relaxkonos.mobile.servercenter.SshPasswordOrigin
import app.relaxkonos.mobile.servercenter.SshFailureReason
import app.relaxkonos.mobile.servercenter.planSshHostKeyReview
import app.relaxkonos.mobile.servercenter.planSshHostOpen
import app.relaxkonos.mobile.servercenter.shouldSaveSshPassword
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.unlockFailureMessage
import app.relaxkonos.mobile.ui.common.withReminder
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * 服务器中心的全部表单与连接状态。
 *
 * 两件凭据的去向在这里被明确分开：
 * - **密码输入框**任何时刻只放本次输入，验证一开始就被清空（`LoginCredentials.Design.md` §6.2）；
 * - **「已保存密码」**是保险箱记录，通过 `CredentialStatus` 表达在输入框之外；勾选框默认开，
 *   但只有一次成功握手之后、且本机能把**用户本次输入**的密码保护起来时才真的写入。
 *
 * 「点开主机」只有一条路径：有可用保存凭据（或本会话已验证过）就直接连，否则把表单交回给
 * 用户并说明原因。判定本身在 `planSshHostOpen` 里，界面不自己发明结论。
 */
class ServerCenterViewModel(application: Application) : AndroidViewModel(application) {
    private val app = getApplication<RelaxKonApplication>()
    private val container = app.container
    private val coordinator: ServerCenterCoordinator = container.serverCenter

    private val mutableState = MutableStateFlow(ServerCenterUiState(hosts = coordinator.hosts()).projected())
    val state = mutableState.asStateFlow()

    // ---- Form editing ----------------------------------------------------------------------

    fun updateHost(value: String) = update { copy(host = value, inputError = false, verification = null) }
    fun updatePort(value: String) = update { copy(port = value, inputError = false, verification = null) }
    fun updateUser(value: String) = update { copy(user = value, inputError = false, verification = null) }
    fun updateName(value: String) = update { copy(name = value, verification = null) }
    fun updatePassword(value: String) = update { copy(password = value, inputError = false, verification = null) }

    /** The explicit opt-in for saving this host's password. Off never deletes an existing record. */
    fun setRememberPassword(value: Boolean) = update { copy(rememberPassword = value) }

    fun dismissMessage() = update { copy(message = null) }

    // ---- Connecting ------------------------------------------------------------------------

    /**
     * 添加一台新主机：校验输入后立刻握手；失败的主机不会留下任何管理记录。
     */
    fun addAndVerify(activity: FragmentActivity) {
        if (mutableState.value.isVerifying) return
        val state = mutableState.value
        val port = state.port.toIntOrNull()
        if (port == null || !ServerHostTargetRules.isValidEndpoint(state.host, port, state.user) || state.password.isEmpty()) {
            update { copy(inputError = true) }
            return
        }
        val target = ServerHostTargetRules.create(state.host, port, state.user, state.name.ifBlank { null }, System.currentTimeMillis())
        beginVerification(
            target = target,
            password = state.password,
            origin = SshPasswordOrigin.Typed,
            openWorkspace = false,
            clearFormOnSuccess = true,
            activity = activity,
        )
    }

    /**
     * 点开列表里的一台主机：本会话已验证过就静默复核，有可用保存密码就解封后连接，否则回到
     * 表单并要求输入密码。这是列表卡片与工作区「切换主机」共用的唯一入口。
     */
    fun openHost(hostId: String, activity: FragmentActivity) {
        if (mutableState.value.isVerifying) return
        val target = mutableState.value.hosts.firstOrNull { it.hostId == hostId } ?: return
        selectForManage(target)
        openWithBestCredential(target, activity)
    }

    /** 主机的次级动作：只打开管理表单，不发起连接。 */
    fun manage(hostId: String) {
        if (mutableState.value.isVerifying) return
        val target = mutableState.value.hosts.firstOrNull { it.hostId == hostId } ?: return
        selectForManage(target)
        update { copy(password = "", quickManaging = false, message = null, inputError = false) }
    }

    /**
     * 工作区里的「切换主机」。
     *
     * 返回 `false` 表示这台主机必须先输入密码：此时工作区会被关掉，由服务器中心的表单接手——
     * 密码只有一个输入位置，不做第二套。
     */
    fun switchWorkspaceHost(hostId: String, activity: FragmentActivity): Boolean {
        if (mutableState.value.hosts.none { it.hostId == hostId }) return false
        val connectsWithoutTyping = coordinator.hasSessionPassword(hostId) ||
            planSshHostOpen(mutableState.value.credentialStates[hostId] ?: SavedCredentialState.Absent).action ==
            SshHostOpenAction.ConnectWithSavedPassword
        openHost(hostId, activity)
        if (!connectsWithoutTyping) coordinator.closeSshFiles()
        return connectsWithoutTyping
    }

    /** 管理表单的主按钮。密码留空即等于「用保存的密码」，与登录页的状态行是同一套约定。 */
    fun verifyAndOpen(activity: FragmentActivity) {
        if (mutableState.value.isVerifying) return
        val target = selectedTarget() ?: return
        val typed = mutableState.value.password
        if (typed.isNotEmpty()) {
            beginVerification(
                target = target,
                password = typed,
                origin = SshPasswordOrigin.Typed,
                openWorkspace = true,
                clearFormOnSuccess = false,
                activity = activity,
            )
            return
        }
        openWithBestCredential(target, activity)
    }

    /**
     * 用户核对完指纹后的唯一确认动作：首次固定与替换已固定的密钥共用。
     *
     * 确认即写入固定（同端点同算法只留一条，旧记录被取代），随后用同一份密码立刻重新握手——
     * 用户点一次就真的连上，而不是回到列表再点一次。复核的还是刚才那份密码，所以它的来源也照旧：
     * 新主机首次固定之后仍要问一次保存，从保险箱解封来的则不再问（见 [SshPasswordOrigin]）。
     */
    fun confirmHostKey(activity: FragmentActivity) {
        if (mutableState.value.isVerifying) return
        val state = mutableState.value
        val target = state.pendingTarget ?: return
        val review = planSshHostKeyReview(state.verification) ?: return
        val password = state.pendingPassword
        if (password.isEmpty()) {
            update { copy(verification = null, pendingTarget = null) }
            return
        }
        coordinator.trustHostKey(target, review.observation)
        val managing = state.formMode == ServerCenterFormMode.Manage
        beginVerification(
            target = target,
            password = password,
            // 来源与密码在同一处写入、同一处清除，因此这里取不到只可能是程序错误；真取不到时宁可
            // 多问一次保存，也不要静默丢掉一次用户已经勾选过的保存。
            origin = state.pendingPasswordOrigin ?: SshPasswordOrigin.Typed,
            openWorkspace = managing,
            clearFormOnSuccess = !managing,
            activity = activity,
        )
    }

    fun dismissHostKeyReview() = update {
        copy(verification = null, pendingTarget = null, pendingPassword = "", pendingPasswordOrigin = null, quickManaging = false)
    }

    /** 从管理表单退回添加形态；不动任何记录。 */
    fun leaveManageForm() = update {
        copy(
            formMode = ServerCenterFormMode.Add, selectedHostId = null,
            host = "", port = "22", user = "", name = "", password = "",
            pendingTarget = null, pendingPassword = "", pendingPasswordOrigin = null,
            verification = null, message = null, inputError = false,
        )
    }

    // ---- Host and credential maintenance ---------------------------------------------------

    fun requestDelete() = update { copy(deleteRequested = selectedTarget() != null) }
    fun dismissDelete() = update { copy(deleteRequested = false) }

    fun removeSelectedHost() {
        val target = selectedTarget() ?: return
        coordinator.removeHost(target.hostId)
        update {
            copy(
                formMode = ServerCenterFormMode.Add, selectedHostId = null,
                host = "", port = "22", user = "", name = "", password = "",
                pendingTarget = null, pendingPassword = "", pendingPasswordOrigin = null,
                verification = null, deleteRequested = false,
                quickManaging = false, forgetPasswordRequested = false,
            )
        }
        refresh()
    }

    fun requestForgetPassword() = update { copy(forgetPasswordRequested = selectedTarget() != null) }
    fun dismissForgetPassword() = update { copy(forgetPasswordRequested = false) }

    /**
     * 用户显式「忘记已保存密码」。只删凭据：宿主资料、主机指纹和本会话内存里的密码都不动——
     * 用户要撤销的是「本机帮我记住密码」，不是当前这次已建立的连接。
     */
    fun forgetSavedPassword() {
        val target = selectedTarget() ?: return
        coordinator.forgetSavedCredential(target.hostId)
        update { copy(forgetPasswordRequested = false) }
        refresh { copy(message = UiMessage(R.string.server_center_password_forgotten, tone = StatusTone.Success)) }
    }

    // ---- Internals -------------------------------------------------------------------------

    private fun selectForManage(target: ServerHostTarget) = update {
        copy(
            formMode = ServerCenterFormMode.Manage,
            selectedHostId = target.hostId,
            hasSessionPassword = coordinator.hasSessionPassword(target.hostId),
            password = "",
            host = target.sshHost,
            port = target.sshPort.toString(),
            user = target.sshUserName,
            name = target.displayName,
            pendingTarget = null,
            pendingPassword = "",
            pendingPasswordOrigin = null,
            inputError = false,
        )
    }

    /** 会话内存优先，其次设备保险箱；两者都没有就如实说明为什么需要输入密码。 */
    private fun openWithBestCredential(target: ServerHostTarget, activity: FragmentActivity) {
        val remembered = coordinator.verifiedPasswordCopy(target.hostId)
        if (remembered != null) {
            val password = remembered.concatToString()
            remembered.fill('\u0000')
            update { copy(quickManaging = true, message = null, verification = null) }
            beginVerification(
                target = target,
                password = password,
                origin = SshPasswordOrigin.SessionMemory,
                openWorkspace = true,
                clearFormOnSuccess = false,
                activity = activity,
            )
            return
        }
        val plan = planSshHostOpen(mutableState.value.credentialStates[target.hostId] ?: SavedCredentialState.Absent)
        if (plan.action == SshHostOpenAction.ConnectWithSavedPassword) {
            unsealThenVerify(target, activity)
            return
        }
        update {
            copy(
                isVerifying = false, quickManaging = false, password = "",
                message = plan.gap?.let(::passwordGapMessage),
            )
        }
    }

    /** 解封保存的密码，然后握手。取消是静默的：不报错、不改记录（§7.2）。 */
    private fun unsealThenVerify(target: ServerHostTarget, activity: FragmentActivity) {
        update { copy(isVerifying = true, quickManaging = true, message = null, verification = null) }
        viewModelScope.launch {
            val outcome = try {
                coordinator.unsealCredential(
                    target = target,
                    activity = activity,
                    title = text(R.string.server_center_vault_unlock_title),
                    subtitle = text(R.string.server_center_vault_unlock_subtitle, target.displayName),
                    negativeButton = text(R.string.common_cancel),
                )
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (_: Exception) {
                null
            }
            when (outcome) {
                is VaultOperation.Success -> {
                    val password = outcome.value.secret.concatToString()
                    outcome.value.clear()
                    // 刚从保险箱解封出来的密码不再问一次保存：那等于让同一次指纹授权做两遍。
                    beginVerification(
                        target = target,
                        password = password,
                        origin = SshPasswordOrigin.UnsealedFromVault,
                        openWorkspace = true,
                        clearFormOnSuccess = false,
                        activity = activity,
                    )
                }

                VaultOperation.Cancelled -> update { copy(isVerifying = false, quickManaging = false) }

                is VaultOperation.Failed -> refresh {
                    copy(isVerifying = false, quickManaging = false, message = unlockFailureMessage(outcome.failure))
                }

                null -> refresh {
                    copy(isVerifying = false, quickManaging = false, message = UiMessage(R.string.server_center_saved_password_unavailable))
                }
            }
        }
    }

    /**
     * 一次完整的「握手 → 记住 → 可选保存 → 可选打开工作区」。
     *
     * 密码在启动前就从表单状态里清掉，只有这一个协程持有它；需要用户核对主机密钥时（首次固定
     * 或密钥变更）把 target 与密码（连同密码的来源）留到确认之后，否则 `confirmHostKey` 没有东西
     * 可以接着做，确认按钮看起来毫无作用。
     */
    private fun beginVerification(
        target: ServerHostTarget,
        password: String,
        origin: SshPasswordOrigin,
        openWorkspace: Boolean,
        clearFormOnSuccess: Boolean,
        activity: FragmentActivity,
    ) {
        if (password.isEmpty()) {
            update { copy(inputError = true) }
            return
        }
        update {
            copy(
                formMode = if (openWorkspace) ServerCenterFormMode.Manage else ServerCenterFormMode.Add,
                password = "",
                pendingPassword = password,
                pendingPasswordOrigin = origin,
                pendingTarget = target,
                selectedHostId = if (openWorkspace) target.hostId else selectedHostId,
                verification = null,
                inputError = false,
                message = null,
                isVerifying = true,
            )
        }
        viewModelScope.launch {
            val secret = password.toCharArray()
            val result = try {
                coordinator.verifySsh(target, SshCredential(SshCredentialKind.Password, secret, null))
            } catch (cancellation: CancellationException) {
                update {
                    copy(isVerifying = false, quickManaging = false, pendingTarget = null,
                        pendingPassword = "", pendingPasswordOrigin = null)
                }
                throw cancellation
            } finally {
                secret.fill('\u0000')
            }
            val succeeded = result is ServerCenterSshVerification.Trusted
            if (result is ServerCenterSshVerification.Failed &&
                result.reason == SshFailureReason.AuthenticationRejected
            ) {
                coordinator.forgetSessionPassword(target.hostId)
            }
            if (succeeded) {
                val now = System.currentTimeMillis()
                coordinator.saveHost(target.copy(sshVerifiedAtEpochMillis = now, lastUsedAtEpochMillis = now))
                coordinator.rememberVerifiedPassword(target.hostId, password.toCharArray())
            }
            // 指纹首次固定与密钥变更都要留住 target、密码与密码的来源：确认按钮必须真的有东西
            // 可以接着做。只认 NeedsTrust 会让「密钥已变更」变成一句无路可走的红字。
            val awaitingHostKeyReview = planSshHostKeyReview(result) != null
            refresh {
                copy(
                    selectedHostId = if (succeeded && clearFormOnSuccess) target.hostId else selectedHostId,
                    pendingTarget = if (awaitingHostKeyReview) target else null,
                    pendingPassword = if (awaitingHostKeyReview) password else "",
                    pendingPasswordOrigin = if (awaitingHostKeyReview) origin else null,
                    verification = result,
                    isVerifying = succeeded,
                    quickManaging = false,
                    formMode = if (succeeded && clearFormOnSuccess) ServerCenterFormMode.Add else formMode,
                    host = if (succeeded && clearFormOnSuccess) "" else host,
                    port = if (succeeded && clearFormOnSuccess) "22" else port,
                    user = if (succeeded && clearFormOnSuccess) "" else user,
                    name = if (succeeded && clearFormOnSuccess) "" else name,
                )
            }
            if (!succeeded) return@launch
            try {
                // 只有用户本次输入的那份密码才问一次保存：解封得来的本来就保存着，本会话内存里的副本
                // 也是一次已经问过的验证留下的（见 SshPasswordOrigin）。
                if (shouldSaveSshPassword(
                        rememberRequested = mutableState.value.rememberPassword,
                        canProtectOnThisDevice = coordinator.unlockMode() != null,
                        origin = origin,
                    )
                ) {
                    savePassword(target, password, activity)
                }
                if (openWorkspace) {
                    coordinator.openSshFiles(target.hostId, password.toCharArray())
                    update { copy(workspaceOpenRevision = workspaceOpenRevision + 1) }
                }
            } finally {
                refresh { copy(isVerifying = false, quickManaging = false) }
            }
        }
    }

    /**
     * 写入保险箱。这里失败只意味着「没保存」，从不意味着连接失败（§8）——连接此时已经成立，
     * 用户要看到的是这件事，而不是一句看起来像登录被拒的话。
     */
    private suspend fun savePassword(target: ServerHostTarget, password: String, activity: FragmentActivity) {
        val outcome = try {
            coordinator.saveCredential(
                target = target,
                password = password.toCharArray(),
                activity = activity,
                title = text(R.string.server_center_vault_save_title),
                subtitle = text(R.string.server_center_vault_save_subtitle, target.displayName),
                negativeButton = text(R.string.common_cancel),
            )
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (_: Exception) {
            null
        }
        refresh {
            copy(
                message = when (outcome) {
                    // `null` 只有一个来源：本机没有可保护密码的解锁方式（`saveCredential` 的第一个
                    // 提前返回）。因此这是设备结论，可以不再提醒。
                    null -> UiMessage(
                        R.string.server_center_password_not_saved,
                        tone = StatusTone.Warning,
                        reminder = ReminderKind.ServerCenterCredentialNotSaved,
                    )
                    is VaultOperation.Success -> UiMessage(R.string.server_center_password_saved, tone = StatusTone.Success)
                    // 用户自己取消了保存，下次仍可能想保存；这里不给「不再提醒」。
                    VaultOperation.Cancelled -> UiMessage(R.string.server_center_password_not_saved, tone = StatusTone.Warning)
                    is VaultOperation.Failed -> unlockFailureMessage(outcome.failure).withReminder(
                        if (outcome.failure == UnlockFailure.Unavailable) ReminderKind.SavedPasswordUnavailable else null,
                    )
                },
            )
        }
    }

    /** 三种「要输入密码」的原因分开表达，不合并成一句「请重新输入密码」。 */
    private fun passwordGapMessage(gap: CredentialGap): UiMessage = when (gap) {
        CredentialGap.Absent -> UiMessage(R.string.server_center_ssh_password_required)
        CredentialGap.Unavailable -> UiMessage(R.string.server_center_saved_password_unavailable)
        CredentialGap.Invalidated -> UiMessage(R.string.server_center_saved_password_invalidated)
    }

    private fun selectedTarget(): ServerHostTarget? =
        mutableState.value.selectedHostId?.let { id -> mutableState.value.hosts.firstOrNull { it.hostId == id } }

    private fun text(resId: Int, vararg args: Any): String = getApplication<Application>().getString(resId, *args)

    private fun update(transform: ServerCenterUiState.() -> ServerCenterUiState) = mutableState.update(transform)

    /**
     * 应用一次变换，并重算所有「保险箱与设备的投影」。
     *
     * 这些字段永远不能从输入框推导（`LoginCredentials.Design.md` §2）：哪台主机有保存密码、
     * 本机能否解封、能不能保存，都是同一份记录的结论。解锁方式只探测一次，供整份状态共用。
     */
    private fun refresh(transform: ServerCenterUiState.() -> ServerCenterUiState = { this }) {
        val unlockMode = container.unlockMode(VaultKind.Ssh)
        mutableState.update { current -> current.transform().projected(unlockMode) }
    }

    private fun ServerCenterUiState.projected(unlockMode: VaultUnlockMode? = container.unlockMode(VaultKind.Ssh)): ServerCenterUiState {
        val hosts = coordinator.hosts()
        return copy(
            hosts = hosts,
            hasSessionPassword = selectedHostId?.let(coordinator::hasSessionPassword) == true,
            unlockMode = unlockMode,
            credentialStates = hosts.associate { it.hostId to credentialState(coordinator.savedCredential(it.hostId), unlockMode) },
        )
    }
}

enum class ServerCenterFormMode { Add, Manage }

data class ServerCenterUiState(
    val hosts: List<ServerHostTarget>,
    /** 每台主机的保存凭据状态；键缺席即「未保存」。 */
    val credentialStates: Map<String, SavedCredentialState> = emptyMap(),
    /** 本机此刻能怎样解封 `VaultKind.Ssh`；`null` 表示这台设备保护不了保存的密码。 */
    val unlockMode: VaultUnlockMode? = null,
    /** 「在本机保存密码」的勾选，默认开；关掉不会删除已有记录。 */
    val rememberPassword: Boolean = true,
    val formMode: ServerCenterFormMode = ServerCenterFormMode.Add,
    val host: String = "",
    val port: String = "22",
    val user: String = "",
    val name: String = "",
    val password: String = "",
    val inputError: Boolean = false,
    val selectedHostId: String? = null,
    val hasSessionPassword: Boolean = false,
    val pendingTarget: ServerHostTarget? = null,
    val pendingPassword: String = "",
    /**
     * 待复核密码的来源，与 [pendingPassword] 同生同灭。
     *
     * 主机密钥需要用户核对时，确认之后要用同一份密码重试，因此还得知道这份密码该不该再问一次
     * 保存（见 [SshPasswordOrigin]）——否则每次核对完都会多弹一次多余的指纹框。
     */
    val pendingPasswordOrigin: SshPasswordOrigin? = null,
    val verification: ServerCenterSshVerification? = null,
    val isVerifying: Boolean = false,
    /** 会话内存里的凭据正在被静默复核，表单暂时让位给进度指示。 */
    val quickManaging: Boolean = false,
    val deleteRequested: Boolean = false,
    val forgetPasswordRequested: Boolean = false,
    val message: UiMessage? = null,
    /** 每成功打开一次工作区自增；工作区的「切换主机」据此知道已经切过去了。 */
    val workspaceOpenRevision: Int = 0,
) {
    val canSubmit: Boolean
        get() = !isVerifying &&
            (formMode == ServerCenterFormMode.Add && password.isNotEmpty() ||
                formMode == ServerCenterFormMode.Manage && selectedHostId != null &&
                (password.isNotEmpty() || hasSessionPassword ||
                    planSshHostOpen(credentialStates[selectedHostId] ?: SavedCredentialState.Absent).action ==
                    SshHostOpenAction.ConnectWithSavedPassword))
}
