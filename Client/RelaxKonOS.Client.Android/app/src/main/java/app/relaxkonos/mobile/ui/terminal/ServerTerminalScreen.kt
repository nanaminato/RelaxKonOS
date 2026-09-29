package app.relaxkonos.mobile.ui.terminal

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
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
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ServerTerminalConnection
import app.relaxkonos.mobile.core.net.TerminalSessionSummary
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Retains only a session ID, never command input or credentials. The server retains the PTY. */
class ServerTerminalViewModel(application: Application) : AndroidViewModel(application) {
    private val auth = getApplication<RelaxKonApplication>().container.session
    private val mutable = MutableStateFlow(ServerTerminalState())
    val state = mutable.asStateFlow()
    private var transport: ServerTerminalConnection? = null
    private var job: Job? = null
    private var generation = 0
    private var selectedId: String? = null
    private var transcript = TerminalTranscript()
    private var columns = 80
    private var rows = 24

    fun connect(owner: SessionState.Active) {
        if (job?.isActive == true || mutable.value.connected) return
        val current = ++generation
        mutable.update { it.copy(connecting = true, error = false) }
        job = viewModelScope.launch {
            val old = transport
            transport = null
            if (old != null) withContext(Dispatchers.IO) { runCatching { old.close() } }
            var connection: ServerTerminalConnection? = null
            try {
                connection = ServerTerminalConnection(owner.effectiveBaseUrl, { auth.accessToken },
                    { bytes ->
                        if (current == generation) {
                            val text = synchronized(this@ServerTerminalViewModel) { transcript.append(bytes) }
                            mutable.update { it.copy(output = text) }
                        }
                    },
                    { code -> if (current == generation) mutable.update { it.copy(connected = false, exitCode = code) } },
                    { if (current == generation) mutable.update { it.copy(connected = false, connecting = false) } })
                withContext(Dispatchers.IO) { connection.connect() }
                if (current != generation || auth.state.value !== owner) {
                    withContext(Dispatchers.IO) { connection.close() }
                    return@launch
                }
                transport = connection
                val sessions = connection.sessions().filterNot { it.hasExited }
                mutable.update { it.copy(connecting = false, connected = true, sessions = sessions) }
                val saved = selectedId
                if (saved != null && sessions.any { it.sessionId == saved }) attach(saved)
                else if (saved != null) {
                    selectedId = null
                    mutable.update { it.copy(sessionLost = true, sessionId = null, output = "") }
                }
            } catch (_: CancellationException) {
                throw CancellationException()
            } catch (_: Exception) {
                if (current == generation) mutable.update { it.copy(connecting = false, connected = false, error = true) }
                if (current == generation) transport = null
                withContext(Dispatchers.IO) { runCatching { connection?.close() } }
            }
        }
    }

    fun attach(id: String?) {
        val connection = transport ?: return
        viewModelScope.launch {
            try {
                // A missing ID would create a new shell. Re-check it before attaching a recovered session.
                if (id != null && connection.sessions().none { it.sessionId == id && !it.hasExited }) {
                    selectedId = null
                    mutable.update { it.copy(sessionLost = true, sessionId = null) }
                    return@launch
                }
                synchronized(this@ServerTerminalViewModel) { transcript = TerminalTranscript().also { it.resize(columns, rows) } }
                mutable.update { it.copy(output = "", sessionLost = false) }
                val result = connection.attach(id, columns, rows)
                selectedId = result.sessionId
                mutable.update { it.copy(sessionId = result.sessionId, exitCode = null, sessions = connection.sessions()) }
            } catch (_: Exception) { mutable.update { it.copy(error = true) } }
        }
    }

    fun send(text: String) {
        val connection = transport ?: return
        if (mutable.value.sessionId == null) return
        viewModelScope.launch { try { connection.input(text.toByteArray(Charsets.UTF_8)) }
            catch (_: Exception) { mutable.update { it.copy(error = true) } } }
    }

    fun resize(newColumns: Int, newRows: Int) {
        columns = newColumns.coerceIn(20, 300)
        rows = newRows.coerceIn(5, 100)
        synchronized(this) { transcript.resize(columns, rows) }
        val connection = transport ?: return
        if (mutable.value.sessionId == null) return
        viewModelScope.launch { runCatching { connection.resize(columns, rows) } }
    }

    fun closeSession() {
        val connection = transport ?: return
        viewModelScope.launch {
            try {
                connection.terminate()
                selectedId = null
                synchronized(this@ServerTerminalViewModel) { transcript = TerminalTranscript().also { it.resize(columns, rows) } }
                mutable.update { it.copy(sessionId = null, output = "", sessions = connection.sessions()) }
            } catch (_: Exception) { mutable.update { it.copy(error = true) } }
        }
    }

    fun detach() {
        generation++
        job?.cancel()
        job = null
        val previous = transport
        transport = null
        mutable.update { it.copy(connected = false, connecting = false) }
        if (previous != null) viewModelScope.launch(Dispatchers.IO) { runCatching { previous.close() } }
    }

    override fun onCleared() { detach(); super.onCleared() }
}

