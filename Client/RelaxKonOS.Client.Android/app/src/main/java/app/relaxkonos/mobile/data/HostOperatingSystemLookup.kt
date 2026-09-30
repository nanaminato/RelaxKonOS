package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import app.relaxkonos.mobile.servercenter.ServerInstallationId
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

/**
 * Refreshes the last observed system whenever a list opens or a session becomes active.
 *
 * An endpoint may change systems. Known and unknown answers are queried again, and failures keep the
 * last observed answer. A managed installation is queried only through its active, verified tunnel.
 */
class HostOperatingSystemLookup(
    private val profiles: ConnectionProfileStore,
    private val gateway: RelaxKonGateway,
) {
    private val observed = MutableStateFlow(profiles.all().associate { it.serviceId to it.hostOperatingSystem })
    val systems = observed.asStateFlow()
    private val sequence = AtomicLong()
    private val requests = ConcurrentHashMap<String, Long>()
    private val permits = Semaphore(MAXIMUM_PARALLEL_LOOKUPS)

    /**
     * Queries each service once. Password records without profiles receive a live, non-persisted mark.
     */
    suspend fun resolve(serviceIds: List<String>, active: SessionState.Active? = null): Int {
        val pending = serviceIds.distinct().mapNotNull { serviceId ->
            val serverUrl = if (active?.serviceId == serviceId) active.effectiveBaseUrl
                else serviceId.takeUnless(ServerInstallationId::isValid) ?: return@mapNotNull null
            val request = sequence.incrementAndGet()
            requests[serviceId] = request
            Triple(serviceId, serverUrl, request)
        }
        if (pending.isEmpty()) return 0

        // Opening a list is not a reason to have a socket per saved server open at once. The limit is
        // shared by all screens. A newer request supersedes an older answer for the same service.
        return coroutineScope {
            pending
                .map { (serviceId, serverUrl, request) ->
                    async {
                        permits.withPermit {
                            val answer = gateway.hostOperatingSystem(serverUrl)
                            if (answer !is ApiResult.Success || requests[serviceId] != request) return@withPermit false
                            val changed = observed.value[serviceId] != answer.value
                            observed.update { it + (serviceId to answer.value) }
                            profiles.setHostOperatingSystem(serviceId, answer.value) || changed
                        }
                    }
                }
                .awaitAll()
                .count { it }
        }
    }

    private companion object {
        const val MAXIMUM_PARALLEL_LOOKUPS = 4
    }
}
