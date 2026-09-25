package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ProcessPage
import app.relaxkonos.mobile.core.net.RelaxKonGateway

/** Host metrics and the process list, both served by the serialized SystemMonitor endpoints. */
class SystemRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
) {
    suspend fun performance(): ApiResult<PerformanceSnapshot> =
        session.authenticated { serverUrl, accessToken -> gateway.performanceSnapshot(serverUrl, accessToken) }

    suspend fun processes(page: Int, pageSize: Int, filter: String?): ApiResult<ProcessPage> =
        session.authenticated { serverUrl, accessToken ->
            gateway.queryProcesses(serverUrl, accessToken, page, pageSize, filter)
        }

    /**
     * Ends a process.
     *
     * Killing a process the signed-in account does not own is an `elevation-required` operation, and
     * the elevation target is the process id because that is what the server authorizes.
     */
    suspend fun killProcess(
        pid: Int,
        force: Boolean,
        elevations: ElevationRepository,
        provider: ElevationAnswerProvider,
    ): ApiResult<Unit> = elevations.withElevation(
        capability = HOST_CAPABILITY_NATIVE_SERVICE_ACTION,
        target = pid.toString(),
        provider = provider,
    ) { serverUrl, accessToken -> gateway.killProcess(serverUrl, accessToken, pid, force) }

    private companion object {
        /** Mirrors `HostElevationCapability.NativeServiceAction`. */
        const val HOST_CAPABILITY_NATIVE_SERVICE_ACTION = "nativeServiceAction"
    }
}
