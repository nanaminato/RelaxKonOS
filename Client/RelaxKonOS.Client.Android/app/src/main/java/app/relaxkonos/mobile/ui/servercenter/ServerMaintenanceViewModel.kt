package app.relaxkonos.mobile.ui.servercenter

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.common.UiMessage
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
internal class MaintenanceReview internal constructor(val hostId: String, val kind: ServerDeploymentKind, val snapshot: ServerHostSnapshot)

internal fun maintenanceRecoveryMatches(pending: MaintenancePending, receipt: ServerDeploymentOperation) =
    receipt.operationId == pending.reference.operationId && receipt.kind == pending.kind && receipt.schemaVersion == ServerDeploymentProtocol.VERSION &&
        receipt.state in setOf(ServerDeploymentState.Succeeded, ServerDeploymentState.Failed, ServerDeploymentState.Cancelled, ServerDeploymentState.Interrupted)

internal class MaintenanceConfiguration internal constructor(val hostId: String, val snapshot: ServerHostSnapshot, val componentsOnly: Boolean) {
    val fields = ServerConfigurationFields(snapshot)
}

internal data class MaintenanceState(val pending: MaintenancePending? = null, val recovered: ServerDeploymentOperation? = null, val verifiedMutation: ServerDeploymentOperation? = null, val configuration: MaintenanceConfiguration? = null, val configurationVersion: Long = 0, val hostId: String? = null, val review: MaintenanceReview? = null, val busy: Boolean = false, val snapshot: ServerHostSnapshot? = null,
    val probe: ServerHostProbe? = null, val error: UiMessage? = null, val complete: Boolean = false,
    val progress: Int = R.string.server_progress_connecting, val firewallStatus: String? = null, val feedbackVersion: Long = 0)

internal fun maintenanceResultValid(kind: ServerDeploymentKind, snapshot: ServerHostSnapshot): Boolean = when (kind) {
    ServerDeploymentKind.Uninstall -> !snapshot.installed
    ServerDeploymentKind.Firewall -> snapshot.installed
    ServerDeploymentKind.Repair, ServerDeploymentKind.Configure -> snapshot.installed && snapshot.healthy
    else -> true
}

