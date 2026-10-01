package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** A cursor belongs to one immutable query. Read observation never replays an alert action. */
data class EventAlertBrowserState(
    val owner: SessionState.Active? = null, val active: Boolean = false, val eventsMode: Boolean = false,
    val alertQuery: AlertQuery = AlertQuery(), val eventQuery: EventQuery = EventQuery(),
    val loading: Boolean = false, val alerts: List<OperationalAlert> = emptyList(), val events: List<OperationalEvent> = emptyList(),
    val nextCursor: String? = null, val pagesLoaded: Int = 0, val readResult: ApiResult<*>? = null, val summary: ApiResult<EventAlertSummary>? = null,
    val selectedId: String? = null, val detailLoading: Boolean = false, val detail: ApiResult<OperationalAlertDetail>? = null,
    val actionBusy: Boolean = false, val actionResult: ApiResult<OperationalAlertDetail>? = null, val unknown: Boolean = false,
    val checkedAtMillis: Long? = null,
) {
    val atLimit: Boolean get() = (if (eventsMode) events.size else alerts.size) >= 500
}

class EventAlertBrowser(private val repository: EventAlertRepository, private val scope: CoroutineScope) {
    private val mutableState = MutableStateFlow(EventAlertBrowserState())
    val state = mutableState.asStateFlow()
    private var generation = 0
    private var selection = 0
    private var actionVersion = 0
    private var pageJob: Job? = null
    private var detailJob: Job? = null
    private var pollJob: Job? = null
    private var actionJob: Job? = null

    fun activate(owner: SessionState.Active) {
        if (mutableState.value.owner !== owner) {
            stop(); mutableState.value = EventAlertBrowserState(owner = owner, active = true)
        } else mutableState.update { it.copy(active = true) }
        refresh()
        pollJob?.cancel()
        pollJob = scope.launch {
            while (isActive && current(owner)) {
                delay(15_000)
                if (!mutableState.value.loading && !mutableState.value.actionBusy && mutableState.value.pagesLoaded <= 1) refresh()
            }
        }
    }
    fun stop() {
        generation++; selection++; actionVersion++
        pageJob?.cancel(); detailJob?.cancel(); pollJob?.cancel(); actionJob?.cancel()
        mutableState.update { it.copy(active = false, loading = false, detailLoading = false, actionBusy = false, alerts = emptyList(), events = emptyList(), detail = null, summary = null, readResult = null, nextCursor = null, pagesLoaded = 0, checkedAtMillis = null) }
    }
    fun filters(eventsMode: Boolean, alertQuery: AlertQuery, eventQuery: EventQuery) {
        if (mutableState.value.actionBusy) return
        generation++; selection++; pageJob?.cancel(); detailJob?.cancel()
        mutableState.update { it.copy(eventsMode = eventsMode, alertQuery = alertQuery, eventQuery = eventQuery,
            alerts = emptyList(), events = emptyList(), nextCursor = null, pagesLoaded = 0, selectedId = null, detail = null, actionResult = null, unknown = false) }
        refresh()
    }
    fun refresh() = load(more = false)
    fun more() = load(more = true)
    private fun load(more: Boolean) {
        val before = mutableState.value; val owner = before.owner ?: return
        if (!current(owner) || before.actionBusy || more && (before.loading || before.nextCursor == null || before.atLimit)) return
        pageJob?.cancel()
        val revision = ++generation
        val cursor = if (more) before.nextCursor else null
        mutableState.update { it.copy(loading = true, readResult = null) }
        pageJob = scope.launch {
            try {
                val summary = repository.summary(owner)
                if (!current(owner) || generation != revision) return@launch
                mutableState.update { it.copy(summary = summary) }
                if (before.eventsMode) {
                    val result = repository.events(owner, cursor, before.eventQuery)
                    if (!current(owner) || generation != revision) return@launch
                    mutableState.update { state -> if (result is ApiResult.Success) state.copy(events =
                        ((if (more) state.events else emptyList()) + result.value.items).distinctBy { it.id }.take(500),
                        pagesLoaded = if (more) state.pagesLoaded + 1 else 1, nextCursor = result.value.nextCursor.takeUnless { it == cursor && cursor != null }, readResult = result, checkedAtMillis = System.currentTimeMillis())
                        else state.copy(readResult = result) }
                } else {
                    val result = repository.page(owner, cursor, before.alertQuery)
                    if (!current(owner) || generation != revision) return@launch
                    mutableState.update { state -> if (result is ApiResult.Success) state.copy(alerts =
                        ((if (more) state.alerts else emptyList()) + result.value.items).distinctBy { it.id }.take(500),
                        pagesLoaded = if (more) state.pagesLoaded + 1 else 1, nextCursor = result.value.nextCursor.takeUnless { it == cursor && cursor != null }, readResult = result, checkedAtMillis = System.currentTimeMillis())
                        else state.copy(readResult = result) }
                    mutableState.value.selectedId?.let { select(it) }
                }
            } finally { if (current(owner) && generation == revision) mutableState.update { it.copy(loading = false) } }
        }
    }
    fun select(id: String) {
        val owner = mutableState.value.owner ?: return
        if (!current(owner) || mutableState.value.actionBusy) return
        detailJob?.cancel(); val version = ++selection
        mutableState.update { it.copy(selectedId = id, detailLoading = true, detail = null, actionResult = null, unknown = repository.hasUnknown(owner, id)) }
        detailJob = scope.launch {
            try {
                val result = repository.detail(owner, id)
                if (current(owner) && selection == version) mutableState.update { it.copy(detail = result) }
            } finally { if (current(owner) && selection == version) mutableState.update { it.copy(detailLoading = false) } }
        }
    }
    fun reconcile() {
        val owner = mutableState.value.owner ?: return; val id = mutableState.value.selectedId ?: return
        if (!current(owner) || mutableState.value.actionBusy) return
        detailJob?.cancel(); val version = ++selection
        mutableState.update { it.copy(detailLoading = true) }
        detailJob = scope.launch {
            try {
                val result = repository.reconcile(owner, id)
                if (current(owner) && selection == version) mutableState.update { it.copy(detail = result, unknown = repository.hasUnknown(owner, id), actionResult = null) }
            } finally { if (current(owner) && selection == version) mutableState.update { it.copy(detailLoading = false) } }
        }
    }
    fun mutate(baseline: OperationalAlert, action: AlertMutation, reason: String?, expiry: Long?) {
        val owner = mutableState.value.owner ?: return
        if (!current(owner) || mutableState.value.selectedId != baseline.id || mutableState.value.actionBusy || mutableState.value.unknown) return
        detailJob?.cancel(); pageJob?.cancel(); generation++; selection++
        val actionRevision = ++actionVersion
        mutableState.update { it.copy(actionBusy = true, actionResult = null, loading = false, detailLoading = false) }
        actionJob = scope.launch {
            try {
                val result = repository.mutate(owner, baseline, action, reason, expiry)
                if (!current(owner) || actionVersion != actionRevision || mutableState.value.selectedId != baseline.id) return@launch
                mutableState.update { state -> state.copy(actionResult = result.result, unknown = result.mayHaveApplied,
                    detail = if (result.result is ApiResult.Success) result.result else state.detail,
                    alerts = if (result.result is ApiResult.Success) state.alerts.map { if (it.id == baseline.id) result.result.value.alert else it } else state.alerts) }
            } finally { if (current(owner) && actionVersion == actionRevision) mutableState.update { it.copy(actionBusy = false) } }
        }
    }
    private fun current(owner: SessionState.Active): Boolean = mutableState.value.owner === owner && mutableState.value.active
}
