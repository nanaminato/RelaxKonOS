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
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultRecordState
import kotlinx.coroutines.launch

/**
 * Host elevation dialog.
 *
 * It is a full dialog rather than a bottom sheet on purpose (`RelaxKonOS.Mobile.V1.Design.md` §3.4):
 * a stray tap must not be able to authorize a host-level change. The dialog names the exact capability
 * and target, is the only place an administrator password is entered, and is the only place one can be
 * stored — saving requires ticking an explicit box and passing a second strong-biometric check (D1).
 *
 * An administrator credential whose key was permanently invalidated is still listed on the account and
 * security page, but it is not offered here: the only way to authorize is to type the password again
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.5).
 */
@Composable
fun ElevationDialog(container: AppContainer) {
    val prompt = container.elevationPrompts.request.collectAsStateValue() ?: return
    val context = LocalContext.current
    val activity = context as? FragmentActivity ?: return
    val scope = rememberCoroutineScope()

    val serverUrl = container.session.serverUrl.orEmpty()
    val elevationMode = container.unlockMode(VaultKind.Elevation)
    val savedAccount = prompt.savedAdministratorAccount
    var vaultRevision by remember { mutableStateOf(0) }
    val savedRecord = remember(serverUrl, savedAccount, vaultRevision) {
        if (serverUrl.isBlank() || savedAccount.isNullOrBlank()) {
            null
        } else {
            container.vault.record(VaultKind.Elevation, serverUrl, savedAccount)
        }
    }

    var account by remember { mutableStateOf(savedAccount.orEmpty()) }
    var password by remember { mutableStateOf("") }
    var storeRequested by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }

    // Every string the click handlers need is resolved here: an `onClick` lambda is not a composable
    // context and a coroutine is not one either.
    val unlockTitle = stringResource(R.string.vault_unlock_title)
    val unlockSubtitle = stringResource(R.string.vault_unlock_subtitle, prompt.target)
    val saveTitle = stringResource(R.string.vault_save_elevation_title)
    val saveSubtitle = stringResource(R.string.vault_save_elevation_subtitle)
    val cancelLabel = stringResource(R.string.common_cancel)
    val missingFieldsLabel = stringResource(R.string.elevation_missing_fields)
    val notStoredLabel = stringResource(R.string.elevation_credential_not_stored)

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
                message?.let {
                    Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                }
            }
        },
        confirmButton = {
            Button(
                enabled = !busy,
                onClick = {
                    if (account.isBlank() || password.isEmpty()) {
                        message = missingFieldsLabel
                        return@Button
                    }
                    val answer = ElevationAnswer(account.trim(), password.toCharArray())
                    password = ""
                    if (!storeRequested || elevationMode == null || serverUrl.isBlank()) {
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
                            serverUrl = serverUrl,
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
                                message = notStoredLabel
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
                                message = unlockFailureLabel(context, outcome.failure)
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
                                        message = unlockFailureLabel(context, outcome.failure)
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
