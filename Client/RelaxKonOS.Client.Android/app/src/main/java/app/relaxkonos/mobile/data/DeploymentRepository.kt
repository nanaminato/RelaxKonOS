package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Reads never request elevation or infer management permission from execution eligibility. */
class DeploymentRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    // A list refresh can overlap a detail refresh. Serialize their auth retries so both do not
    // try to rotate the same refresh token when the access token expires.
    private val reads = Mutex()
    suspend fun applications(owner: SessionState.Active): ApiResult<List<DeploymentApplication>> = read(owner) { url, token ->
        gateway.deploymentApplications(url, token)
    }

    suspend fun snapshot(owner: SessionState.Active, id: String): ApiResult<DeploymentSnapshot> = read(owner) { url, token ->
        gateway.deploymentSnapshot(url, token, id)
    }

    suspend fun runtime(owner: SessionState.Active): ApiResult<DeploymentRuntime> = read(owner) { url, token ->
        gateway.deploymentRuntime(url, token)
    }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = reads.withLock {
        fun verifyOwner() {
            if (session.state.value !== owner) throw CancellationException("Deployment session changed")
        }
        verifyOwner()
        val result = session.authenticated { url, token ->
            verifyOwner() // Also guards the retry after a token refresh.
            call(url, token)
        }
        verifyOwner()
        result
    }
}
