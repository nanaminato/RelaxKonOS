package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ProcessKillResult
import kotlinx.coroutines.CancellationException
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.PerformanceInfo
import app.relaxkonos.mobile.core.net.NetworkAddress
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ProcessSort
import app.relaxkonos.mobile.core.net.ProcessPage
import app.relaxkonos.mobile.core.net.RelaxKonGateway

/** Host metrics and the process list, both served by the serialized SystemMonitor endpoints. */
class SystemRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
) {
    suspend fun performance(): ApiResult<PerformanceSnapshot> =
        owned { serverUrl, accessToken -> gateway.performanceSnapshot(serverUrl, accessToken) }

    suspend fun performance(owner: SessionState.Active): ApiResult<PerformanceSnapshot> =
        owned(owner) { url, token -> gateway.performanceSnapshot(url, token) }
    suspend fun performanceInfo(owner: SessionState.Active): ApiResult<PerformanceInfo> =
        owned(owner) { url, token -> gateway.performanceInfo(url, token) }
    suspend fun performanceHistory(owner: SessionState.Active): ApiResult<List<PerformanceSnapshot>> =
        owned(owner) { url, token -> gateway.performanceHistory(url, token) }
    suspend fun networkAddresses(owner: SessionState.Active): ApiResult<List<NetworkAddress>> =
        owned(owner) { url, token -> gateway.networkAddresses(url, token) }

    suspend fun processes(owner: SessionState.Active, page: Int, pageSize: Int, filter: String?, sort: ProcessSort, descending: Boolean): ApiResult<ProcessPage> =
        owned(owner) { serverUrl, accessToken ->
            gateway.queryProcesses(serverUrl, accessToken, page, pageSize, filter, sort, descending)
        }

    /** This endpoint uses host OS permissions, not the native-service elevation grant. */
    suspend fun killProcess(owner: SessionState.Active, pid: Int, expectedStartTime: String): ApiResult<ProcessKillResult> {
        return owned(owner) { url, token -> gateway.killProcess(url, token, pid, expectedStartTime) }
    }
    private suspend fun <T> owned(call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        val owner = session.state.value as? SessionState.Active ?: return ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null)
        return owned(owner, call)
    }
    private suspend fun <T> owned(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        fun guard() { if (session.state.value !== owner) throw CancellationException("System observer owner changed") }
        guard()
        return session.authenticated { url, token -> guard(); call(url, token).also { guard() } }.also { guard() }
    }
}
