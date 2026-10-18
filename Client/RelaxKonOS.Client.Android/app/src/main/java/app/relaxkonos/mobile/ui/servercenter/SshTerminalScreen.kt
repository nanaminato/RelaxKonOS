package app.relaxkonos.mobile.ui.servercenter
import app.relaxkonos.mobile.ui.common.CheckboxOption
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.Icon
import androidx.compose.foundation.layout.size

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
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.runtime.key
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.rememberUpdatedState
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
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
fun SshTerminalScreen(hostId: String, onClose: () -> Unit, isPageCurrent: () -> Boolean, modifier: Modifier = Modifier) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val target = container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    val model = container.sshTerminals
    val all by model.state.collectAsStateWithLifecycle()
    LaunchedEffect(hostId) { model.enter(hostId) }
    val state = all.selectedForHost(hostId) ?: SshTerminalUiState(hostId = hostId)
    val sessions = all.sessions.filter { it.hostId == hostId }
    val workspaceRevision = container.serverCenter.workspaceRevision
    key(hostId, workspaceRevision) {
    SshTerminalContent(hostId, target?.let { "${it.sshUserName}@${it.sshHost}:${it.sshPort}" },
        state, { model.reconnect(state.sessionId) }, { model.send(state.sessionId, it) },
        { columns, rows -> model.resize(state.sessionId, columns, rows) }, onClose,
        { model.updateDraft(state.sessionId, it) }, { model.concealInput(state.sessionId, it) },
        { model.fontSize(state.sessionId, it) }, sessions, all.sessions.size < SshTerminalSessions.MAX_SESSIONS, model::select,
        { model.create(hostId) }, model::end,
        readState = { if (isPageCurrent() && container.serverCenter.sshFilesHostId == hostId && container.serverCenter.workspaceRevision == workspaceRevision)
            model.state.value.selectedForHost(hostId) else null },
        modifier = modifier, terminalType = container.appearance.terminalType)
    }
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
    readState: () -> SshTerminalUiState?,
    modifier: Modifier = Modifier,
    imeInsets: WindowInsets = WindowInsets.ime,
    terminalType: TerminalType = TerminalType.Xterm,
) {
    val input = state.draft
    val concealInput = state.concealInput
    var pasteReview by remember(hostId, state.sessionId) { mutableStateOf<SshPasteReview?>(null) }
    var ctrlNext by remember(hostId, state.sessionId) { mutableStateOf(false) }
    var altNext by remember(hostId, state.sessionId) { mutableStateOf(false) }
    val fontSize = state.fontSize
    var menuOpen by remember(hostId, state.sessionId) { mutableStateOf(false) }
    var endReview by remember(hostId, state.sessionId) { mutableStateOf<SshEndReview?>(null) }
    val fontScale = LocalDensity.current.fontScale
    val liveState by rememberUpdatedState(readState)
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var foreground by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    fun withdraw() { pasteReview = null; endReview = null; menuOpen = false; ctrlNext = false; altNext = false }
    fun current(owner: SshTerminalReviewOwner, connected: Boolean = false): Boolean {
        val latest = liveState()
        return foreground && owner.matches(latest) && (!connected || latest?.connected == true)
    }
    DisposableEffect(lifecycle, hostId, state.sessionId) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_RESUME -> foreground = true
                Lifecycle.Event.ON_PAUSE -> { foreground = false; withdraw() }
                else -> Unit
            }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); foreground = false; withdraw() }
    }
    LaunchedEffect(state.sessionId, state.hostId, state.inputRevision, state.draft, state.connected) {
        pasteReview?.let { if (!current(it.owner, connected = true)) pasteReview = null }
        endReview?.let { if (!current(it.owner)) endReview = null }
    }

    fun closePage() { foreground = false; withdraw(); onClose() }

    fun sendLine() {
        val owner = SshTerminalReviewOwner(state)
        if (!current(owner, connected = true) || pasteReview != null || endReview != null) return
        val value = sshTerminalLinePayload(input, ctrlNext, altNext)
        if (sshTerminalNeedsReview(input)) pasteReview = SshPasteReview(owner, value)
        else {
            onSend(value)
            onDraftChange("")
            ctrlNext = false; altNext = false
        }
    }

    pasteReview?.let { expected -> AlertDialog(
        onDismissRequest = { if (pasteReview === expected) pasteReview = null },
        title = { Text(stringResource(R.string.terminal_paste_title)) },
        text = { Column(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState())) {
            SelectionContainer { Text(expected.payload) }
        } },
        confirmButton = { TextButton(enabled = state.connected, onClick = {
            if (pasteReview === expected && current(expected.owner, connected = true)) {
                pasteReview = null
                ctrlNext = false; altNext = false
                onSend(expected.payload); onDraftChange("")
            }
        }) { Text(stringResource(R.string.terminal_send)) } },
        dismissButton = { TextButton(onClick = { if (pasteReview === expected) pasteReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    ) }

    endReview?.let { expected -> AlertDialog(
        onDismissRequest = { if (endReview === expected) endReview = null },
        title = { Text(stringResource(R.string.terminal_close)) },
        text = { Column(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState())) {
            Text(stringResource(R.string.terminal_close_confirm, stringResource(R.string.terminal_session_name, expected.number)))
            Text(expected.hostLabel)
            Text(expected.owner.sessionId)
        } },
        confirmButton = { TextButton(onClick = {
            if (endReview === expected && current(expected.owner)) { endReview = null; onEnd(expected.owner.sessionId) }
        }) { Text(stringResource(R.string.terminal_close)) } },
        dismissButton = { TextButton(onClick = { if (endReview === expected) endReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    ) }

    val actions: @Composable () -> Unit = {
        androidx.compose.foundation.layout.Box {
            val actionsLabel = stringResource(R.string.terminal_session_actions)
            IconButton(onClick = { menuOpen = true }, modifier = Modifier.size(48.dp)) {
                Icon(Icons.Default.MoreVert, contentDescription = actionsLabel, modifier = Modifier.size(24.dp))
            }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                DropdownMenuItem(text = { Text(stringResource(R.string.terminal_new)) },
                    onClick = { menuOpen = false; onNew() }, enabled = canCreate)
                sessions.forEach { session ->
                    DropdownMenuItem(text = { Text(sshSessionLabel(session, sessions.indexOf(session) + 1)) },
                        onClick = { menuOpen = false; onSelect(session.sessionId) })
                }
                if (state.sessionId.isNotEmpty()) DropdownMenuItem(text = { Text(stringResource(R.string.terminal_close)) },
                    onClick = {
                        menuOpen = false
                        val owner = SshTerminalReviewOwner(state)
                        if (current(owner) && endReview == null && pasteReview == null)
                            endReview = SshEndReview(owner, sessions.indexOfFirst { it.sessionId == state.sessionId } + 1, hostLabel ?: hostId)
                    })
            }
        }
    }

    TerminalScreenLayout(modifier = modifier, imeInsets = imeInsets, header = { compact, wide ->
        if (compact) Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = ::closePage) {
                DesktopIcon(DesktopIcons.back, size = 22.dp, contentDescription = stringResource(R.string.common_back))
            }
            Text(hostLabel ?: stringResource(R.string.ssh_terminal_title), modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            actions()
        } else ScreenHeader(
            title = stringResource(R.string.ssh_terminal_title),
            subtitle = hostLabel,
            onBack = ::closePage,
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
        if (!compact && !wide && sessions.isNotEmpty()) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            sessions.forEach { session -> FilterChip(selected = session.sessionId == state.sessionId,
                onClick = { onSelect(session.sessionId) }, label = { Text(sshSessionLabel(session, sessions.indexOf(session) + 1)) }) }
        }
        if (state.sessionId.isEmpty() && !compact) Text(stringResource(R.string.ssh_terminal_empty),
            style = MaterialTheme.typography.bodySmall)
    }, sidebar = {
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.terminal_sessions_title), style = MaterialTheme.typography.titleMedium)
            sessions.forEach { session ->
                FilterChip(selected = session.sessionId == state.sessionId,
                    onClick = { onSelect(session.sessionId) }, label = { Text(sshSessionLabel(session, sessions.indexOf(session) + 1)) })
            }
        }
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
            if (!compact) CheckboxOption(concealInput, stringResource(R.string.ssh_terminal_hide_input), onCheckedChange = onConcealChange)
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
private fun sshSessionLabel(session: SshTerminalUiState, number: Int): String {
    val status = stringResource(when {
        session.connecting -> R.string.ssh_terminal_connecting
        session.connected -> R.string.ssh_terminal_connected
        session.problem -> R.string.ssh_terminal_failed
        else -> R.string.ssh_terminal_disconnected
    })
    return "${stringResource(R.string.terminal_session_name, number)} · $status"
}
