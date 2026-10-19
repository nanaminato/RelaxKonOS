package app.relaxkonos.mobile.ui.manage.firewall

import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*

internal data class FirewallState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val facts: FirewallFacts? = null, val checkedAtMillis: Long? = null, val pending: List<PendingFirewallChange> = emptyList(),
    val problem: String? = null, val saved: Int = 0, val feedbackVersion: Long = 0)
internal class FirewallConfirmation(val owner: SessionState.Active, val expected: FirewallFacts, val change: FirewallChange)
internal class FirewallViewModel(private val session: AuthSession, private val firewall: FirewallRepository,
    private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    var state by mutableStateOf(FirewallState()); private set
    var confirmation by mutableStateOf<FirewallConfirmation?>(null); private set
    fun dismissConfirmation(expected: FirewallConfirmation) {
        if (confirmation === expected && state.owner === expected.owner && session.state.value === expected.owner && !state.busy) {
            confirmation = null
        }
    }
    fun withdrawConfirmation() { confirmation = null }
    fun requestChange(owner: SessionState.Active, expected: FirewallFacts, change: FirewallChange) {
        if (session.state.value !== owner || state.owner !== owner || state.busy || state.facts != expected ||
            !owner.privilegedOperations || !expected.status.isAvailable || state.pending.isNotEmpty()) return
        if (runCatching { change.validate() }.isFailure) return
        if (change.kind in setOf(FirewallChangeKind.Delete, FirewallChangeKind.Replace) &&
            expected.rules.singleOrNull { it.number == change.number } == null) return
        confirmation = FirewallConfirmation(owner, expected, change)
    }
    private var job: Job? = null
    private var generation = 0
    init { viewModelScope.launch { session.state.collect { next ->
        if (next !== state.owner) { withdrawConfirmation(); job?.cancel(); generation++; state = FirewallState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { withdrawConfirmation(); job?.cancel(); generation++; state = state.copy(busy = false, facts = null, checkedAtMillis = null) }
    fun refresh() { withdrawConfirmation(); work { owner -> load(owner) } }
    fun accept(pending: PendingFirewallChange) = work { owner ->
        val result = firewall.acceptFacts(owner, pending); verify(owner); showFacts(owner, result)
    }
    fun change(review: FirewallConfirmation) {
        if (confirmation !== review) return
        withdrawConfirmation()
        if (session.state.value !== review.owner || state.owner !== review.owner || state.facts != review.expected || state.pending.isNotEmpty()) return
        val expected = review.expected; val change = review.change
        work { owner ->
            val outcome = firewall.change(owner, expected, change, elevationAnswers)
            val result = outcome.result
            verify(owner)
            val problem = when (result) {
                is ApiResult.Problem -> result.code
                is ApiResult.Transport -> "firewall.unverified"
                is ApiResult.Success -> result.value.problemCode.takeIf(String::isNotBlank)
            }
            val success = result is ApiResult.Success && result.value.success
            // Commit the authoritative receipt before local recovery-index reads can fail.
            if (success) state = state.copy(saved = state.saved + 1)
            showFacts(owner, outcome.facts)
            val verificationFailed = outcome.facts !is ApiResult.Success || !outcome.facts.value.status.isAvailable
            state = state.copy(problem = when {
                outcome.storageFailed -> "firewall.recovery_storage_failed"
                problem != null -> problem
                success && verificationFailed -> "firewall.refresh_failed"
                else -> state.problem
            })
        }
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = firewall.facts(owner); verify(owner)
        showFacts(owner, result)
    }
    private fun showFacts(owner: SessionState.Active, result: ApiResult<FirewallFacts>) {
        val pending = try { firewall.pending(owner) }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) {
            state = state.copy(facts = null, checkedAtMillis = null, problem = "firewall.recovery_storage_failed")
            return
        }
        state = state.copy(facts = (result as? ApiResult.Success)?.value, checkedAtMillis = System.currentTimeMillis(), pending = pending,
            problem = when (result) { is ApiResult.Problem -> result.code; is ApiResult.Transport -> "firewall.unverified"; is ApiResult.Success -> result.value.status.problemCode.takeIf(String::isNotBlank) })
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.FIREWALL !in owner.capabilities) return
        val request = generation
        state = state.copy(busy = true, problem = null, feedbackVersion = state.feedbackVersion + 1)
        job = viewModelScope.launch {
            try { verify(owner); block(owner) }
            catch (cancelled: CancellationException) {
                if (current(owner, request)) state = state.copy(facts = null, problem = "firewall.unverified")
                throw cancelled
            }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, problem = "firewall.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false,
                pending = runCatching { firewall.pending(owner) }.getOrDefault(state.pending)) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && session.state.value === owner && generation == request
    private suspend fun verify(owner: SessionState.Active) {
        currentCoroutineContext().ensureActive()
        if (state.owner !== owner || session.state.value !== owner) throw CancellationException("Firewall session changed")
    }
}
