package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerCenterSshTerminal
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import app.relaxkonos.mobile.servercenter.SshTerminalTranscript
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** The PTY belongs only to this page. Leaving it closes the shell and clears its transcript. */
class SshTerminalViewModel(application: Application) : AndroidViewModel(application) {
    private val coordinator = getApplication<RelaxKonApplication>().container.serverCenter
    private val connections = getApplication<RelaxKonApplication>().container.serverCenterConnections
    private val mutableState = MutableStateFlow(SshTerminalUiState())
    val state = mutableState.asStateFlow()

    private var generation = 0
    private var connectionJob: Job? = null
    private var terminal: ServerCenterSshTerminal? = null

    fun start(hostId: String) {
        if (mutableState.value.hostId == hostId && connectionJob?.isActive == true &&
            (mutableState.value.connecting || mutableState.value.connected)) return
        stop()
        val current = ++generation
        mutableState.value = SshTerminalUiState(hostId = hostId, connecting = true)
        connectionJob = viewModelScope.launch {
            val secret = coordinator.workspacePasswordCopy()
            if (secret == null) {
                if (current == generation) mutableState.update { it.copy(connecting = false, problem = true) }
                return@launch
            }
            try {
                connections.connect(
                    hostId,
                    SshCredential(SshCredentialKind.Password, secret, null),
                    System.currentTimeMillis(),
                ).use { session ->
                    secret.fill('\u0000')
                    if (current != generation) return@use
                    val shell = session.sshTransport.openTerminal()
                    if (current != generation) { shell.close(); return@use }
                    terminal = shell
                    val transcript = SshTerminalTranscript()
                    mutableState.update { it.copy(connecting = false, connected = true) }
                    while (current == generation && currentCoroutineContext().isActive) {
                        val output = shell.read() ?: break
                        if (current != generation) break
                        val visible = transcript.append(output)
                        mutableState.update { it.copy(output = visible) }
                    }
                }
                if (current == generation) mutableState.update { it.copy(connected = false, connecting = false) }
            } catch (_: CancellationException) {
                // Disposing the page closes the PTY and cancels the reader.
            } catch (_: Exception) {
                if (current == generation) mutableState.update {
                    it.copy(connected = false, connecting = false, problem = true)
                }
            } finally {
                secret.fill('\u0000')
                if (current == generation) terminal = null
            }
        }
    }

    fun send(value: String) {
        val shell = terminal ?: return
        val current = generation
        viewModelScope.launch {
            try {
                shell.write(value)
            } catch (_: Exception) {
                if (current == generation) mutableState.update { it.copy(connected = false, problem = true) }
            }
        }
    }

    fun stop() {
        generation++
        terminal?.close()
        terminal = null
        connectionJob?.cancel()
        connectionJob = null
        mutableState.value = SshTerminalUiState()
    }

    override fun onCleared() {
        stop()
        super.onCleared()
    }
}

data class SshTerminalUiState(
    val hostId: String = "",
    val connecting: Boolean = false,
    val connected: Boolean = false,
    val problem: Boolean = false,
    val output: String = "",
)

@Composable
fun SshTerminalScreen(hostId: String, onClose: () -> Unit, modifier: Modifier = Modifier) {
    val target = (LocalContext.current.applicationContext as RelaxKonApplication)
        .container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    val model: SshTerminalViewModel = viewModel()
    val state by model.state.collectAsState()
    var input by remember(hostId) { mutableStateOf("") }
    var concealInput by remember(hostId) { mutableStateOf(false) }
    val scroll = rememberScrollState()
    LaunchedEffect(hostId) { model.start(hostId) }
    DisposableEffect(hostId) { onDispose { model.stop() } }
    LaunchedEffect(state.output) { scroll.scrollTo(scroll.maxValue) }

    fun sendLine() {
        model.send(input + "\r")
        input = ""
    }

    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(
            title = stringResource(R.string.ssh_terminal_title),
            subtitle = target?.let { "${it.sshUserName}@${it.sshHost}:${it.sshPort}" },
            onBack = onClose,
        )
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
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
            if (!state.connecting && !state.connected) OutlinedButton(onClick = { model.start(hostId) }) {
                Text(stringResource(R.string.ssh_terminal_retry))
            }
        }
        Surface(
            modifier = Modifier.fillMaxWidth().weight(1f),
            color = Color(0xFF101820),
            contentColor = Color(0xFFF2F5F7),
            shape = MaterialTheme.shapes.medium,
        ) {
            SelectionContainer {
                Column(Modifier.verticalScroll(scroll).padding(Spacing.md)) {
                    Text(state.output, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton({ model.send("\u0003") }, enabled = state.connected) { Text("Ctrl+C") }
            OutlinedButton({ model.send("\t") }, enabled = state.connected) { Text("Tab") }
            OutlinedButton({ model.send("\u001b[A") }, enabled = state.connected) { Text("↑") }
            OutlinedButton({ model.send("\u001b[B") }, enabled = state.connected) { Text("↓") }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Checkbox(checked = concealInput, onCheckedChange = { concealInput = it })
            Text(stringResource(R.string.ssh_terminal_hide_input), modifier = Modifier.padding(top = Spacing.sm))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedTextField(
                value = input,
                onValueChange = { input = it },
                modifier = Modifier.weight(1f),
                label = { Text(stringResource(R.string.ssh_terminal_input)) },
                singleLine = true,
                enabled = state.connected,
                visualTransformation = if (concealInput) PasswordVisualTransformation() else VisualTransformation.None,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                keyboardActions = KeyboardActions(onSend = { sendLine() }),
            )
            Button(onClick = { sendLine() }, enabled = state.connected, modifier = Modifier.padding(top = Spacing.sm)) {
                Text(stringResource(R.string.ssh_terminal_send))
            }
        }
    }
}
