package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ElevationAnswer
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultRecordState
import kotlinx.coroutines.launch

/**
 * Host elevation dialog.
 *
 * It is a full dialog rather than a bottom sheet on purpose (`Shell.Design.md` §3.4):
 * a stray tap must not be able to authorize a host-level change. The dialog names the exact capability
 * and target, is the only place an administrator password is entered, and is the only place one can be
 * stored — saving requires ticking an explicit box and passing a second strong-biometric check (D1).
 *
 * An administrator credential whose key was permanently invalidated is still listed on the account and
 * security page, but it is not offered here: the only way to authorize is to type the password again
 * (`LoginCredentials.Design.md` §7.5).
 */
@Composable
fun ElevationDialog(container: AppContainer) {
    val prompt = container.elevationPrompts.request.collectAsStateValue() ?: return
    val context = LocalContext.current
    val activity = context as? FragmentActivity ?: return
    val scope = rememberCoroutineScope()

    val serviceId = container.session.serviceId.orEmpty()
    val elevationMode = container.unlockMode(VaultKind.Elevation)
    val savedAccount = prompt.savedAdministratorAccount
    var vaultRevision by remember { mutableStateOf(0) }
    val savedRecord = remember(serviceId, savedAccount, vaultRevision) {
        if (serviceId.isBlank() || savedAccount.isNullOrBlank()) {
            null
        } else {
            container.vault.record(VaultKind.Elevation, serviceId, savedAccount)
        }
    }

    var account by remember {
        mutableStateOf(savedAccount ?: "")
    }
    var password by remember { mutableStateOf("") }
    var storeRequested by remember { mutableStateOf(false) }
    // A message rather than a string: the outcome of the save step decides both how it reads and
    // whether it is one of the verdicts this device will keep repeating.
    var message by remember { mutableStateOf<UiMessage?>(null) }
    var busy by remember { mutableStateOf(false) }
    val reminders = container.notices

    // Every string the click handlers need is resolved here: an `onClick` lambda is not a composable
    // context and a coroutine is not one either.
    val unlockTitle = stringResource(R.string.vault_unlock_title)
    val unlockSubtitle = stringResource(R.string.vault_unlock_subtitle, prompt.target)
    val saveTitle = stringResource(R.string.vault_save_elevation_title)
    val saveSubtitle = stringResource(R.string.vault_save_elevation_subtitle)
    val cancelLabel = stringResource(R.string.common_cancel)

    /**
     * The one verdict in this dialog that is a fact about the device rather than about this attempt,
     * and only while the user has not already answered it.
     *
     * "No lock screen can unseal a saved password" will not read differently on the next press, so it
     * may be answered for good — the same [ReminderKind.SavedPasswordUnavailable] the sign-in and
     * server-centre screens offer for the same sentence. A lockout, an invalidated key or tampering
     * each leave the user something to do, so they keep interrupting.
     */
    fun silenceableVerdict(failure: UnlockFailure): ReminderKind? =
        if (failure == UnlockFailure.Unavailable && !reminders.isSilenced(ReminderKind.SavedPasswordUnavailable)) {
            ReminderKind.SavedPasswordUnavailable
        } else {
            null
        }

    AlertDialog(
        onDismissRequest = { container.elevationPrompts.cancel() },
        title = { Text(stringResource(R.string.elevation_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(stringResource(R.string.elevation_body, capabilityLabel(prompt.capability), prompt.target))
                OutlinedTextField(
                    value = account,
                    onValueChange = { account = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.elevation_account_label)) },
                    singleLine = true,
                    enabled = !busy,
                )
                PasswordTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = stringResource(R.string.elevation_password_label),
                    enabled = !busy,
                )
                if (elevationMode == null) {
                    Text(
                        text = stringResource(R.string.elevation_vault_unavailable),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(
                            checked = storeRequested,
                            onCheckedChange = { storeRequested = it },
                            enabled = !busy,
                        )
                        Text(stringResource(R.string.elevation_save_credential))
                    }
                }
                OperationMessageDialog(
                    message = message?.takeUnless { busy }?.let { it.text() },
                    eventKey = message,
                    tone = message?.tone ?: StatusTone.Danger,
                    onDismiss = { message = null },
                    // Unlike a floating notice, this sentence is load-bearing: it is the only thing that
                    // says the authorization did not go through and that pressing Authorize again
                    // continues without saving (§5.4 rule 2). So it is never skipped — what an answered
                    // reminder removes here is the offer, and that is decided where the message is set.
                    reminder = message?.reminder,
                    onSilenceReminder = { kind -> reminders.silence(kind) },
                )
            }
        },
        confirmButton = {
            Button(
                enabled = !busy,
                onClick = {
                    if (account.isBlank() || password.isEmpty()) {
                        message = UiMessage(R.string.elevation_missing_fields)
                        return@Button
                    }
                    val answer = ElevationAnswer(account.trim(), password.toCharArray())
                    password = ""
                    if (!storeRequested || elevationMode == null || serviceId.isBlank()) {
                        container.elevationPrompts.supply(answer)
                        return@Button
                    }
                    // Storing is a separate, explicit action: one more strong-biometric check stands
                    // between the tick and the ciphertext being written.
                    busy = true
                    scope.launch {
                        val outcome = container.vaultAccess.save(
                            kind = VaultKind.Elevation,
                            mode = elevationMode,
                            serviceId = serviceId,
                            account = answer.account,
                            password = answer.password,
                            activity = activity,
                            title = saveTitle,
                            subtitle = saveSubtitle,
                            negativeButton = cancelLabel,
                            nowEpochMillis = System.currentTimeMillis(),
                        )
                        busy = false
                        when (outcome) {
                            is VaultOperation.Success -> container.elevationPrompts.supply(answer)
                            VaultOperation.Cancelled -> {
                                answer.password.fill('\u0000')
                                storeRequested = false
                                // The user dismissed the confirmation himself, so this stays a warning he
                                // keeps seeing — the same reading as the sign-in and server-centre
                                // screens give the same outcome.
                                message = UiMessage(R.string.elevation_credential_not_stored, tone = StatusTone.Warning)
                            }
                            is VaultOperation.Failed -> {
                                if (outcome.failure == UnlockFailure.KeyInvalidated) {
                                    // The elevation vault uses one alias, so mark its records together
                                    // and retain them all rather than deleting user data (D5).
                                    container.vault.markAllInvalidated(VaultKind.Elevation)
                                    vaultRevision++
                                }
                                answer.password.fill('\u0000')
                                storeRequested = false
                                message = unlockFailureMessage(outcome.failure)
                                    .withReminder(silenceableVerdict(outcome.failure))
                            }
                        }
                    }
                },
            ) { Text(stringResource(R.string.elevation_confirm)) }
        },
        dismissButton = {
            Row {
                if (savedRecord?.state == VaultRecordState.Sealed && elevationMode != null) {
                    TextButton(
                        enabled = !busy,
                        onClick = {
                            busy = true
                            scope.launch {
                                val outcome = container.vaultAccess.load(
                                    mode = elevationMode,
                                    record = savedRecord,
                                    activity = activity,
                                    title = unlockTitle,
                                    subtitle = unlockSubtitle,
                                    negativeButton = cancelLabel,
                                )
                                busy = false
                                when (outcome) {
                                    is VaultOperation.Success ->
                                        container.elevationPrompts.supply(ElevationAnswer(savedRecord.account, outcome.value))

                                    VaultOperation.Cancelled -> Unit
                                    is VaultOperation.Failed -> {
                                        if (outcome.failure == UnlockFailure.KeyInvalidated) {
                                            // The record is kept and marked, so the user can see why the
                                            // saved password stopped working (D5).
                                            container.vault.markAllInvalidated(VaultKind.Elevation)
                                            vaultRevision++
                                        }
                                        // The same sentence as the save branch, so it carries the same
                                        // offer: unlocking a saved record is where
                                        // `SavedPasswordUnavailable` reads most literally.
                                        message = unlockFailureMessage(outcome.failure)
                                            .withReminder(silenceableVerdict(outcome.failure))
                                    }
                                }
                            }
                        },
                    ) { Text(stringResource(R.string.elevation_use_fingerprint)) }
                }
                TextButton(onClick = { container.elevationPrompts.cancel() }) {
                    Text(stringResource(R.string.common_cancel))
                }
            }
        },
    )
}
