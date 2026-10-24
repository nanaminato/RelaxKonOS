package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class FirewallChangeOutcome(val result: ApiResult<FirewallResult>, val facts: ApiResult<FirewallFacts>,
    val storageFailed: Boolean = false)

class FirewallRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val journal: FirewallMutationJournal) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active) = journal.pending(owner.also(::verify))
    suspend fun facts(owner: SessionState.Active): ApiResult<FirewallFacts> {
        val status = call(owner, gateway::firewallStatus)
        if (status !is ApiResult.Success) return failure(status)
        if (!status.value.isAvailable) return ApiResult.Success(FirewallFacts(status.value, emptyList()))
        val rules = call(owner, gateway::firewallRules)
        return if (rules is ApiResult.Success) ApiResult.Success(FirewallFacts(status.value, rules.value)) else failure(rules)
    }
    suspend fun change(owner: SessionState.Active, expected: FirewallFacts, change: FirewallChange,
        provider: ElevationAnswerProvider): FirewallChangeOutcome =
        mutations.withLock {
            verify(owner); require(owner.privilegedOperations && expected.status.isAvailable)
            change.validate(); require(journal.pending(owner).isEmpty())
            val current = facts(owner)
            if (current !is ApiResult.Success) return@withLock FirewallChangeOutcome(failure(current), current)
            if (current.value != expected) return@withLock FirewallChangeOutcome(ApiResult.Problem(409, "firewall.facts_changed", null), current)
            val pending = journal.begin(owner, change.kind, change.number)
            val result = elevations.withElevation("firewallChange", "host/firewall", provider) { url, token ->
                verify(owner)
                val latest = facts(owner)
                if (latest !is ApiResult.Success) return@withElevation failure(latest)
                if (latest.value != expected) return@withElevation ApiResult.Problem(409, "firewall.facts_changed", null)
                gateway.changeFirewall(url, token, change).let {
                    if (it is ApiResult.Success && !it.value.success && it.value.problemCode == "firewall.elevation_required")
                        ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else it
                }
            }
            verify(owner)
            // Only explicit pre-mutation authorization refusals prove that no host change occurred.
            val refused = result is ApiResult.Problem && result.code in setOf(ProblemCodes.ELEVATION_REQUIRED, ProblemCodes.UNAUTHORIZED)
            // The write receipt and its one follow-up read are independent evidence.
            // A failed verification must not erase a known successful write or trigger another read.
            val confirmed = try { facts(owner) }
            catch (cancelled: CancellationException) {
                currentCoroutineContext().ensureActive()
                verify(owner)
                ApiResult.Transport(null)
            } catch (_: Exception) { verify(owner); ApiResult.Transport(null) }
            val complete = refused || result is ApiResult.Success && result.value.success &&
                confirmed is ApiResult.Success && confirmed.value.status.isAvailable
            val storageFailed = if (complete) {
                try { journal.complete(pending); false } catch (_: Exception) { true }
            } else false
            FirewallChangeOutcome(result, confirmed, storageFailed)
        }
    suspend fun acceptFacts(owner: SessionState.Active, pending: PendingFirewallChange): ApiResult<FirewallFacts> = mutations.withLock {
        verify(owner); require(pending in journal.pending(owner))
        facts(owner).also {
            currentCoroutineContext().ensureActive()
            verify(owner)
            if (it is ApiResult.Success && it.value.status.isAvailable) journal.complete(pending)
        }
    }
    private suspend fun <T> call(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return session.authenticated { url, token -> verify(owner); request(url, token) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Firewall session changed")
        require(ServerCapabilities.FIREWALL in owner.capabilities)
    }
    private fun <T> failure(value: ApiResult<*>): ApiResult<T> = when (value) {
        is ApiResult.Problem -> value
        is ApiResult.Transport -> value
        is ApiResult.Success -> error("Expected unavailable result")
    }
}
