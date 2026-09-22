package app.relaxkonos.mobile.ui.connect

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultKeyInvalidatedException
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.security.model.SavedConnection
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.ui.common.ErrorBanner
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.failureMessage
import app.relaxkonos.mobile.ui.common.problemMessage
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.common.withDebugDetail
import kotlinx.coroutines.launch

/**
 * Sign-in state.
 *
 * Only one password is ever in flight: it is converted to a `CharArray` the moment it is submitted,
 * the text field is cleared immediately, and the array is zeroed once the network call has finished.
 * The text field itself is a `String`, because that is what Compose text input is built on; the string
 * is dropped as soon as the field is cleared, and nothing derived from it is persisted.
 *
 * The fingerprint path never asks the server for an alternative: it unlocks the local ciphertext and
 * then performs an ordinary login, so biometrics stay a local unlock rather than a server-side proof
 * (`RelaxKonOS.Mobile.V1.Design.md` §8).
 */
class LoginViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    private var revision by mutableStateOf(0)

    var serverUrl by mutableStateOf(container.profiles.recent()?.serverUrl.orEmpty())
    var identifier by mutableStateOf(container.profiles.recent()?.identifier.orEmpty())
    var password by mutableStateOf("")
    var rememberCredential by mutableStateOf(true)
    var busy by mutableStateOf(false)
    var message by mutableStateOf<UiMessage?>(null)
    var passwordFormRequested by mutableStateOf(false)
    var connectionsOpen by mutableStateOf(false)

    val profiles: List<SavedConnection> get() = revision.let { container.profiles.all() }

    /** The stored credential for the address and account currently entered, if any. */
    val savedRecord: VaultRecord? get() = revision.let { recordFor() }

    val unlockMode: VaultUnlockMode? get() = revision.let { container.unlockMode(VaultKind.Connection) }

    /** The fingerprint entry exists only when a password is actually stored and unlockable. */
    val canUseFingerprint: Boolean get() = savedRecord != null && unlockMode != null

    val hasProfiles: Boolean get() = profiles.isNotEmpty()

    fun changeServer(value: String) {
        serverUrl = value
    }

    fun changeIdentifier(value: String) {
        identifier = value
    }

    fun changePassword(value: String) {
        password = value
    }

    fun dismissMessage() {
        message = null
    }

    fun requestPasswordForm() {
        passwordFormRequested = true
    }

    fun openConnections() {
        connectionsOpen = true
    }

    fun closeConnections() {
        connectionsOpen = false
    }

    fun select(connection: SavedConnection) {
        serverUrl = connection.serverUrl
        identifier = connection.identifier
        password = ""
        message = null
        connectionsOpen = false
    }

    fun remove(connection: SavedConnection) {
        container.vault.delete(VaultKind.Connection, connection.serverUrl, connection.identifier)
        container.profiles.remove(connection.serverUrl)
        revision++
        if (serverUrl == connection.serverUrl) {
            serverUrl = ""
            identifier = ""
        }
    }

    /** Signs in with the password from the form, then offers to store it when that is possible. */
    fun signIn(activity: FragmentActivity) {
        if (serverUrl.isBlank() || identifier.isBlank() || password.isEmpty()) {
            message = UiMessage(R.string.login_missing_fields)
            return
        }
        val normalized = serverUrl.trim().trimEnd('/')
        val account = identifier.trim()
        val credential = password.toCharArray()
        password = ""
        busy = true
        message = null
        viewModelScope.launch {
            when (val result = container.session.login(normalized, account, credential) {
                container.profiles.upsert(SavedConnection(normalized, account, System.currentTimeMillis()))
                revision++
                if (rememberCredential) {
                    storeCredential(activity, normalized, account, credential)
                } else {
                    credential.fill('\u0000')
                }
            }) {
                is ApiResult.Success -> {
                    // The profile and optional vault record were committed before the shell appeared.
                }

                is ApiResult.Problem -> {
                    credential.fill('\u0000')
                    message = loginProblemMessage(result)
                }

                is ApiResult.Transport -> {
                    credential.fill('\u0000')
                    message = loginTransportMessage(result)
                }
            }
            busy = false
        }
    }

    /**
     * Unlocks the stored password with a biometric and logs in with it.
     *
     * A password the server actively rejects is deleted (design §5.8.2). A network failure is not a
     * rejection, so the record survives it unchanged.
     */
    fun signInWithFingerprint(activity: FragmentActivity) {
        val record = savedRecord ?: return
        val mode = unlockMode ?: return
        busy = true
        message = null
        viewModelScope.launch {
            val outcome = container.vaultAccess.load(
                mode = mode,
                record = record,
                activity = activity,
                title = promptTitle(),
                subtitle = promptSubtitle(record.serverUrl),
                negativeButton = promptCancel(),
            )
            when (outcome) {
                is VaultOperation.Success -> submitUnlockedCredential(record, outcome.value)
                VaultOperation.Cancelled -> Unit
                is VaultOperation.Failed -> {
                    if (outcome.failure == UnlockFailure.KeyInvalidated) {
                        // A new biometric enrolment invalidates only the affected record.
                        container.vault.delete(record)
                        revision++
                    }
                    message = failureMessage(outcome.failure)
                }
            }
            busy = false
        }
    }

    private suspend fun submitUnlockedCredential(record: VaultRecord, credential: CharArray) {
        when (val result = container.session.login(record.serverUrl, record.account, credential) {}) {
            is ApiResult.Success -> {
                container.profiles.upsert(SavedConnection(record.serverUrl, record.account, System.currentTimeMillis()))
                container.vault.markUsed(record, System.currentTimeMillis())
                revision++
            }

            is ApiResult.Problem -> {
                if (result.code == ProblemCodes.INVALID_CREDENTIAL) {
                    // The server rejected the stored password: drop the password, keep server and account.
                    container.vault.delete(record)
                    revision++
                    message = UiMessage(R.string.login_stored_credential_rejected)
                } else {
                    message = loginProblemMessage(result)
                }
            }

            is ApiResult.Transport -> message = loginTransportMessage(result)
        }
        credential.fill('\u0000')
    }

    /**
     * Stores the password after an explicit biometric confirmation.
     *
     * Saving is a separate, user-visible step: it needs the checkbox on the form and a strong
     * biometric, and it never happens silently (`RelaxKonOS.Mobile.V1.Design.md` §5.8.1, D1).
     */
    private suspend fun storeCredential(activity: FragmentActivity, url: String, account: String, credential: CharArray) {
        val mode = container.unlockMode(VaultKind.Connection)
        if (mode == null) {
            credential.fill('\u0000')
            message = UiMessage(R.string.vault_failure_unavailable)
            return
        }
        val outcome = try {
            container.vaultAccess.save(
                kind = VaultKind.Connection,
                mode = mode,
                serverUrl = url,
                account = account,
                password = credential,
                activity = activity,
                title = saveTitle(),
                subtitle = saveSubtitle(url),
                negativeButton = promptCancel(),
                nowEpochMillis = System.currentTimeMillis(),
            )
        } catch (_: VaultKeyInvalidatedException) {
            VaultOperation.Failed(UnlockFailure.KeyInvalidated)
        }
        credential.fill('\u0000')
        revision++
        when (outcome) {
            is VaultOperation.Success -> Unit
            // The user signed in successfully; failing to save must not look like a failed login.
            VaultOperation.Cancelled -> message = UiMessage(R.string.login_credential_not_saved)
            is VaultOperation.Failed -> message = failureMessage(outcome.failure)
        }
    }

    private fun recordFor(): VaultRecord? {
        if (serverUrl.isBlank() || identifier.isBlank()) {
            return null
        }
        return container.vault.record(VaultKind.Connection, serverUrl.trim().trimEnd('/'), identifier.trim())
    }

    /**
     * Release builds retain the localised, user-safe sentence. Debug builds additionally expose
     * the HTTP verdict so a rejected credential is distinguishable from throttling or a server
     * failure while testing Android against a real host.
     */
    private fun loginProblemMessage(result: ApiResult.Problem): UiMessage {
        val trace = result.traceId?.let { "; traceId=$it" }.orEmpty()
        return problemMessage(result.code).withDebugDetail("HTTP ${result.status}; problem=${result.code}$trace")
    }

    /** A transport result is also used for malformed responses and non-JSON 429/5xx responses. */
    private fun loginTransportMessage(result: ApiResult.Transport): UiMessage =
        UiMessage(R.string.error_connectivity).withDebugDetail(result.detail)

    private fun failureMessage(failure: UnlockFailure): UiMessage = UiMessage(
        when (failure) {
            UnlockFailure.LockedOut -> R.string.vault_failure_locked_out
            UnlockFailure.LockedOutPermanently -> R.string.vault_failure_locked_out_permanent
            UnlockFailure.KeyInvalidated -> R.string.vault_failure_key_invalidated
            UnlockFailure.Unavailable -> R.string.vault_failure_unavailable
            UnlockFailure.Tampered -> R.string.vault_failure_tampered
            UnlockFailure.Unknown -> R.string.vault_failure_unknown
        },
    )

    private fun promptTitle() = getApplication<Application>().getString(R.string.vault_unlock_title)

    private fun promptSubtitle(target: String) = getApplication<Application>().getString(R.string.vault_unlock_subtitle, target)

    private fun promptCancel() = getApplication<Application>().getString(R.string.common_cancel)

    private fun saveTitle() = getApplication<Application>().getString(R.string.vault_save_connection_title)

    private fun saveSubtitle(target: String) = getApplication<Application>().getString(R.string.vault_save_connection_subtitle, target)
}

