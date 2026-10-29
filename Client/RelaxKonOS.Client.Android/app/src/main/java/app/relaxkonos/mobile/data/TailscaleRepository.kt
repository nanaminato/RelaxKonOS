package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

class TailscaleRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    suspend fun status(owner: SessionState.Active): ApiResult<TailscaleStatus> {
        verify(owner)
        return session.authenticated { url, token ->
            verify(owner)
            gateway.tailscaleStatus(url, token)
        }.also { currentCoroutineContext().ensureActive(); verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Tailscale session changed")
        require(ServerCapabilities.TAILSCALE in owner.capabilities)
    }
}
