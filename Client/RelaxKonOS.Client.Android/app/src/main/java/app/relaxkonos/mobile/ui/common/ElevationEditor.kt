package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.*
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ElevationAnswer
import app.relaxkonos.mobile.data.ElevationPrompt
import app.relaxkonos.mobile.security.*
import kotlinx.coroutines.*

/** One prompt owns its form, vault access and plaintext lifetime. */
internal class ElevationEditor(
    private val container: AppContainer,
    private val prompt: ElevationPrompt,
    parentScope: CoroutineScope,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    private val current get() = job.isActive && container.elevationPrompts.request.value === prompt
    private val serviceId = container.session.serviceId.orEmpty()
    val elevationMode get() = container.unlockMode(VaultKind.Elevation)
    val savedAccount = prompt.savedAdministratorAccount
    var vaultRevision by mutableStateOf(0)
    val savedRecord get(): VaultRecord? {
        vaultRevision // Observe invalidation so Compose rereads the sealed record.
        return if (serviceId.isBlank() || savedAccount.isNullOrBlank()) null
        else container.vault.record(VaultKind.Elevation, serviceId, savedAccount)
    }
    var account by mutableStateOf(savedAccount ?: "")
    var password by mutableStateOf("")
    var storeRequested by mutableStateOf(false)
    var message by mutableStateOf<UiMessage?>(null)
    var busy by mutableStateOf(false)
    val reminders get() = container.notices

    fun close() { job.cancel(); password = "" }
    fun cancel() { if (current) container.elevationPrompts.cancel() }
    private fun supply(answer: ElevationAnswer) {
        if (current) container.elevationPrompts.supply(answer) else answer.password.fill('\u0000')
    }
    fun unlock(savedRecord: VaultRecord, elevationMode: VaultUnlockMode, activity: FragmentActivity,
        unlockTitle: String, unlockSubtitle: String, cancelLabel: String) {
        if (!current || busy) return
        busy = true
        scope.launch {
            try {
                val outcome = container.vaultAccess.load(
                    mode = elevationMode, record = savedRecord, activity = activity,
                    title = unlockTitle, subtitle = unlockSubtitle, negativeButton = cancelLabel,
                )
                when (outcome) {
                    is VaultOperation.Success -> supply(ElevationAnswer(savedRecord.account, outcome.value))
                    VaultOperation.Cancelled -> Unit
                    is VaultOperation.Failed -> if (current) reportFailure(outcome.failure)
                }
            } finally { busy = false }
        }
    }

    fun submit(activity: FragmentActivity, saveTitle: String, saveSubtitle: String, cancelLabel: String) {
        if (!current || busy) return
        val mode = elevationMode
        if (account.isBlank() || password.isEmpty()) {
            message = UiMessage(R.string.elevation_missing_fields)
            return
        }
        val answer = ElevationAnswer(account.trim(), password.toCharArray())
        password = ""
        if (!storeRequested || mode == null || serviceId.isBlank()) {
            supply(answer)
            return
        }
        busy = true
        scope.launch {
            var supplied = false
            try {
                val outcome = container.vaultAccess.save(
                    kind = VaultKind.Elevation, mode = mode, serviceId = serviceId,
                    account = answer.account, password = answer.password, activity = activity,
                    title = saveTitle, subtitle = saveSubtitle, negativeButton = cancelLabel,
                    nowEpochMillis = System.currentTimeMillis(),
                )
                if (!current) return@launch
                when (outcome) {
                    is VaultOperation.Success -> { supply(answer); supplied = true }
                    VaultOperation.Cancelled -> {
                        storeRequested = false
                        message = UiMessage(R.string.elevation_credential_not_stored, tone = StatusTone.Warning)
                    }
                    is VaultOperation.Failed -> {
                        storeRequested = false
                        reportFailure(outcome.failure)
                    }
                }
            } finally {
                if (!supplied) answer.password.fill('\u0000')
                busy = false
            }
        }
    }

    private fun reportFailure(failure: UnlockFailure) {
        if (failure == UnlockFailure.KeyInvalidated) {
            // One elevation alias covers every record; retain and mark them together.
            container.vault.markAllInvalidated(VaultKind.Elevation)
            vaultRevision++
        }
        message = unlockFailureMessage(failure).withReminder(silenceableUnlockVerdict(failure, reminders))
    }
}
