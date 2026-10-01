package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.IconButton
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshTerminalUiState
import app.relaxkonos.mobile.servercenter.SshTerminalSessions
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.terminal.TerminalScreenLayout
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.theme.TerminalType
import app.relaxkonos.mobile.ui.terminal.NativeTerminal

@Composable
fun SshTerminalScreen(hostId: String, onClose: () -> Unit, modifier: Modifier = Modifier) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val target = container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    val model = container.sshTerminals
    val all by model.state.collectAsState()
    LaunchedEffect(hostId) { model.enter(hostId) }
    val state = all.selectedForHost(hostId) ?: SshTerminalUiState(hostId = hostId)
    val sessions = all.sessions.filter { it.hostId == hostId }
    SshTerminalContent(hostId, target?.let { "${it.sshUserName}@${it.sshHost}:${it.sshPort}" },
        state, { model.reconnect(state.sessionId) }, { model.send(state.sessionId, it) },
        { columns, rows -> model.resize(state.sessionId, columns, rows) }, onClose,
        { model.updateDraft(state.sessionId, it) }, { model.concealInput(state.sessionId, it) },
        { model.fontSize(state.sessionId, it) }, sessions, all.sessions.size < SshTerminalSessions.MAX_SESSIONS, model::select,
        { model.create(hostId) }, model::end, modifier, terminalType = container.appearance.terminalType)
}

