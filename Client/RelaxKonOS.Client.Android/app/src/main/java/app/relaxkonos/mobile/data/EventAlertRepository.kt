package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Alerts are server owned. Acknowledgement changes read state, never fault resolution. */
class EventAlertRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val requests = Mutex()
    suspend fun page(owner: SessionState.Active, cursor: String? = null) =
        call(owner) { url, token -> gateway.alerts(url, token, cursor) }
    suspend fun detail(owner: SessionState.Active, id: String): ApiResult<app.relaxkonos.mobile.core.net.OperationalAlertDetail> =
        call(owner) { url, token -> gateway.alertDetail(url, token, id) }.let {
            if (it is ApiResult.Success && it.value.alert.id != id) ApiResult.Transport(null) else it
        }
    suspend fun acknowledge(owner: SessionState.Active, id: String): ApiResult<app.relaxkonos.mobile.core.net.OperationalAlert> =
        call(owner) { url, token -> gateway.acknowledgeAlert(url, token, id) }.let {
            if (it is ApiResult.Success && it.value.id != id) ApiResult.Transport(null) else it
        }

    private suspend fun <T> call(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> =
        requests.withLock {
            fun verify() { if (session.state.value !== owner) throw CancellationException("Alert session changed") }
            verify()
            val result = session.authenticated { url, token -> verify(); request(url, token) }
            verify()
            result
        }
}
