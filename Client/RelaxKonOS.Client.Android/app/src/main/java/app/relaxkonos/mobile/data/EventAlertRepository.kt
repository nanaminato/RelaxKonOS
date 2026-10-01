package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Alerts are server owned. Acknowledgement changes read state, never fault resolution. */
class EventAlertRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val requests = Mutex()
    suspend fun page(owner: SessionState.Active, cursor: String? = null, query: AlertQuery = AlertQuery()) =
        call(owner) { url, token -> gateway.alerts(url, token, cursor, query) }
    suspend fun events(owner: SessionState.Active, cursor: String? = null, query: EventQuery = EventQuery()) =
        call(owner) { url, token -> gateway.operationalEvents(url, token, cursor, query) }
    suspend fun summary(owner: SessionState.Active) = call(owner) { url, token -> gateway.eventAlertSummary(url, token) }
    suspend fun detail(owner: SessionState.Active, id: String): ApiResult<app.relaxkonos.mobile.core.net.OperationalAlertDetail> =
        call(owner) { url, token -> gateway.alertDetail(url, token, id) }.let {
            if (it is ApiResult.Success && it.value.alert.id != id) ApiResult.Transport(null) else it
        }
    private val mutations = Mutex()
    @Volatile private var mutationOwner: SessionState.Active? = null
    private val unknown = java.util.Collections.synchronizedMap(mutableMapOf<String, String>())
    fun hasUnknown(owner: SessionState.Active, id: String): Boolean = mutationOwner === owner && id in unknown

    suspend fun reconcile(owner: SessionState.Active, id: String): ApiResult<OperationalAlertDetail> = mutations.withLock {
        val result = detail(owner, id)
        if (result is ApiResult.Success && mutationOwner === owner) if (result.value.alert.status == unknown[id]) unknown.remove(id)
        result
    }

    /** No idempotency/cancel contract exists for synchronous alert actions. Unknown writes are not replayed. */
    suspend fun mutate(owner: SessionState.Active, baseline: OperationalAlert, action: AlertMutation,
        reason: String? = null, expiresAtMillis: Long? = null): AlertMutationOutcome = mutations.withLock {
        if (session.state.value !== owner) throw CancellationException("Alert session changed")
        if (mutationOwner !== owner) { mutationOwner = owner; unknown.clear() }
        if (baseline.id in unknown) return@withLock AlertMutationOutcome(ApiResult.Problem(409, "event-alerts.result_unknown", null), true)
        if (!canMutateAlert(baseline, action) || reason?.length?.let { it > 512 } == true ||
            action in setOf(AlertMutation.Resolve, AlertMutation.Suppress) && reason.isNullOrBlank() ||
            action == AlertMutation.Suppress && (expiresAtMillis == null || expiresAtMillis <= System.currentTimeMillis() + 60_000))
            return@withLock AlertMutationOutcome(ApiResult.Problem(400, "event-alerts.invalid_request", null), false)
        var sent = false; var finished = false
        try {
            val result = call(owner) { url, token ->
                when (val before = gateway.alertDetail(url, token, baseline.id)) {
                    is ApiResult.Success -> if (before.value.alert != baseline) return@call ApiResult.Problem(409, "event-alerts.invalid_transition", null)
                    is ApiResult.Problem -> return@call before
                    is ApiResult.Transport -> return@call before
                }
                if (session.state.value !== owner) throw CancellationException("Alert session changed")
                sent = true
                when (val receipt = gateway.mutateAlert(url, token, baseline.id, action, reason, expiresAtMillis?.let(IsoInstant::fromEpochMillis))) {
                    is ApiResult.Success -> {
                        if (session.state.value !== owner) throw CancellationException("Alert session changed")
                        val expected = expectedAlertStatus(action)
                        if (receipt.value.id != baseline.id || receipt.value.type != baseline.type || receipt.value.status != expected)
                            return@call ApiResult.Transport("Unexpected alert receipt.")
                        when (val after = gateway.alertDetail(url, token, baseline.id)) {
                            is ApiResult.Success -> if (after.value.alert == receipt.value) after else ApiResult.Transport("Alert readback changed.")
                            else -> ApiResult.Transport("Alert readback unavailable.")
                        }
                    }
                    is ApiResult.Problem -> receipt
                    is ApiResult.Transport -> receipt
                }
            }
            val uncertain = sent && (result is ApiResult.Transport || result is ApiResult.Problem && result.status >= 500)
            if (uncertain) unknown[baseline.id] = expectedAlertStatus(action)
            finished = true
            AlertMutationOutcome(result, uncertain)
        } finally {
            if (sent && !finished && session.state.value === owner) unknown[baseline.id] = expectedAlertStatus(action)
        }
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

data class AlertMutationOutcome(val result: ApiResult<OperationalAlertDetail>, val mayHaveApplied: Boolean)

/** Closed current Server catalog; unknown sources never get a manual-resolution button. */
fun canMutateAlert(alert: OperationalAlert, action: AlertMutation): Boolean = when (action) {
    AlertMutation.Acknowledge -> alert.status == "open"
    AlertMutation.RemoveSuppression -> alert.status == "suppressed"
    AlertMutation.Suppress -> alert.status in setOf("open", "acknowledged")
    AlertMutation.Resolve -> alert.status in setOf("open", "acknowledged") && alert.type in setOf(
        "deployment.operation_failed", "backup.definition_failed", "docker.engine_unavailable", "docker.operation_failed", "tunnel.disconnected", "event-center.source_degraded")
}

private fun expectedAlertStatus(action: AlertMutation): String = when (action) {
    AlertMutation.Acknowledge -> "acknowledged"
    AlertMutation.Resolve -> "resolved"
    AlertMutation.Suppress -> "suppressed"
    AlertMutation.RemoveSuppression -> "open"
}
