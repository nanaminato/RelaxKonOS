package app.relaxkonos.mobile.ui.terminal

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
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
    private val settingsController = TerminalSettingsController(presentation, viewModelScope,
        { container.activeSession }, container.terminalSettings::read, container.terminalSettings::save)
    private var activeOwner: SessionState.Active? = null
    init {
        viewModelScope.launch { container.session.state.collect { state ->
            val owner = state as? SessionState.Active
            if (activeOwner !== owner) {
                activeOwner = owner; settingsController.bindOwner(owner)
                controller.resetOwner()
            }
        } }
    }
    fun connect(owner: SessionState.Active) {
        if (activeOwner !== owner) { activeOwner = owner; settingsController.bindOwner(owner) }
        controller.connect(owner)
        if (!presentation.settingsVerified) readSettings(owner)
    }
    fun readSettings(owner: SessionState.Active) = settingsController.read(owner)
    fun saveSettings(owner: SessionState.Active, value: app.relaxkonos.mobile.core.net.TerminalSettings) = settingsController.save(owner, value)
    fun attach(id: String?) = controller.attach(id)
    fun send(sessionId: String?, text: String) = controller.send(sessionId, text)
    fun resize(columns: Int, rows: Int) = controller.resize(columns, rows)
    fun close(sessionId: String) = controller.close(sessionId)
    fun closeSessions(ids: List<String>) = controller.closeSessions(ids)
    fun clearOutput() = controller.clearOutput()
    fun detach() = controller.detach()
    override fun onCleared() { detach(); super.onCleared() }
}
