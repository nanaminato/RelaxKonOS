package app.relaxkonos.mobile.ui.terminal

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.IconButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch
import androidx.compose.runtime.rememberCoroutineScope
import androidx.lifecycle.compose.LifecycleStartEffect

/** Retains a selected session across rotation. Leaving the page only detaches its transport. */
class ServerTerminalViewModel(application: Application) : AndroidViewModel(application) {
    private val controller = ServerTerminalController(
        getApplication<RelaxKonApplication>().container.session, viewModelScope,
    )
    val state = controller.state
    fun connect(owner: SessionState.Active) = controller.connect(owner)
    fun attach(id: String?) = controller.attach(id)
    fun send(text: String) = controller.send(text)
    fun resize(columns: Int, rows: Int) = controller.resize(columns, rows)
    fun close(sessionId: String) = controller.close(sessionId)
    fun closeOtherSessions() = controller.closeOtherSessions()
    fun detach() = controller.detach()
    override fun onCleared() { detach(); super.onCleared() }
}

@Composable
fun ServerTerminalScreen(owner: SessionState.Active, modifier: Modifier = Modifier) {
    val model: ServerTerminalViewModel = viewModel()
    val state by model.state.collectAsState()
    LifecycleStartEffect(owner) {
        model.connect(owner)
        onStopOrDispose { model.detach() }
    }
    ServerTerminalContent(owner, state, { model.connect(owner) }, model::attach, model::send,
        model::resize, model::close, model::closeOtherSessions, modifier)
}