@Composable
internal fun SshTerminalContent(
    hostId: String,
    hostLabel: String?,
    state: SshTerminalUiState,
    onConnect: () -> Unit,
    onSend: (String) -> Unit,
    onResize: (Int, Int) -> Unit,
    onClose: () -> Unit,
    onDraftChange: (String) -> Unit,
    onConcealChange: (Boolean) -> Unit,
    onFontSizeChange: (Int) -> Unit,
    sessions: List<SshTerminalUiState>,
    canCreate: Boolean,
    onSelect: (String) -> Unit,
    onNew: () -> Unit,
    onEnd: (String) -> Unit,
    modifier: Modifier = Modifier,
    imeInsets: WindowInsets = WindowInsets.ime,
    terminalType: TerminalType = TerminalType.Xterm,
) {
    val input = state.draft
    val concealInput = state.concealInput
    var pasteReview by remember(hostId, state.sessionId) { mutableStateOf<String?>(null) }
    var ctrlNext by remember(hostId, state.sessionId) { mutableStateOf(false) }
    var altNext by remember(hostId, state.sessionId) { mutableStateOf(false) }
    val fontSize = state.fontSize
    var menuOpen by remember { mutableStateOf(false) }
    var endReview by remember { mutableStateOf<String?>(null) }
    val fontScale = LocalDensity.current.fontScale

    fun sendLine() {
        if (!state.connected) return
        if (input.contains('\n') && !ctrlNext && !altNext) pasteReview = input + "\r"
        else {
            val value = if (ctrlNext && input.length == 1) ((input[0].uppercaseChar().code) and 0x1f).toChar().toString()
                else if (ctrlNext || altNext) input else input + "\r"
            onSend(if (altNext) "\u001b$value" else value)
            onDraftChange("")
            ctrlNext = false
            altNext = false
        }
    }

    if (pasteReview != null) AlertDialog(
        onDismissRequest = { pasteReview = null },
        title = { Text(stringResource(R.string.terminal_paste_title)) },
        text = { SelectionContainer { Text(pasteReview.orEmpty().take(2000)) } },
        confirmButton = { TextButton(enabled = state.connected, onClick = { onSend(pasteReview.orEmpty()); onDraftChange(""); pasteReview = null }) {
            Text(stringResource(R.string.terminal_send)) } },
        dismissButton = { TextButton(onClick = { pasteReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    )

    endReview?.let { id -> AlertDialog(
        onDismissRequest = { endReview = null },
        title = { Text(stringResource(R.string.terminal_close)) },
        text = { Text(stringResource(R.string.terminal_close_confirm, id.take(8))) },
        confirmButton = { TextButton(onClick = { onEnd(id); endReview = null }) { Text(stringResource(R.string.terminal_close)) } },
        dismissButton = { TextButton(onClick = { endReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    ) }

    val actions: @Composable () -> Unit = {
        androidx.compose.foundation.layout.Box {
            val actionsLabel = stringResource(R.string.terminal_session_actions)
            IconButton(onClick = { menuOpen = true }, modifier = Modifier.semantics { contentDescription = actionsLabel }) { Text("⋮") }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                DropdownMenuItem(text = { Text(stringResource(R.string.terminal_new)) },
                    onClick = { menuOpen = false; onNew() }, enabled = canCreate)
                sessions.forEach { session ->
                    DropdownMenuItem(text = { Text(sshSessionLabel(session)) },
                        onClick = { menuOpen = false; onSelect(session.sessionId) })
                }
                if (state.sessionId.isNotEmpty()) DropdownMenuItem(text = { Text(stringResource(R.string.terminal_close)) },
                    onClick = { menuOpen = false; endReview = state.sessionId })
            }
        }
    }

    TerminalScreenLayout(modifier = modifier, imeInsets = imeInsets, header = { compact ->
        if (compact) Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onClose) {
                DesktopIcon(DesktopIcons.back, size = 22.dp, contentDescription = stringResource(R.string.common_back))
            }
            Text(hostLabel ?: stringResource(R.string.ssh_terminal_title), modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            actions()
        } else ScreenHeader(
            title = stringResource(R.string.ssh_terminal_title),
            subtitle = hostLabel,
            onBack = onClose,
        )
        if (!compact || !state.connected) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(
                stringResource(when {
                    state.connecting -> R.string.ssh_terminal_connecting
                    state.problem -> R.string.ssh_terminal_failed
                    state.connected -> R.string.ssh_terminal_connected
                    else -> R.string.ssh_terminal_disconnected
                }),
                modifier = Modifier.weight(1f),
                color = if (state.problem) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (!state.connecting && !state.connected) OutlinedButton(onClick = onConnect) {
                Text(stringResource(R.string.ssh_terminal_retry))
            }
        }
        if (!compact) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.xs),
            verticalAlignment = Alignment.CenterVertically) {
            Button(onClick = onNew, enabled = canCreate, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.terminal_new), maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            OutlinedButton(onClick = { onFontSizeChange((fontSize - 1).coerceAtLeast(9)) }, modifier = Modifier.width(48.dp),
                contentPadding = PaddingValues(0.dp)) { Text("A−") }
            OutlinedButton(onClick = { onFontSizeChange((fontSize + 1).coerceAtMost(24)) }, modifier = Modifier.width(48.dp),
                contentPadding = PaddingValues(0.dp)) { Text("A+") }
            actions()
        }
        if (!compact && sessions.isNotEmpty()) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            sessions.forEach { session -> FilterChip(selected = session.sessionId == state.sessionId,
                onClick = { onSelect(session.sessionId) }, label = { Text(sshSessionLabel(session)) }) }
        }
        if (state.sessionId.isEmpty() && !compact) Text(stringResource(R.string.ssh_terminal_empty),
            style = MaterialTheme.typography.bodySmall)
    }, output = {
        if (terminalType == TerminalType.Native) NativeTerminal(
            state.sessionId, state.transcript, fontSize, onResize,
            Modifier.fillMaxSize().testTag("ssh-terminal-output"), connected = state.connected,
        ) else
        Surface(modifier = Modifier.fillMaxSize(), color = Color(0xFF101820),
            shape = MaterialTheme.shapes.medium) {
            app.relaxkonos.mobile.ui.terminal.XtermTerminal(
                sessionId = state.sessionId, output = state.output,
                fontSize = fontSize * fontScale, connected = state.connected,
                onSend = onSend, onResize = onResize,
                modifier = Modifier.fillMaxSize().clip(MaterialTheme.shapes.medium).testTag("ssh-terminal-output"),
            )
        }
    }, keys = {
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton({ ctrlNext = !ctrlNext }, enabled = state.connected) { Text(if (ctrlNext) "Ctrl ✓" else "Ctrl") }
            OutlinedButton({ altNext = !altNext }, enabled = state.connected) { Text(if (altNext) "Alt ✓" else "Alt") }
            OutlinedButton({ onSend("\u001b") }, enabled = state.connected) { Text("Esc") }
            OutlinedButton({ onSend("\u0003") }, enabled = state.connected) { Text("Ctrl+C") }
            OutlinedButton({ onSend("\t") }, enabled = state.connected) { Text("Tab") }
            OutlinedButton({ onSend("\u001b[A") }, enabled = state.connected) { Text("↑") }
            OutlinedButton({ onSend("\u001b[B") }, enabled = state.connected) { Text("↓") }
            OutlinedButton({ onSend("\u001b[D") }, enabled = state.connected) { Text("←") }
            OutlinedButton({ onSend("\u001b[C") }, enabled = state.connected) { Text("→") }
        }
    }, input = { compact ->
        Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            if (!compact) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Checkbox(checked = concealInput, onCheckedChange = onConcealChange)
                Text(stringResource(R.string.ssh_terminal_hide_input), modifier = Modifier.padding(top = Spacing.sm))
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = input,
                    onValueChange = onDraftChange,
                    modifier = Modifier.weight(1f).heightIn(min = 56.dp),
                    label = { Text(stringResource(R.string.ssh_terminal_input)) },
                    maxLines = if (compact) 1 else 3,
                    enabled = state.connected,
                    visualTransformation = if (concealInput) PasswordVisualTransformation() else VisualTransformation.None,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                    keyboardActions = KeyboardActions(onSend = { sendLine() }),
                )
                Button(onClick = { sendLine() }, enabled = state.connected) {
                    Text(stringResource(R.string.ssh_terminal_send))
                }
            }
        }
    })
}

@Composable
private fun sshSessionLabel(session: SshTerminalUiState): String {
    val status = stringResource(when {
        session.connecting -> R.string.ssh_terminal_connecting
        session.connected -> R.string.ssh_terminal_connected
        session.problem -> R.string.ssh_terminal_failed
        else -> R.string.ssh_terminal_disconnected
    })
    return "${session.sessionId.take(8)} · $status"
}
