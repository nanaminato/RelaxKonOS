package app.relaxkonos.mobile.ui.servercenter

import android.text.format.DateFormat
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.core.auth.SavedCredentialState
import app.relaxkonos.mobile.core.auth.credentialStatus
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.servercenter.ServerCenterSshVerification
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.servercenter.ServerHostTrustRules
import app.relaxkonos.mobile.servercenter.SshFailureReason
import app.relaxkonos.mobile.servercenter.planSshHostKeyReview
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing
import java.util.Date

/**
 * 服务器中心：本机信任的 SSH 主机一览，加上一个「添加主机」表单。
 *
 * 层次按用户实际动作排序——**先选主机，再添主机**：列表里点一台就连接（有保存密码时先解封），
 * 卡片上的「管理」只打开表单。新增与管理共用同一个表单，按 `formMode` 决定字段是否可编辑，
 * 免得两套字段各自漂移。
 */
@Composable
fun ServerCenterScreen(onClose: () -> Unit) {
    val viewModel: ServerCenterViewModel = viewModel()
    val state by viewModel.state.collectAsState()
    // 保存与解封都需要一个 Activity 来承载指纹对话框；没有它就不能假装任务可以完成。
    val activity = LocalContext.current as? FragmentActivity ?: return

    ServerCenterContent(
        state = state,
        onHostChanged = viewModel::updateHost,
        onPortChanged = viewModel::updatePort,
        onUserChanged = viewModel::updateUser,
        onNameChanged = viewModel::updateName,
        onPasswordChanged = viewModel::updatePassword,
        onRememberPasswordChanged = viewModel::setRememberPassword,
        onOpenHost = { viewModel.openHost(it, activity) },
        onManageHost = viewModel::manage,
        onAddAndVerify = { viewModel.addAndVerify(activity) },
        onVerifyAndOpen = { viewModel.verifyAndOpen(activity) },
        onLeaveManage = viewModel::leaveManageForm,
        onRequestDelete = viewModel::requestDelete,
        onRequestForgetPassword = viewModel::requestForgetPassword,
        onDismissMessage = viewModel::dismissMessage,
        onClose = onClose,
    )

    val selected = state.hosts.firstOrNull { it.hostId == state.selectedHostId }
    if (state.deleteRequested && selected != null) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.server_center_remove_host),
            message = stringResource(R.string.server_center_remove_host_message, selected.displayName),
            confirmLabel = stringResource(R.string.server_center_remove_host),
            onConfirm = viewModel::removeSelectedHost,
            onDismiss = viewModel::dismissDelete,
        )
    }
    if (state.forgetPasswordRequested && selected != null) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.server_center_forget_password),
            message = stringResource(R.string.server_center_forget_password_message, selected.displayName),
            confirmLabel = stringResource(R.string.server_center_forget_password),
            onConfirm = viewModel::forgetSavedPassword,
            onDismiss = viewModel::dismissForgetPassword,
        )
    }
    // 首次固定与替换已固定的密钥共用一次核对：两者都必须由用户显式确认，区别只在是否需要
    // 并排展示被取代的旧指纹。密钥变更曾经只是一行红字、没有任何出口——在 DHCP 地址漂移或
    // 克隆/重装的主机上这属于常态，用户必须有办法接受新指纹并继续（SshHostKeyReviewRules）。
    val hostKeyReview = planSshHostKeyReview(state.verification)
    if (hostKeyReview != null) {
        val previous = hostKeyReview.previous
        ConfirmDangerousDialog(
            title = stringResource(
                if (previous != null) R.string.server_center_host_key_replace_title
                else R.string.server_center_host_key_confirm_title,
            ),
            message = if (previous == null) {
                stringResource(R.string.server_center_host_key_review, hostKeyReview.observation.groupedFingerprint)
            } else {
                stringResource(
                    R.string.server_center_host_key_replace_message,
                    ServerHostTrustRules.groupedFingerprint(previous.fingerprint),
                    DateFormat.getDateFormat(activity).format(Date(previous.confirmedAtEpochMillis)),
                    hostKeyReview.observation.groupedFingerprint,
                )
            },
            confirmLabel = stringResource(
                if (previous != null) R.string.server_center_host_key_replace_confirm
                else R.string.server_center_trust_and_verify,
            ),
            onConfirm = { viewModel.confirmHostKey(activity) },
            onDismiss = viewModel::dismissHostKeyReview,
            busy = state.isVerifying,
        )
    }
}