@Composable
internal fun ServerTerminalContent(
    owner: SessionState.Active,
    state: ServerTerminalState,
    onConnect: () -> Unit,
    onAttach: (String?) -> Unit,
    onSend: (String) -> Boolean,
    onResize: (Int, Int) -> Unit,
    onCloseSession: (String) -> Unit,
    onCloseOthers: () -> Unit,
    modifier: Modifier = Modifier,
    imeInsets: WindowInsets = WindowInsets.ime,
) {
    var input by remember { mutableStateOf("") }
    var pasteReview by remember { mutableStateOf<String?>(null) }
    var ctrlNext by remember { mutableStateOf(false) }
    var altNext by remember { mutableStateOf(false) }
    var fontSize by rememberSaveable { mutableStateOf(13) }
    var menuOpen by remember { mutableStateOf(false) }
    var closeReview by remember { mutableStateOf<Pair<String?, String>?>(null) }
    val scroll = rememberScrollState()
    val uiScope = rememberCoroutineScope()
    var followOutput by remember { mutableStateOf(true) }
    val fontScale = LocalDensity.current.fontScale
    val decreaseFontLabel = stringResource(R.string.terminal_font_smaller)
    val increaseFontLabel = stringResource(R.string.terminal_font_larger)
    val sessionActionsLabel = stringResource(R.string.terminal_session_actions)
    LaunchedEffect(state.sessionId) { ctrlNext = false; altNext = false; followOutput = true }
    LaunchedEffect(scroll) {
        snapshotFlow { scroll.isScrollInProgress to scroll.value }.collect { (scrolling, value) ->
            if (scrolling) followOutput = value >= scroll.maxValue - 24
        }
    }
    LaunchedEffect(state.output, followOutput) { if (followOutput) scroll.scrollTo(scroll.maxValue) }
    fun sendLine() {
        if (!state.canInput) return
        if (input.contains('\n') && !ctrlNext && !altNext) pasteReview = input + "\r"
        else {
            val payload = if (ctrlNext && input.length == 1) {
                ((input[0].uppercaseChar().code) and 0x1f).toChar().toString()
            } else if (ctrlNext || altNext) input else input + "\r"
            if (onSend(if (altNext) "\u001b$payload" else payload)) {
                input = ""
                ctrlNext = false
                altNext = false
            }
        }
    }
    if (pasteReview != null) AlertDialog(
        onDismissRequest = { pasteReview = null },
        title = { Text(stringResource(R.string.terminal_paste_title)) },
        text = { SelectionContainer { Text(pasteReview.orEmpty().take(2000)) } },
        confirmButton = { TextButton(enabled = state.canInput, onClick = {
            if (onSend(pasteReview.orEmpty())) { input = ""; pasteReview = null }
        }) {
            Text(stringResource(R.string.terminal_send)) } },
        dismissButton = { TextButton(onClick = { pasteReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    )
    closeReview?.let { (id, target) -> AlertDialog(
        onDismissRequest = { closeReview = null },
        title = { Text(stringResource(R.string.terminal_close)) },
        text = { Text(stringResource(R.string.terminal_close_confirm, target)) },
        confirmButton = { TextButton(enabled = state.connected && !state.busy, onClick = {
            if (id == null) onCloseOthers() else onCloseSession(id)
            closeReview = null
        }) { Text(stringResource(R.string.terminal_close)) } },
        dismissButton = { TextButton(onClick = { closeReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    ) }
    TerminalScreenLayout(modifier = modifier, imeInsets = imeInsets, header = { compact ->
        // Keep long server addresses to one line so they cannot consume the terminal viewport.
        if (compact) {
            val selectedSession = state.sessions.firstOrNull { it.sessionId == state.sessionId }
            val sessionLabel = selectedSession?.let {
                terminalSessionLabel(it.createdAt, it.sessionId, System.currentTimeMillis())
            } ?: stringResource(R.string.terminal_server_title)
            Text("${owner.userName} · $sessionLabel", style = MaterialTheme.typography.bodySmall,
                maxLines = 1, overflow = TextOverflow.Ellipsis)
        } else Column {
            Text(stringResource(R.string.terminal_server_title), style = MaterialTheme.typography.titleLarge)
            Text("${owner.userName} · ${owner.effectiveBaseUrl}", style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        if (!compact || !state.connected || state.error || state.sessionLost || state.busy) Text(stringResource(when {
            state.connecting && state.retryAttempt > 0 -> R.string.terminal_recovering
            state.connecting -> R.string.terminal_connecting
            state.busy -> R.string.terminal_switching
            state.error -> R.string.terminal_failed
            state.sessionLost -> R.string.terminal_session_lost
            state.connected -> R.string.terminal_connected
            else -> R.string.terminal_detached
        }), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
            color = if (state.error) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
        if (!compact) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Box(Modifier.weight(1f)) {
                    if (!state.connected && !state.connecting) Button(onClick = { onConnect() }) {
                        Text(stringResource(R.string.terminal_reconnect), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    } else Button(onClick = { onAttach(null) }, enabled = state.connected && !state.busy) {
                        Text(stringResource(R.string.terminal_new), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
                TextButton(onClick = { fontSize = (fontSize - 1).coerceAtLeast(9) }, enabled = fontSize > 9,
                    contentPadding = PaddingValues(horizontal = Spacing.xs),
                    modifier = Modifier.width(48.dp).semantics { contentDescription = decreaseFontLabel }) { Text("A−") }
                TextButton(onClick = { fontSize = (fontSize + 1).coerceAtMost(24) }, enabled = fontSize < 24,
                    contentPadding = PaddingValues(horizontal = Spacing.xs),
                    modifier = Modifier.width(48.dp).semantics { contentDescription = increaseFontLabel }) { Text("A+") }
                if (state.sessions.size > 1) Box {
                    TextButton(onClick = { menuOpen = true }, enabled = state.connected && !state.busy,
                        modifier = Modifier.width(48.dp).semantics { contentDescription = sessionActionsLabel }) { Text("⋮") }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        val target = stringResource(R.string.terminal_close_others)
                        DropdownMenuItem(text = { Text(target) }, onClick = {
                            menuOpen = false
                            closeReview = null to state.sessions.filter { it.sessionId != state.sessionId }.joinToString("\n") {
                                terminalSessionLabel(it.createdAt, it.sessionId, System.currentTimeMillis())
                            }
                        })
                    }
                }
            }
            if (state.sessions.isNotEmpty()) {
                // One scrolling row rather than a growing column. `RelaxKonOS.Mobile.Design.md` gives a phone
                // a single focused session, so switching and pruning must never take rows away from the
                // terminal — which is exactly what a vertical session list did, one session at a time.
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    state.sessions.forEach { session ->
                        val active = session.sessionId == state.sessionId
                        val stamp = terminalSessionLabel(session.createdAt, session.sessionId, System.currentTimeMillis())
                        TerminalSessionChip(
                            label = if (active) "${stringResource(R.string.terminal_session_current)} · $stamp" else stamp,
                            active = active,
                            closeLabel = "${stringResource(R.string.terminal_close)} · $stamp",
                            enabled = state.connected && !state.busy,
                            onSelect = { if (!active) onAttach(session.sessionId) },
                            onClose = { closeReview = session.sessionId to stamp },
                        )
                    }
                }
            }
        }
        state.exitCode?.let { Text(stringResource(R.string.terminal_exit_code, it), style = MaterialTheme.typography.bodySmall) }
    }, output = {
        BoxWithConstraints(Modifier.fillMaxSize()) {
            val width = maxWidth.value
            val height = maxHeight.value
            LaunchedEffect(width, height, fontSize, fontScale) {
                // Text size is in scaled pixels; subtract the actual transcript padding first.
                onResize(((width - 2 * Spacing.md.value) / (fontSize * fontScale * 0.61f)).toInt(),
                    ((height - 2 * Spacing.md.value) / (fontSize * fontScale * 1.5f)).toInt())
            }
            Surface(Modifier.fillMaxSize(), color = Color(0xFF101820), contentColor = Color(0xFFF2F5F7), shape = MaterialTheme.shapes.medium) {
                Box {
                    SelectionContainer { Column(Modifier.fillMaxSize().verticalScroll(scroll)
                        .horizontalScroll(rememberScrollState()).padding(Spacing.md)) {
                        if (state.output.isEmpty() && state.connected && !state.busy && state.sessionId == null)
                            Text(stringResource(R.string.terminal_empty), color = Color(0xFFB7C5D0))
                        else Text(state.output, fontFamily = FontFamily.Monospace, fontSize = fontSize.sp,
                            lineHeight = (fontSize * 1.5f).sp, softWrap = false)
                    } }
                    if (!followOutput && state.output.isNotEmpty()) TextButton(
                        onClick = { followOutput = true; uiScope.launch { scroll.scrollTo(scroll.maxValue) } },
                        modifier = Modifier.align(Alignment.BottomEnd).background(MaterialTheme.colorScheme.surface, CircleShape),
                    ) { Text(stringResource(R.string.terminal_latest)) }
                }
            }
        }
    }, keys = {
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { ctrlNext = !ctrlNext }, enabled = state.canInput) {
                Text(if (ctrlNext) "Ctrl ✓" else "Ctrl")
            }
            OutlinedButton(onClick = { altNext = !altNext }, enabled = state.canInput) {
                Text(if (altNext) "Alt ✓" else "Alt")
            }
            listOf("Esc" to "\u001b", "Tab" to "\t", "Ctrl+C" to "\u0003", "↑" to "\u001b[A", "↓" to "\u001b[B", "←" to "\u001b[D", "→" to "\u001b[C")
                .forEach { (label, key) -> OutlinedButton(onClick = { onSend(key) }, enabled = state.canInput) { Text(label) } }
        }
    }, input = { compact ->
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = input, onValueChange = { input = it }, modifier = Modifier.weight(1f).heightIn(min = 56.dp),
                label = { Text(stringResource(R.string.terminal_input)) }, enabled = state.canInput,
                maxLines = if (compact) 1 else 3,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send), keyboardActions = KeyboardActions(onSend = { sendLine() }))
            Button(onClick = { sendLine() }, enabled = state.canInput) { Text(stringResource(R.string.terminal_send)) }
        }
    })
}

/**
 * One entry of the session strip.
 *
 * The attached session is both tinted and named — colour alone never carries the state — and every
 * entry closes itself, because a PTY is freed only by an explicit Server call and a phone has no room
 * for a separate screen of session management.
 */
@Composable
private fun TerminalSessionChip(
    label: String,
    active: Boolean,
    closeLabel: String,
    enabled: Boolean,
    onSelect: () -> Unit,
    onClose: () -> Unit,
) {
    val container = if (active) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainerHighest
    val content = if (active) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurfaceVariant
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(Radius.pill))
            .background(container)
            .clickable(enabled = enabled, role = Role.Tab, onClick = onSelect)
            .semantics { selected = active }
            .padding(start = Spacing.md, end = Spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = content, maxLines = 1)
        // The glyph is not a word, so the accessible name is set here rather than left as "multiplication sign".
        IconButton(onClick = onClose, enabled = enabled, modifier = Modifier.semantics { contentDescription = closeLabel }) {
            Text("×", style = MaterialTheme.typography.labelMedium, color = content)
        }
    }
}