internal class ServerMaintenanceViewModel(private val actions: MaintenanceActions, private val journal: MaintenancePendingJournal,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO) : ViewModel() {
    private val mutable = MutableStateFlow(MaintenanceState())
    val state = mutable.asStateFlow()
    private var feedbackSequence = 0L
    private val pendingByHost = java.util.concurrent.ConcurrentHashMap<String, MaintenancePending>()
    private var reviewActive = false
    private var configurationSequence = 0L
    fun setReviewActive(active: Boolean) {
        reviewActive = active
        if (!active) { clearReview(); invalidateConfiguration() }
    }
    fun invalidateConfiguration() {
        mutable.value.configuration?.fields?.discard?.value = false
        mutable.value = mutable.value.copy(configurationVersion = ++configurationSequence)
    }
    fun clearConfiguration() { mutable.value = mutable.value.copy(configuration = null, configurationVersion = ++configurationSequence) }
    fun openConfiguration(hostId: String, componentsOnly: Boolean) {
        if (!reviewActive || !writable(hostId) || mutable.value.configuration != null || mutable.value.review != null) return
        mutable.value = mutable.value.copy(configuration = MaintenanceConfiguration(hostId, requireNotNull(mutable.value.snapshot), componentsOnly),
            configurationVersion = ++configurationSequence)
    }
    fun dismissConfiguration(editor: MaintenanceConfiguration, version: Long): Boolean {
        if (!reviewActive || mutable.value.configuration !== editor || mutable.value.configurationVersion != version || mutable.value.busy) return false
        clearConfiguration(); return true
    }
    fun check(hostId: String, sudo: String = "") {
        if (mutable.value.configuration != null) return
        perform(MaintenanceRequest(hostId, ServerDeploymentKind.Status, sudo = sudo))
    }
    fun recover(pending: MaintenancePending) {
        val hostId = pending.reference.hostId
        if (pendingByHost[hostId] !== pending || mutable.value.busy || mutable.value.hostId != hostId) return
        perform(MaintenanceRequest(hostId, ServerDeploymentKind.Status, recovery = pending))
    }
    private fun writable(hostId: String) = pendingByHost[hostId] == null && !mutable.value.busy && mutable.value.hostId == hostId && mutable.value.snapshot?.installed == true
    fun configure(editor: MaintenanceConfiguration, version: Long, draft: ServerConfigurationDraft, sudo: String): Boolean {
        if (!reviewActive || mutable.value.configuration !== editor || mutable.value.configurationVersion != version ||
            !writable(editor.hostId) || mutable.value.snapshot != editor.snapshot || draft.reviewed != editor.snapshot) return false
        clearConfiguration()
        perform(MaintenanceRequest(editor.hostId, ServerDeploymentKind.Configure, sudo = sudo, configuration = draft, reviewedSnapshot = editor.snapshot))
        return true
    }
    fun firewall(hostId: String, removeRules: Boolean, sudo: String) {
        if (!reviewActive || !writable(hostId) || mutable.value.configuration != null || mutable.value.review != null) return
        perform(MaintenanceRequest(hostId, ServerDeploymentKind.Firewall, sudo = sudo,
            removeFirewallRules = removeRules, reviewedSnapshot = mutable.value.snapshot))
    }
    fun requestReview(hostId: String, kind: ServerDeploymentKind) {
        if (!reviewActive || !writable(hostId) || mutable.value.configuration != null || kind !in setOf(ServerDeploymentKind.Repair, ServerDeploymentKind.Uninstall)) return
        mutable.value = mutable.value.copy(review = MaintenanceReview(hostId, kind, requireNotNull(mutable.value.snapshot)))
    }
    fun clearReview() { mutable.value = mutable.value.copy(review = null) }
    fun dismissReview(review: MaintenanceReview): Boolean {
        if (mutable.value.review !== review) return false
        clearReview(); return true
    }
    fun submitReview(review: MaintenanceReview, purge: Boolean = false, sudo: String = "", repairCertificate: Boolean = false,
        certificateIdentities: String = "", addFirewallRule: Boolean = false, removeComponents: String = "", repairPackagePath: String = ""): Boolean {
        if (!reviewActive || !writable(review.hostId) || mutable.value.review !== review || mutable.value.snapshot != review.snapshot) return false
        mutable.value = mutable.value.copy(review = null)
        perform(MaintenanceRequest(review.hostId, review.kind, purge, sudo, repairCertificate, certificateIdentities,
            addFirewallRule, removeComponents, repairPackagePath = repairPackagePath, reviewedSnapshot = review.snapshot))
        return true
    }
    private fun perform(request: MaintenanceRequest) {
        if (mutable.value.busy || request.kind != ServerDeploymentKind.Status && pendingByHost[request.hostId] != null) return
        mutable.value = mutable.value.copy(pending = pendingByHost[request.hostId], recovered = null, hostId = request.hostId, review = null, configuration = null, configurationVersion = ++configurationSequence, busy = true, error = null, complete = false, firewallStatus = null,
            progress = R.string.server_progress_connecting)
        viewModelScope.launch {
            var mutationStarted = false
            try {
                val restored = withContext(ioDispatcher) { journal.load(request.hostId) }
                if (restored != pendingByHost[request.hostId]) {
                    if (restored == null) pendingByHost.remove(request.hostId) else pendingByHost[request.hostId] = restored
                }
                mutable.update { it.copy(pending = pendingByHost[request.hostId]) }
                if (request.kind != ServerDeploymentKind.Status && restored != null ||
                    request.recovery != null && restored != request.recovery) {
                    mutable.value = MaintenanceState(hostId = request.hostId, pending = pendingByHost[request.hostId], feedbackVersion = ++feedbackSequence)
                    return@launch
                }
                val result = actions.execute(request,
                    { progress -> mutable.update { it.copy(progress = progress) } },
                    { pending ->
                        require(pending.reference.hostId == request.hostId && pending.kind == request.kind)
                        journal.record(pending)
                        mutationStarted = true; pendingByHost[request.hostId] = pending
                        mutable.update { it.copy(pending = pending) }
                    })
                if (request.recovery != null) {
                    val receipt = result.recovered
                    if (receipt == null || !maintenanceRecoveryMatches(request.recovery, receipt)) {
                        mutable.value = MaintenanceState(hostId = request.hostId, pending = pendingByHost[request.hostId],
                            error = result.error ?: UiMessage(R.string.server_maintenance_read_failed), feedbackVersion = ++feedbackSequence)
                        return@launch
                    }
                    withContext(ioDispatcher) { journal.resolve(request.recovery) }
                    pendingByHost.remove(request.hostId)
                } else if (request.kind != ServerDeploymentKind.Status) {
                    val pending = pendingByHost[request.hostId]
                    if (pending != null && result.verifiedMutation?.let { maintenanceRecoveryMatches(pending, it) } == true ||
                        result.complete && result.error == null) {
                        if (pending != null) withContext(ioDispatcher) { journal.resolve(pending) }
                        pendingByHost.remove(request.hostId)
                    }
                }
                mutable.value = result.copy(pending = pendingByHost[request.hostId], hostId = request.hostId, feedbackVersion = ++feedbackSequence)
            } catch (cancelled: CancellationException) {
                mutable.value = MaintenanceState(hostId = request.hostId, pending = pendingByHost[request.hostId], error = maintenanceErrorMessage(cancelled, mutationStarted), feedbackVersion = ++feedbackSequence)
                throw cancelled
            } catch (error: Exception) {
                mutable.value = MaintenanceState(hostId = request.hostId, pending = pendingByHost[request.hostId], error = maintenanceErrorMessage(error, mutationStarted), feedbackVersion = ++feedbackSequence)
            }
        }
    }
}

/** Product errors expose only known protocol codes or a classified transport cause. */
internal fun maintenanceErrorMessage(error: Throwable, mutationStarted: Boolean): UiMessage {
    val code = error.message
    if (code == "server-deployment.postcondition_failed") return UiMessage(R.string.server_maintenance_verification_failed)
    if (code in ServerDeploymentProblemCodes.all) return UiMessage(R.string.server_maintenance_failed_code, listOf(requireNotNull(code)))
    if (mutationStarted) return UiMessage(R.string.server_maintenance_result_unknown)
    val reason = SshFailureRules.classify(error)
    return UiMessage(if (reason == SshFailureReason.Unexpected) R.string.server_maintenance_read_failed else sshFailureMessage(reason))
}
