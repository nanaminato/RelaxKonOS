package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SavedCredentialState
import app.relaxkonos.mobile.core.auth.credentialStatus
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.StatusChip
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * 工作区里的「切换主机」选择器。
 *
 * 选一台就有三种结局，且都不需要用户先去别的地方：本会话已验证过的主机静默复核后直接切过去；
 * 有可用保存密码的主机先解封再切；两者都没有的主机**退出工作区**，由服务器中心的表单接手输入
 * 密码——密码只有一个输入位置，不做第二套。
 */
@Composable
fun SshHostSwitcherDialog(currentHostId: String, onDismiss: () -> Unit) {
    val viewModel: ServerCenterViewModel = viewModel()
    val state by viewModel.state.collectAsState()
    val activity = LocalContext.current as? FragmentActivity

    // 任何一次成功打开工作区都意味着切换完成：关掉选择器，让新的主机界面接上来。
    val openRevision = state.workspaceOpenRevision
    val revisionOnOpen = remember { mutableIntStateOf(openRevision) }
    LaunchedEffect(openRevision) {
        if (openRevision != revisionOnOpen.intValue) onDismiss()
    }
    // 解封与握手期间留着选择器：它此时是唯一能显示进度的地方，也避免用户重复点。
    val switching = state.isVerifying || state.quickManaging

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.server_center_switch_host_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(
                    stringResource(R.string.server_center_switch_host_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                // 解封被拒、握手失败或「已保存但没保存上」都要在这里说出来，不能让对话框空转。
                state.message?.let { message ->
                    ActionFeedback(
                        message = message.text(),
                        onRetry = null,
                        onDismiss = viewModel::dismissMessage,
                        tone = message.tone,
                    )
                }
                if (switching) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                    ) {
                        CircularProgressIndicator(Modifier.size(18.dp))
                        Text(
                            stringResource(R.string.server_center_switching_host),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    state.hosts.forEach { host ->
                        val credential = state.credentialStates[host.hostId] ?: SavedCredentialState.Absent
                        val selectable = host.hostId != currentHostId && activity != null && !switching
                        ListRow(
                            title = host.displayName,
                            subtitle = "${host.sshUserName}@${host.sshHost}:${host.sshPort}",
                            supporting = stringResource(credentialStatusLabel(credentialStatus(credential, state.unlockMode))),
                            leading = { IconBadge(DesktopIcons.host, contentDescription = null) },
                            trailing = {
                                if (host.hostId == currentHostId) {
                                    StatusChip(
                                        text = stringResource(R.string.server_center_current_host),
                                        tone = StatusTone.Primary,
                                    )
                                } else if (selectable) {
                                    TextButton(onClick = { viewModel.switchWorkspaceHost(host.hostId, activity!!) }) {
                                        Text(stringResource(R.string.server_center_connect))
                                    }
                                }
                            },
                            selected = host.hostId == currentHostId,
                            // 当前主机的整行不做事：它在列表里存在只是为了说明「你正在这里」。
                            onClick = if (selectable) {
                                { viewModel.switchWorkspaceHost(host.hostId, activity!!) }
                            } else {
                                null
                            },
                        )
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
        },
    )
}