/**
 * Sign-in screen.
 *
 * The form has two shapes from one code path (design §3.1): with a stored credential it opens in short
 * form, where the fingerprint is the primary action and the fields stay hidden until asked for; without
 * one it opens as the full form.
 */
@Composable
fun LoginScreen(modifier: Modifier = Modifier) {
    val activity = LocalContext.current as? FragmentActivity ?: return
    val viewModel: LoginViewModel = viewModel()
    val container = appContainer()

    val showPasswordForm = !viewModel.canUseFingerprint || viewModel.passwordFormRequested

    Column(
        modifier = modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Card(Modifier.fillMaxWidth().widthIn(max = 520.dp)) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(stringResource(R.string.login_title), style = MaterialTheme.typography.headlineSmall)
                Text(
                    stringResource(R.string.login_subtitle),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    style = MaterialTheme.typography.bodyMedium,
                )

                viewModel.message?.let { banner ->
                    ErrorBanner(
                        message = banner.text(),
                        onRetry = null,
                        onDismiss = { viewModel.dismissMessage() },
                    )
                }

                if (viewModel.canUseFingerprint && !viewModel.passwordFormRequested) {
                    val record = viewModel.savedRecord
                    Text(
                        stringResource(
                            R.string.login_saved_credential,
                            record?.account.orEmpty(),
                            record?.serverUrl.orEmpty(),
                        ),
                    )
                    Button(
                        onClick = { viewModel.signInWithFingerprint(activity) },
                        enabled = !viewModel.busy,
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.login_use_fingerprint)) }
                    TextButton(onClick = { viewModel.requestPasswordForm() }) {
                        Text(stringResource(R.string.login_use_password))
                    }
                }

                if (showPasswordForm) {
                    OutlinedTextField(
                        value = viewModel.serverUrl,
                        onValueChange = { viewModel.changeServer(it) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text(stringResource(R.string.login_server_address)) },
                        singleLine = true,
                        enabled = !viewModel.busy,
                    )
                    OutlinedTextField(
                        value = viewModel.identifier,
                        onValueChange = { viewModel.changeIdentifier(it) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text(stringResource(R.string.login_identifier)) },
                        singleLine = true,
                        enabled = !viewModel.busy,
                    )
                    PasswordTextField(
                        value = viewModel.password,
                        onValueChange = { viewModel.changePassword(it) },
                        label = stringResource(R.string.login_password),
                        enabled = !viewModel.busy,
                    )
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(
                            checked = viewModel.rememberCredential,
                            onCheckedChange = { viewModel.rememberCredential = it },
                            enabled = !viewModel.busy && container.unlockMode(VaultKind.Connection) != null,
                        )
                        Text(stringResource(R.string.login_remember_hint))
                    }
                    if (container.unlockMode(VaultKind.Connection) == null) {
                        Text(
                            stringResource(R.string.login_no_fingerprint),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    Button(
                        onClick = { viewModel.signIn(activity) },
                        enabled = !viewModel.busy,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text(
                            stringResource(
                                if (viewModel.busy) R.string.login_action_connecting else R.string.login_action_connect,
                            ),
                        )
                    }
                    if (viewModel.hasProfiles) {
                        OutlinedButton(onClick = { viewModel.openConnections() }, modifier = Modifier.fillMaxWidth()) {
                            Text(stringResource(R.string.connections_title))
                        }
                    }
                }

                if (container.unlockMode(VaultKind.Connection) == VaultUnlockMode.DeviceUnlockWindow) {
                    Text(
                        stringResource(R.string.vault_device_window_notice),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }

    if (viewModel.connectionsOpen) {
        ConnectionListScreen(
            profiles = viewModel.profiles,
            hasCredential = { connection ->
                container.vault.record(VaultKind.Connection, connection.serverUrl, connection.identifier) != null
            },
            onSelected = { viewModel.select(it) },
            onDelete = { viewModel.remove(it) },
            onDismiss = { viewModel.closeConnections() },
        )
    }
}
