package app.relaxkonos.mobile.ui.manage.firewall

import android.app.Application
import androidx.compose.runtime.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*

internal data class FirewallState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val facts: FirewallFacts? = null, val checkedAtMillis: Long? = null, val pending: List<PendingFirewallChange> = emptyList(),
    val problem: String? = null, val saved: Int = 0)
internal class FirewallViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(FirewallState()); private set
    private var job: Job? = null
    private var generation = 0
    init { viewModelScope.launch { container.session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; state = FirewallState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = state.copy(busy = false, facts = null, checkedAtMillis = null) }
    fun refresh() = work { owner -> load(owner) }
    fun accept(pending: PendingFirewallChange) = work { owner ->
        container.firewall.acceptFacts(owner, pending); verify(owner); load(owner)
    }
    fun change(expected: FirewallFacts, change: FirewallChange) {
        work { owner ->
            val result = container.firewall.change(owner, expected, change, container.elevationAnswers)
            verify(owner)
            val problem = when (result) {
                is ApiResult.Problem -> result.code
                is ApiResult.Transport -> "firewall.unverified"
                is ApiResult.Success -> result.value.problemCode.takeIf(String::isNotBlank)
            }
            val success = result is ApiResult.Success && result.value.success
            load(owner)
            state = state.copy(problem = problem ?: state.problem, saved = state.saved + if (success) 1 else 0)
        }
    }
    private suspend fun load(owner: SessionState.Active) {
        val result = container.firewall.facts(owner); verify(owner)
        state = state.copy(facts = (result as? ApiResult.Success)?.value, checkedAtMillis = System.currentTimeMillis(), pending = container.firewall.pending(owner),
            problem = when (result) { is ApiResult.Problem -> result.code; is ApiResult.Transport -> "firewall.unverified"; is ApiResult.Success -> result.value.status.problemCode.takeIf(String::isNotBlank) })
    }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.FIREWALL !in owner.capabilities) return
        val request = generation
        state = state.copy(busy = true, problem = null)
        job = viewModelScope.launch {
            try { block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(owner, request)) state = state.copy(facts = null, problem = "firewall.unverified") }
            finally { if (current(owner, request)) state = state.copy(busy = false) }
        }
    }
    private fun current(owner: SessionState.Active, request: Int) = state.owner === owner && container.session.state.value === owner && generation == request
    private fun verify(owner: SessionState.Active) { if (state.owner !== owner || container.session.state.value !== owner) throw CancellationException("Firewall session changed") }
}
