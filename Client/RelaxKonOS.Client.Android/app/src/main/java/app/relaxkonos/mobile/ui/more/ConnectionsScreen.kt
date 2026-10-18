package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import app.relaxkonos.mobile.core.auth.SessionState
import kotlinx.coroutines.CancellationException
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.SectionLabel
import app.relaxkonos.mobile.ui.common.RecordActionRow
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.connect.credentialStatusLabel
import app.relaxkonos.mobile.ui.icons.ServerPlatformBadge
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Connections, inside the shell.
 *
 * The same content as the sign-in list, with one difference that only makes sense while signed in:
 * the actions also affect the stored credential. They stay two separate actions, per row
 * (`LoginCredentials.Design.md` §6.3): forgetting a password keeps the login, deleting
 * a login removes both.
 *
 * Switching explicitly ends the current session and continues through the ordinary sign-in flow.
 */
@Composable
fun ConnectionsScreen(
    onBack: (() -> Unit)?,
    onSwitchLogin: (SavedLogin) -> Unit,
    active: Boolean,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val controller = remember(container) { ConnectionsController(container) }
    ConnectionsContent(controller, container.activeSession, onBack, onSwitchLogin, container.serverCenter::open, active, modifier)
}

@Composable
internal fun ConnectionsContent(controller: ConnectionsActions, session: SessionState.Active?, onBack: (() -> Unit)?,
    onSwitchLogin: (SavedLogin) -> Unit, onOpenServerCenter: () -> Unit, active: Boolean, modifier: Modifier = Modifier) {
    val currentActive by rememberUpdatedState(active)
    val currentController by rememberUpdatedState(controller)
    val currentSession by rememberUpdatedState(session)
    var attached by remember { mutableStateOf(false) }
    var confirmationToken by remember { mutableStateOf<Any?>(null) }
    var revision by remember { mutableStateOf(0) }
    val lifecycleState by LocalLifecycleOwner.current.lifecycle.currentStateAsState()
    val resumed = lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    var forgetTarget by remember(active, resumed, session) { mutableStateOf<SavedLogin?>(null) }
    var deleteTarget by remember(active, resumed, session) { mutableStateOf<SavedLogin?>(null) }
    var mutationFailed by remember(forgetTarget, deleteTarget) { mutableStateOf(false) }
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(controller, lifecycleOwner, active, resumed, session) {
        attached = true
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) {
                confirmationToken = null
                forgetTarget = null
                deleteTarget = null
                mutationFailed = false
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose {
            attached = false
            confirmationToken = null
            lifecycleOwner.lifecycle.removeObserver(observer)
        }
    }
    fun canMutate() = attached && currentActive && lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) &&
        currentController === controller && currentSession === session && controller.isCurrentSession(session)
    fun dismissConfirmation() {
        confirmationToken = null
        forgetTarget = null
        deleteTarget = null
        mutationFailed = false
    }
    fun openConfirmation(open: () -> Unit) {
        if (!canMutate()) return
        dismissConfirmation()
        confirmationToken = Any()
        open()
    }
    fun mutate(action: () -> Unit, completed: () -> Unit) {
        mutationFailed = false
        try { action(); completed() }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { mutationFailed = true; revision++ }
    }

    fun confirm(token: Any?, action: () -> Unit, completed: () -> Unit) {
        if (token == null || confirmationToken !== token) return
        if (!canMutate()) { confirmationToken = null; return }
        confirmationToken = null
        mutate(action, completed)
        if (forgetTarget != null || deleteTarget != null) confirmationToken = Any()
    }

    val logins = remember(controller, revision, active, resumed, session) { controller.logins }
    LaunchedEffect(controller, active, resumed) { if (active && resumed) controller.resolvePlatforms() }

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.connections_title),
            onBack = onBack,
        )

        // The page header already names this list, so the group carries no title of its own.
        SectionGroup {
            if (logins.isEmpty()) {
                EmptyHint(stringResource(R.string.connections_empty))
            } else {
                logins.forEach { login ->
                    RecordActionRow(
                        leading = { ServerPlatformBadge(login.serviceId) },
                        actions = {
                            Column {
                                TextButton(onClick = { if (canMutate()) { dismissConfirmation(); onSwitchLogin(login) } }) {
                                    Text(stringResource(R.string.more_switch_login))
                                }
                                TextButton(onClick = { openConfirmation { forgetTarget = login } }) {
                                    Text(stringResource(R.string.connections_forget_password))
                                }
                                TextButton(onClick = { openConfirmation { deleteTarget = login } }) {
                                    ActionLabel(R.string.common_delete)
                                }
                            }
                        },
                    ) {
                        Text(login.displayName ?: login.serviceId, style = MaterialTheme.typography.bodyLarge)
                        if (login.displayName != null) {
                            Text(login.serviceId, style = MaterialTheme.typography.bodySmall)
                        }
                        Text(
                            listOfNotNull(login.identifier, formatTimestamp(login.lastUsedEpochMillis)).joinToString(" · "),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        Text(
                            credentialStatusLabel(controller.status(login)),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        if (login.serviceId == session?.serviceId && login.identifier == session.userName) {
                            Text(
                                stringResource(R.string.connections_active),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.primary,
                            )
                        }
                    }
                }
            }
        }

        // Host lifecycle is intentionally adjacent to saved logins, but remains a separate record
        // type: removing a login above never removes this device's SSH host information.
        SectionLabel(stringResource(R.string.server_center_title))
        SectionGroup {
            Text(
                stringResource(R.string.server_center_connections_hint),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.bodyMedium,
            )
            OutlinedButton(onClick = { if (canMutate()) { dismissConfirmation(); onOpenServerCenter() } }, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.server_center_open))
            }
        }

        Text(
            stringResource(R.string.connections_sign_out_note),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }

    forgetTarget?.let { login ->
        val token = confirmationToken
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_forget_title),
            message = stringResource(R.string.connections_forget_message, login.serviceId, login.identifier),
            confirmLabel = stringResource(R.string.connections_forget_password),
            extraContent = { if (mutationFailed) Text(stringResource(R.string.connections_mutation_failed), color = MaterialTheme.colorScheme.error) },
            onConfirm = {
                confirm(token, { controller.forgetPassword(login) }) { forgetTarget = null; revision++ }
            },
            onDismiss = ::dismissConfirmation,
        )
    }

    deleteTarget?.let { login ->
        val token = confirmationToken
        ConfirmDangerousDialog(
            title = stringResource(R.string.connections_delete_title),
            message = stringResource(R.string.connections_delete_message, login.serviceId, login.identifier),
            confirmLabel = stringResource(R.string.common_delete),
            extraContent = { if (mutationFailed) Text(stringResource(R.string.connections_mutation_failed), color = MaterialTheme.colorScheme.error) },
            onConfirm = {
                confirm(token, { controller.delete(login) }) { deleteTarget = null; revision++ }
            },
            onDismiss = ::dismissConfirmation,
        )
    }
}
