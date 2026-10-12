package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.TerminalSettings
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.UiMessage
import kotlinx.coroutines.*

internal class TerminalSettingsController(
    private val presentation: TerminalPresentation,
    private val scope: CoroutineScope,
    private val currentOwner: () -> SessionState.Active?,
    private val readSettings: suspend (SessionState.Active) -> ApiResult<TerminalSettings>,
    private val saveSettings: suspend (SessionState.Active, TerminalSettings, TerminalSettings) -> ApiResult<TerminalSettings>,
) {
    private var owner: SessionState.Active? = null
    private var job: Job? = null
    private var generation = 0

    fun bindOwner(active: SessionState.Active?) {
        if (owner === active) return
        generation++; job?.cancel(); job = null; owner = active
        presentation.bindOwner(active)
    }

    fun read(owner: SessionState.Active) { request(owner, false) { readSettings(owner) } }

    fun save(owner: SessionState.Active, value: TerminalSettings) {
        if (!presentation.settingsVerified) return
        val expected = presentation.settings
        request(owner, true) { saveSettings(owner, expected, value) }
    }

    private fun request(active: SessionState.Active, writing: Boolean, action: suspend () -> ApiResult<TerminalSettings>) {
        if (owner !== active || currentOwner() !== active || job?.isActive == true) return
        val version = ++generation
        fun current() = version == generation && owner === active && currentOwner() === active
        fun failed(message: Int) {
            presentation.settingsVerified = false
            presentation.settingsMessage = UiMessage(message)
        }
        presentation.settingsBusy = true
        presentation.settingsMessage = null
        job = scope.launch {
            try {
                val result = action()
                if (!current()) return@launch
                when (result) {
                    is ApiResult.Success -> {
                        presentation.settings = result.value
                        presentation.settingsVerified = true
                        if (writing) {
                            presentation.localFontSize = null
                            presentation.settingsMessage = UiMessage(R.string.terminal_settings_saved, tone = StatusTone.Success)
                        }
                    }
                    else -> failed(if (!writing) R.string.terminal_settings_unavailable
                        else if (result is ApiResult.Problem && result.status < 500) R.string.terminal_settings_changed
                        else R.string.terminal_settings_unknown)
                }
            } catch (cancelled: CancellationException) {
                if (current()) failed(if (writing) R.string.terminal_settings_unknown else R.string.terminal_settings_unavailable)
                throw cancelled
            } catch (_: Exception) {
                if (current()) failed(if (writing) R.string.terminal_settings_unknown else R.string.terminal_settings_unavailable)
            } finally {
                if (current()) presentation.settingsBusy = false
            }
        }
    }
}
