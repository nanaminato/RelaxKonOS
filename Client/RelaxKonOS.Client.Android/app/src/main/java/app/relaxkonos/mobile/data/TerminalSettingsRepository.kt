package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class TerminalSettingsRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val writes = Mutex()
    suspend fun read(owner: SessionState.Active): ApiResult<TerminalSettings> = owned(owner) { url, token -> gateway.terminalSettings(url, token, owner.workspaceId) }
    suspend fun save(owner: SessionState.Active, expected: TerminalSettings, requested: TerminalSettings): ApiResult<TerminalSettings> = writes.withLock {
        TerminalSettingsWire.validate(requested)
        when (val latest = read(owner)) {
            is ApiResult.Success -> if (latest.value != expected) return@withLock ApiResult.Problem(409, "terminal.settings.changed", null)
            is ApiResult.Problem -> return@withLock latest
            is ApiResult.Transport -> return@withLock latest
        }
        when (val result = owned(owner) { url, token ->
            when (val latest = gateway.terminalSettings(url, token, owner.workspaceId)) {
                is ApiResult.Success -> if (latest.value != expected) ApiResult.Problem(409, "terminal.settings.changed", null)
                    else gateway.saveTerminalSettings(url, token, owner.workspaceId, requested)
                is ApiResult.Problem -> latest
                is ApiResult.Transport -> latest
            }
        }) {
            is ApiResult.Success -> when (val verified = read(owner)) {
                is ApiResult.Success -> if (verified.value == result.value && result.value == requested) verified else ApiResult.Transport("Terminal settings receipt did not match.")
                else -> ApiResult.Transport("Terminal settings read-back unavailable.")
            }
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }
    private suspend fun <T> owned(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        fun guard() { if (session.state.value !== owner) throw CancellationException("Terminal settings owner changed.") }
        guard(); return session.authenticated { url, token -> guard(); call(url, token).also { guard() } }.also { guard() }
    }
}