@Composable
private fun ServerCenterContent(
    state: ServerCenterUiState,
    onHostChanged: (String) -> Unit,
    onPortChanged: (String) -> Unit,
    onUserChanged: (String) -> Unit,
    onNameChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onRememberPasswordChanged: (Boolean) -> Unit,
    onOpenHost: (String) -> Unit,
    onManageHost: (String) -> Unit,
    onAddAndVerify: () -> Unit,
    onVerifyAndOpen: () -> Unit,
    onLeaveManage: () -> Unit,
    onRequestDelete: () -> Unit,
    onRequestForgetPassword: () -> Unit,
    onDismissMessage: () -> Unit,
    onClose: () -> Unit,
) {
    Column(
        Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.server_center_title),
            subtitle = stringResource(R.string.server_center_subtitle),
            onBack = onClose,
        )
        state.message?.let { message ->
            ActionFeedback(
                message = message.text(),
                onRetry = null,
                onDismiss = onDismissMessage,
                tone = message.tone,
            )
        }
        ManagedHosts(state, onOpenHost = onOpenHost, onManageHost = onManageHost)
        if (state.quickManaging) {
            // 「静默复核」期间表单让位：此刻没有需要用户填的东西，只有一件正在发生的事。
            CircularProgressIndicator()
        } else {
            HostForm(
                state = state,
                onHostChanged = onHostChanged,
                onPortChanged = onPortChanged,
                onUserChanged = onUserChanged,
                onNameChanged = onNameChanged,
                onPasswordChanged = onPasswordChanged,
                onRememberPasswordChanged = onRememberPasswordChanged,
                onSubmit = if (state.formMode == ServerCenterFormMode.Manage) onVerifyAndOpen else onAddAndVerify,
                onLeaveManage = onLeaveManage,
                onRequestDelete = onRequestDelete,
                onRequestForgetPassword = onRequestForgetPassword,
            )
        }
        VerificationNotice(state.verification)
    }
}

