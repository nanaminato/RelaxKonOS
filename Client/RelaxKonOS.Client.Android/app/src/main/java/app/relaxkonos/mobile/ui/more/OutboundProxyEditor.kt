package app.relaxkonos.mobile.ui.more

import androidx.compose.runtime.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DockerRepository
import app.relaxkonos.mobile.ui.common.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
internal sealed interface ProxyReview {
    data class Save(val settings: OutboundProxySettings) : ProxyReview
    data object Clear : ProxyReview
    data object Refresh : ProxyReview
    data object Leave : ProxyReview
}

/** In-memory form lifetime follows the page and exact authenticated owner, including its jobs. */
internal class OutboundProxyEditor(
    private val repository: DockerRepository,
    private val owner: SessionState.Active,
    private val isCurrentOwner: () -> Boolean,
    private val scope: CoroutineScope,
) {
    var status by mutableStateOf<OutboundProxyStatus?>(null)
        private set
    var draft by mutableStateOf<OutboundProxySettings?>(null)
        private set
    var busy by mutableStateOf(false)
        private set
    var message by mutableStateOf<UiMessage?>(null)
        private set
    var review by mutableStateOf<ProxyReview?>(null)
        private set
    private var activeJob: Job? = null
    private var closed = false
    private var navigation: (() -> Unit)? = null
    fun close() {
        closed = true
        activeJob?.cancel()
        status = null
        draft = null
        review = null
        message = null
        navigation = null
    }
    val dirty: Boolean get() = draft != null && draft != status?.settings
    val canSubmit: Boolean get() = owner.privilegedOperations && status != null && draft != null && !busy && review == null

    fun change(transform: (OutboundProxySettings) -> OutboundProxySettings) {
        if (canSubmit) draft = draft?.let(transform)
    }
    fun selectManaged() {
        if (ServerCapabilities.PROXY in owner.capabilities) change { it.copy(source = OutboundProxySource.ManagedProxy, httpProxy = "", httpsProxy = "") }
    }
    fun refresh() {
        if (busy) return
        if (dirty) review = ProxyReview.Refresh else load()
    }
    fun load() = call(false) { repository.proxyStatus(owner) }
    fun save() { if (canSubmit) review = ProxyReview.Save(requireNotNull(draft)) }
    fun clear() { if (canSubmit) review = ProxyReview.Clear }
    fun leave(onBack: () -> Unit) { if (!busy) { if (dirty) { navigation = onBack; review = ProxyReview.Leave } else onBack() } }
    fun dismissMessage() { message = null }
    fun dismiss() { review = null; navigation = null }
    fun confirm(onBack: (() -> Unit)?) {
        if (closed || busy || !isCurrentOwner()) return
        val pending = review ?: return
        val target = navigation; navigation = null
        review = null
        when (pending) {
            is ProxyReview.Save -> call(true) { repository.saveProxy(owner, pending.settings, confirmed = true) }
            ProxyReview.Clear -> call(true) { repository.clearProxy(owner) }
            ProxyReview.Refresh -> load()
            ProxyReview.Leave -> (target ?: onBack)?.invoke()
        }
    }
    private fun call(write: Boolean, action: suspend () -> ApiResult<OutboundProxyStatus>) {
        if (closed || busy || !isCurrentOwner()) return
        busy = true
        message = null
        activeJob = scope.launch {
            try {
                val result = action()
                if (closed || !isCurrentOwner()) return@launch
                when (result) {
                    is ApiResult.Success -> {
                        status = result.value
                        draft = result.value.settings
                        if (write) message = UiMessage(R.string.proxy_saved, tone = StatusTone.Success)
                    }
                    is ApiResult.Problem -> {
                        message = proxyProblem(result.code)
                        // A failed read or write requires a fresh host snapshot before another change.
                        status = null
                    }
                    is ApiResult.Transport -> {
                        status = null
                        message = UiMessage(if (write) R.string.proxy_result_unknown else R.string.error_connectivity)
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (!closed && isCurrentOwner()) {
                    status = null
                    message = UiMessage(if (write) R.string.proxy_result_unknown else R.string.error_generic)
                }
            } finally {
                if (!closed && isCurrentOwner()) busy = false
            }
        }
    }
}
