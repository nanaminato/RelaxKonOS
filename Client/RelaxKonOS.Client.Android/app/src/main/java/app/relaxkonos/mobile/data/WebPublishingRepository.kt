package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Session-scoped facade for AD05 diagnostics. It never asks for elevation and cannot modify host state. */
class WebPublishingRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val reads = Mutex()

    suspend fun servers(owner: SessionState.Active) = read(owner) { url, token -> gateway.webServers(url, token) }
    suspend fun status(owner: SessionState.Active, id: String) = read(owner) { url, token -> gateway.webServerStatus(url, token, id) }
    suspend fun configTest(owner: SessionState.Active, id: String) = read(owner) { url, token -> gateway.webServerConfigTest(url, token, id) }
    suspend fun sites(owner: SessionState.Active, id: String) = read(owner) { url, token -> gateway.webServerSites(url, token, id) }
    suspend fun certificates(owner: SessionState.Active) = read(owner) { url, token -> gateway.certificates(url, token) }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = reads.withLock {
        fun verifyOwner() { if (session.state.value !== owner) throw CancellationException("Web publishing session changed") }
        verifyOwner()
        val result = session.authenticated { url, token -> verifyOwner(); call(url, token) }
        verifyOwner()
        result
    }
}