/** 已管理主机：整行可点即「连接」，行内的「管理」只打开表单。 */
@Composable
private fun ManagedHosts(
    state: ServerCenterUiState,
    onOpenHost: (String) -> Unit,
    onManageHost: (String) -> Unit,
) {
    SectionCard(
        title = stringResource(R.string.server_center_managed_hosts),
        subtitle = stringResource(R.string.server_center_managed_hosts_hint),
        leading = DesktopIcons.host,
    ) {
        if (state.hosts.isEmpty()) {
            Text(stringResource(R.string.server_center_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
            return@SectionCard
        }
        state.hosts.forEach { host ->
            val credential = state.credentialStates[host.hostId] ?: SavedCredentialState.Absent
            ListRow(
                title = host.displayName,
                subtitle = stringResource(
                    R.string.server_center_host_summary,
                    host.sshUserName, host.sshHost, host.sshPort, hostStatus(host),
                ),
                supporting = stringResource(credentialStatusLabel(credentialStatus(credential, state.unlockMode))),
                leading = { IconBadge(DesktopIcons.host, contentDescription = null) },
                trailing = {
                    TextButton(onClick = { onManageHost(host.hostId) }) {
                        Text(stringResource(R.string.server_center_manage_host))
                    }
                },
                selected = host.hostId == state.selectedHostId,
                onClick = { onOpenHost(host.hostId) },
            )
        }
    }
}

@Composable
private fun HostForm(
    state: ServerCenterUiState,
    onHostChanged: (String) -> Unit,
    onPortChanged: (String) -> Unit,
    onUserChanged: (String) -> Unit,
    onNameChanged: (String) -> Unit,
    onPasswordChanged: (String) -> Unit,
    onRememberPasswordChanged: (Boolean) -> Unit,
    onSubmit: () -> Unit,
    onLeaveManage: () -> Unit,
    onRequestDelete: () -> Unit,
    onRequestForgetPassword: () -> Unit,
) {
    val managing = state.formMode == ServerCenterFormMode.Manage
    val credential = state.selectedHostId?.let { state.credentialStates[it] } ?: SavedCredentialState.Absent
    val status = credentialStatus(credential, state.unlockMode)
    val savedPasswordUsable = managing &&
        (status == CredentialStatus.SavedByFingerprint || status == CredentialStatus.SavedByScreenLock)
    // 密码框留空只在「保存的密码可用」时才合法：那正是状态行说的「留空即可解封」（§6.2）。
    val maySubmit = !state.isVerifying &&
        (state.password.isNotEmpty() || savedPasswordUsable) &&
        (!managing || state.selectedHostId != null)

    SectionCard(
        title = if (managing) {
            stringResource(R.string.server_center_manage_host_named, state.name)
        } else {
            stringResource(R.string.server_center_add_host)
        },
        subtitle = if (managing) null else stringResource(R.string.server_center_add_host_hint),
        leading = DesktopIcons.credentials,
    ) {
        OutlinedTextField(
            value = state.host,
            onValueChange = onHostChanged,
            modifier = Modifier.fillMaxWidth(),
            label = { Text(stringResource(R.string.server_center_ssh_host)) },
            isError = state.inputError,
            singleLine = true,
            enabled = !managing && !state.isVerifying,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
            OutlinedTextField(
                value = state.port,
                onValueChange = onPortChanged,
                modifier = Modifier.weight(1f),
                label = { Text(stringResource(R.string.server_center_ssh_port)) },
                isError = state.inputError,
                singleLine = true,
                enabled = !managing && !state.isVerifying,
            )
            OutlinedTextField(
                value = state.user,
                onValueChange = onUserChanged,
                modifier = Modifier.weight(1f),
                label = { Text(stringResource(R.string.server_center_ssh_user)) },
                isError = state.inputError,
                singleLine = true,
                enabled = !managing && !state.isVerifying,
            )
        }
        if (!managing) {
            OutlinedTextField(
                value = state.name,
                onValueChange = onNameChanged,
                modifier = Modifier.fillMaxWidth(),
                label = { Text(stringResource(R.string.server_center_host_name_optional)) },
                singleLine = true,
                enabled = !state.isVerifying,
            )
        }
        PasswordTextField(
            state.password,
            onPasswordChanged,
            stringResource(R.string.server_center_ssh_password),
            enabled = !state.isVerifying,
            supportingText = credentialStatusLine(status),
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(
                checked = state.rememberPassword,
                onCheckedChange = onRememberPasswordChanged,
                // 勾选跟着「本机能不能保护它」走，绝不比它更宽松：SSH 凭据没有 debug 明文兜底。
                enabled = !state.isVerifying && state.unlockMode != null,
            )
            Text(stringResource(R.string.server_center_remember_password))
        }
        if (state.unlockMode == null) {
            Text(
                stringResource(R.string.server_center_no_lock_screen),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        } else if (state.unlockMode == VaultUnlockMode.DeviceUnlockWindow) {
            Text(
                stringResource(R.string.vault_device_window_notice),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        if (state.inputError) {
            Text(
                stringResource(
                    if (managing) R.string.server_center_invalid_password else R.string.server_center_invalid_host,
                ),
                color = MaterialTheme.colorScheme.error,
            )
        }
        Row(
            horizontalArrangement = Arrangement.spacedBy(Spacing.md),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Button(onSubmit, enabled = maySubmit, modifier = Modifier.weight(1f)) {
                if (state.isVerifying) {
                    CircularProgressIndicator()
                } else {
                    Text(
                        stringResource(
                            if (managing) R.string.server_center_verify_and_open else R.string.server_center_add_and_verify,
                        ),
                    )
                }
            }
            if (managing) {
                OutlinedButton(onClick = onLeaveManage, enabled = !state.isVerifying) {
                    Text(stringResource(R.string.server_center_close_form))
                }
            }
        }
        if (managing && state.selectedHostId != null) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                if (credential != SavedCredentialState.Absent) {
                    TextButton(onClick = onRequestForgetPassword, enabled = !state.isVerifying) {
                        Text(stringResource(R.string.server_center_forget_password))
                    }
                }
                TextButton(onClick = onRequestDelete, enabled = !state.isVerifying) {
                    Text(stringResource(R.string.server_center_remove_host), color = MaterialTheme.colorScheme.error)
                }
            }
        }
    }
}

/** 保存凭据的状态行：文案随状态变，颜色只在「解不开」时才转红（§6.2）。 */
@Composable
private fun credentialStatusLine(status: CredentialStatus): (@Composable () -> Unit)? {
    if (status == CredentialStatus.None) return null
    val color: Color = when (status) {
        CredentialStatus.Unavailable, CredentialStatus.Invalidated -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    return {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.xs + 2.dp),
        ) {
            DesktopIcon(icon = DesktopIcons.credentials, size = 16.dp)
            Text(
                stringResource(credentialStatusLabel(status)),
                style = MaterialTheme.typography.bodySmall,
                color = color,
            )
        }
    }
}

@Composable
private fun VerificationNotice(verification: ServerCenterSshVerification?) = when (verification) {
    is ServerCenterSshVerification.Trusted -> Text(
        stringResource(R.string.server_center_ssh_verified, verification.fingerprint.orEmpty()),
        color = MaterialTheme.colorScheme.primary,
    )

    is ServerCenterSshVerification.NeedsTrust -> Unit

    // 对话框被关掉之后留下的结论。它必须说清「还能做什么」：密钥变更在 DHCP 地址漂移或重装/
    // 克隆出来的主机上是常态，不是终点——再点一次这台主机就会重新弹出核对对话框。
    is ServerCenterSshVerification.KeyChanged -> Text(
        stringResource(R.string.server_center_host_key_changed, verification.observation.groupedFingerprint),
        color = MaterialTheme.colorScheme.error,
    )

    is ServerCenterSshVerification.Failed -> Text(
        stringResource(sshFailureMessage(verification.reason)),
        color = MaterialTheme.colorScheme.error,
    )

    null -> Unit
}

@Composable
private fun hostStatus(target: ServerHostTarget): String = when {
    target.sshVerifiedAtEpochMillis != null -> stringResource(R.string.server_center_status_ssh_verified)
    target.lastVerified == null -> stringResource(R.string.server_center_status_unverified)
    target.lastVerified.installed && target.lastVerified.healthy -> stringResource(R.string.server_center_status_healthy_cached)
    target.lastVerified.installed -> stringResource(R.string.server_center_status_unhealthy_cached)
    else -> stringResource(R.string.server_center_status_not_installed_cached)
}

/**
 * 保存凭据的一句话摘要，列表、状态行与切换器共用同一份映射：几处说法不可能不一致。
 *
 * `SavedInDebugBuild` 在 SSH 上不可达——SSH 凭据没有明文兜底（`ServerCenter.md` §3）——但映射
 * 仍给出「已保存」的措辞而不是省略，免得将来有人把兜底接上来时这里静默少一句话。
 */
internal fun credentialStatusLabel(status: CredentialStatus): Int = when (status) {
    CredentialStatus.None -> R.string.server_center_credential_none
    CredentialStatus.SavedByFingerprint -> R.string.server_center_credential_fingerprint
    CredentialStatus.SavedByScreenLock -> R.string.server_center_credential_screen_lock
    CredentialStatus.SavedInDebugBuild -> R.string.server_center_credential_fingerprint
    CredentialStatus.Unavailable -> R.string.server_center_credential_unavailable
    CredentialStatus.Invalidated -> R.string.server_center_credential_invalidated
}

/**
 * 握手失败的文案：按原因给出一句可执行的下一步，而不是让用户同时猜主机、账号、密码和网络四件事。
 *
 * 映射是穷尽的——新增一个 [SshFailureReason] 就会在这里编译失败，不会静默退回一句通用话。
 * 归为通用句的只有「无法归因」两种情况，那时通用句本来就是最诚实的话。
 */
internal fun sshFailureMessage(reason: SshFailureReason): Int = when (reason) {
    SshFailureReason.AuthenticationRejected -> R.string.server_center_ssh_failed_authentication
    SshFailureReason.AuthenticationCancelled -> R.string.server_center_ssh_failed_authentication_cancelled
    SshFailureReason.TimedOut -> R.string.server_center_ssh_failed_timeout
    SshFailureReason.ConnectionRefused -> R.string.server_center_ssh_failed_unreachable
    SshFailureReason.NameNotResolved -> R.string.server_center_ssh_failed_name
    SshFailureReason.AlgorithmNegotiation -> R.string.server_center_ssh_failed_algorithm
    SshFailureReason.HostKeyRejected -> R.string.server_center_ssh_failed_host_key
    SshFailureReason.HandshakeFailed, SshFailureReason.Unexpected -> R.string.server_center_ssh_failed
}
