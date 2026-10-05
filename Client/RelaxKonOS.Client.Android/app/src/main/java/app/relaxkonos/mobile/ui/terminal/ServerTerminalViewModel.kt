package app.relaxkonos.mobile.ui.terminal

import android.app.Application
import androidx.compose.ui.input.key.*
import androidx.compose.ui.semantics.selected
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.ui.theme.TerminalType
import kotlinx.coroutines.launch
/** Retains a selected session across rotation. Leaving the page only detaches its transport. */
class ServerTerminalViewModel(application: Application) : AndroidViewModel(application) {
    private val controller = ServerTerminalController(
        getApplication<RelaxKonApplication>().container.session, viewModelScope,
        nativeResponses = { getApplication<RelaxKonApplication>().container.appearance.terminalType == TerminalType.Native },
    )
    val state = controller.state
    val presentation = TerminalPresentation()
    private val container get() = getApplication<RelaxKonApplication>().container
    private var settingsJob: kotlinx.coroutines.Job? = null
    private var activeOwner: SessionState.Active? = null
    init {
        viewModelScope.launch { container.session.state.collect { state ->
            val owner = state as? SessionState.Active
            if (activeOwner !== owner) {
                settingsJob?.cancel(); activeOwner = owner; presentation.bindOwner(owner)
                controller.resetOwner()
            }
        } }
    }
    fun connect(owner: SessionState.Active) {
        if (activeOwner !== owner) { settingsJob?.cancel(); activeOwner = owner; presentation.bindOwner(owner) }
        controller.connect(owner)
        if (!presentation.settingsVerified && settingsJob?.isActive != true) readSettings(owner)
    }
    fun readSettings(owner: SessionState.Active) {
        if (settingsJob?.isActive == true) return
        presentation.settingsBusy = true
        settingsJob = viewModelScope.launch {
            try {
                val result = container.terminalSettings.read(owner)
                if (activeOwner !== owner) return@launch
                presentation.settingsVerified = result is app.relaxkonos.mobile.core.net.ApiResult.Success
                if (result is app.relaxkonos.mobile.core.net.ApiResult.Success) { presentation.settings = result.value; presentation.settingsMessage = null }
                else presentation.settingsMessage = app.relaxkonos.mobile.ui.common.UiMessage(R.string.terminal_settings_unavailable)
            } finally { if (activeOwner === owner) presentation.settingsBusy = false }
        }
    }
    fun saveSettings(owner: SessionState.Active, value: app.relaxkonos.mobile.core.net.TerminalSettings) {
        if (settingsJob?.isActive == true || !presentation.settingsVerified) return
        val expected = presentation.settings
        presentation.settingsBusy = true
        settingsJob = viewModelScope.launch {
            try {
                val result = container.terminalSettings.save(owner, expected, value)
                if (activeOwner !== owner) return@launch
                when (result) {
                    is app.relaxkonos.mobile.core.net.ApiResult.Success -> {
                        presentation.settings = result.value; presentation.localFontSize = null; presentation.settingsMessage = app.relaxkonos.mobile.ui.common.UiMessage(R.string.terminal_settings_saved, tone = app.relaxkonos.mobile.ui.common.StatusTone.Success)
                    }
                    else -> {
                        presentation.settingsVerified = false
                        presentation.settingsMessage = app.relaxkonos.mobile.ui.common.UiMessage(
                            if (result is app.relaxkonos.mobile.core.net.ApiResult.Problem && result.status < 500) R.string.terminal_settings_changed else R.string.terminal_settings_unknown)
                    }
                }
            } finally { if (activeOwner === owner) presentation.settingsBusy = false }
        }
    }
    fun attach(id: String?) = controller.attach(id)
    fun send(text: String) = controller.send(text)
    fun resize(columns: Int, rows: Int) = controller.resize(columns, rows)
    fun close(sessionId: String) = controller.close(sessionId)
    fun closeSessions(ids: List<String>) = controller.closeSessions(ids)
    fun clearOutput() = controller.clearOutput()
    fun detach() = controller.detach()
    override fun onCleared() { detach(); super.onCleared() }
}