data class ServerTerminalState(
    val connecting: Boolean = false,
    val connected: Boolean = false,
    val error: Boolean = false,
    val sessionLost: Boolean = false,
    val sessionId: String? = null,
    val sessions: List<TerminalSessionSummary> = emptyList(),
    val output: String = "",
    val exitCode: Int? = null,
)

@Composable
fun ServerTerminalScreen(owner: SessionState.Active, modifier: Modifier = Modifier) {
    val model: ServerTerminalViewModel = viewModel()
    val state by model.state.collectAsState()
    var input by remember { mutableStateOf("") }
    var pasteReview by remember { mutableStateOf<String?>(null) }
    var ctrlNext by remember { mutableStateOf(false) }
    var altNext by remember { mutableStateOf(false) }
    var fontSize by remember { mutableStateOf(13) }
    val scroll = rememberScrollState()
    LaunchedEffect(owner) { model.connect(owner) }
    DisposableEffect(owner) { onDispose { model.detach() } }
    LaunchedEffect(state.output) { scroll.scrollTo(scroll.maxValue) }
    fun sendLine() {
        if (input.contains('\n') && !ctrlNext && !altNext) pasteReview = input + "\r"
        else {
            val payload = if (ctrlNext && input.length == 1) {
                ((input[0].uppercaseChar().code) and 0x1f).toChar().toString()
            } else if (ctrlNext || altNext) input else input + "\r"
            model.send(if (altNext) "\u001b$payload" else payload)
            input = ""
            ctrlNext = false
            altNext = false
        }
    }
    if (pasteReview != null) AlertDialog(
        onDismissRequest = { pasteReview = null },
        title = { Text(stringResource(R.string.terminal_paste_title)) },
        text = { SelectionContainer { Text(pasteReview.orEmpty().take(2000)) } },
        confirmButton = { TextButton(onClick = { model.send(pasteReview.orEmpty()); input = ""; pasteReview = null }) {
            Text(stringResource(R.string.terminal_send)) } },
        dismissButton = { TextButton(onClick = { pasteReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    )
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(title = stringResource(R.string.terminal_server_title), subtitle = "${owner.userName} · ${owner.effectiveBaseUrl}")
        Text(stringResource(when {
            state.connecting -> R.string.terminal_connecting
            state.error -> R.string.terminal_failed
            state.sessionLost -> R.string.terminal_session_lost
            state.connected -> R.string.terminal_connected
            else -> R.string.terminal_detached
        }), color = if (state.error) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
        if (!state.connected && !state.connecting) OutlinedButton(onClick = { model.connect(owner) }) {
            Text(stringResource(R.string.terminal_reconnect))
        }
        if (state.connected) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Button(onClick = { model.attach(null) }) { Text(stringResource(R.string.terminal_new)) }
                if (state.sessionId != null) OutlinedButton(onClick = model::closeSession) { Text(stringResource(R.string.terminal_close)) }
            }
            state.sessions.forEach { session ->
                OutlinedButton(onClick = { model.attach(session.sessionId) }, enabled = session.sessionId != state.sessionId) {
                    Text("${session.createdAt} · ${session.sessionId.take(8)}")
                }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { fontSize = (fontSize - 1).coerceAtLeast(9) }) { Text("A−") }
            OutlinedButton(onClick = { fontSize = (fontSize + 1).coerceAtMost(24) }) { Text("A+") }
            state.exitCode?.let { Text(stringResource(R.string.terminal_exit_code, it)) }
        }
        BoxWithConstraints(Modifier.fillMaxWidth().weight(1f)) {
            val width = maxWidth.value
            val height = maxHeight.value
            LaunchedEffect(width, height, fontSize) {
                model.resize((width / (fontSize * 0.59f)).toInt(), (height / (fontSize * 1.5f)).toInt())
            }
            Surface(Modifier.fillMaxSize(), color = Color(0xFF101820), contentColor = Color(0xFFF2F5F7), shape = MaterialTheme.shapes.medium) {
                SelectionContainer { Column(Modifier.verticalScroll(scroll).padding(Spacing.md)) {
                    Text(state.output, fontFamily = FontFamily.Monospace, fontSize = fontSize.sp)
                } }
            }
        }
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { ctrlNext = !ctrlNext }, enabled = state.sessionId != null) {
                Text(if (ctrlNext) "Ctrl ✓" else "Ctrl")
            }
            OutlinedButton(onClick = { altNext = !altNext }, enabled = state.sessionId != null) {
                Text(if (altNext) "Alt ✓" else "Alt")
            }
            listOf("Esc" to "\u001b", "Tab" to "\t", "Ctrl+C" to "\u0003", "↑" to "\u001b[A", "↓" to "\u001b[B", "←" to "\u001b[D", "→" to "\u001b[C")
                .forEach { (label, key) -> OutlinedButton(onClick = { model.send(key) }, enabled = state.sessionId != null) { Text(label) } }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedTextField(value = input, onValueChange = { input = it }, modifier = Modifier.weight(1f),
                label = { Text(stringResource(R.string.terminal_input)) }, enabled = state.sessionId != null,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send), keyboardActions = KeyboardActions(onSend = { sendLine() }))
            Button(onClick = { sendLine() }, enabled = state.sessionId != null) { Text(stringResource(R.string.terminal_send)) }
        }
    }
}
