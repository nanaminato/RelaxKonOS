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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.VaultRecordState

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

    val editor = remember(prompt, scope) { ElevationEditor(container, prompt, scope) }
    androidx.compose.runtime.DisposableEffect(editor) { onDispose { editor.close() } }
    with(editor) {
        val savedRecord = editor.savedRecord
        val elevationMode = editor.elevationMode
        val passwordFocus = remember(prompt) { FocusRequester() }
        LaunchedEffect(prompt) { if (!savedAccount.isNullOrBlank()) passwordFocus.requestFocus() }
        // Every string the click handlers need is resolved here: an `onClick` lambda is not a composable
        // context and a coroutine is not one either.
        val unlockTitle = stringResource(R.string.vault_unlock_title)
        val unlockSubtitle = stringResource(R.string.vault_unlock_subtitle, prompt.target)
        val saveTitle = stringResource(R.string.vault_save_elevation_title)
        val saveSubtitle = stringResource(R.string.vault_save_elevation_subtitle)
        val cancelLabel = stringResource(R.string.common_cancel)

        AlertDialog(
            onDismissRequest = { editor.cancel() },
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
                        modifier = Modifier.focusRequester(passwordFocus),
                        enabled = !busy,
                    )
                    if (elevationMode == null) {
                        Text(
                            text = stringResource(R.string.elevation_vault_unavailable),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    } else {
                        CheckboxOption(storeRequested, stringResource(R.string.elevation_save_credential), !busy) { storeRequested = it }
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
                    onClick = { editor.submit(activity, saveTitle, saveSubtitle, cancelLabel) },
                ) { Text(stringResource(R.string.elevation_confirm)) }
            },
            dismissButton = {
                Row {
                    if (savedRecord?.state == VaultRecordState.Sealed && elevationMode != null) {
                        TextButton(
                            enabled = !busy,
                            onClick = { editor.unlock(savedRecord, elevationMode, activity, unlockTitle, unlockSubtitle, cancelLabel) },
                        ) { Text(stringResource(R.string.elevation_use_fingerprint)) }
                    }
                    TextButton(onClick = { editor.cancel() }) {
                        Text(stringResource(R.string.common_cancel))
                    }
                }
            },
        )
    }
}
