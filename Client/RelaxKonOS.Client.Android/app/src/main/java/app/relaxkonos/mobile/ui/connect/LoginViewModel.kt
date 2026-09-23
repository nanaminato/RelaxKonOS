package app.relaxkonos.mobile.ui.connect

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.CredentialGap
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.core.auth.LoginDecision
import app.relaxkonos.mobile.core.auth.SavedCredentialState
import app.relaxkonos.mobile.core.auth.SelectedLogin
import app.relaxkonos.mobile.core.auth.credentialState
import app.relaxkonos.mobile.core.auth.credentialStatus as credentialStatusOf
import app.relaxkonos.mobile.core.auth.decideLogin
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultKeyInvalidatedException
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.problemMessage
import app.relaxkonos.mobile.ui.common.unlockFailureLabel
import app.relaxkonos.mobile.ui.common.unlockFailureMessage
import app.relaxkonos.mobile.ui.common.withDebugDetail
import kotlinx.coroutines.launch

/** The field a rejected click pointed at. One-shot: the screen focuses it, then clears the request. */
enum class LoginField { Server, Identifier, Password }

/**
 * Sign-in state for the single-form login screen.
 *
 * Four independent facts are held apart, exactly as the design requires
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §2, §4): who is being signed in as
 * ([selectedLogin]), whether a password is stored and usable ([savedCredentialState]), whether this
 * device may unseal it ([vaultUnlockMode]), and what the user typed *this time* ([passwordText]).
 * Nothing here is derived from the password field, and the field is never filled from the vault.
 *
 * One password is ever in flight. It is converted to a `CharArray` the moment the click is decided, the
 * text field is cleared immediately, and the array is zeroed once the request has finished — whichever
 * way it finished. Nothing derived from it is persisted.
 */
class LoginViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    private var revision by mutableStateOf(0)

    var serverUrl by mutableStateOf(container.profiles.recent()?.serverUrl.orEmpty())
    var identifier by mutableStateOf(container.profiles.recent()?.identifier.orEmpty())

    /** Only ever what was typed this time. A saved password is never written here (§3, §6.2). */
    var passwordText by mutableStateOf("")

    var rememberCredential by mutableStateOf(true)
    var isLoggingIn by mutableStateOf(false)
    var message by mutableStateOf<UiMessage?>(null)
    var connectionsOpen by mutableStateOf(false)

    var focusRequest by mutableStateOf<LoginField?>(null)
        private set

    /**
     * Identities whose saved password was authorized in this process under the device-unlock window
     * (D3). While the Keystore key is inside its five-minute window, signing in again needs no second
     * confirmation; per-use mode never consults this set, because there every unseal must be authorized
     * on its own (§4.3).
     */
    private var windowUnlocked: Set<String> = emptySet()

    /** The identity the form currently describes, i.e. the `(Service, Username)` pair (§2.1). */
    val selectedLogin: SelectedLogin get() = SelectedLogin(serverUrl, identifier)

    /** How this device can unseal a credential right now, or `null` when it cannot unseal anything. */
    val vaultUnlockMode: VaultUnlockMode? get() = container.unlockMode(VaultKind.Connection)

    val logins: List<SavedLogin> get() = revision.let { container.profiles.all() }

    val hasLogins: Boolean get() = logins.isNotEmpty()

    /** Whether the identity in the form has a saved password, and whether it can be used right now. */
    val savedCredentialState: SavedCredentialState
        get() = revision.let { credentialState(storedRecord(), container.unlockMode(VaultKind.Connection)) }

    /** The line rendered beside the password field. Never the password, only the fact. */
    val credentialStatus: CredentialStatus
        get() = revision.let {
            val mode = container.unlockMode(VaultKind.Connection)
            credentialStatusOf(credentialState(storedRecord(), mode), mode)
        }

    /** What the sign-in button will do on this click. */
    val decision: LoginDecision?
        get() = decideLogin(selectedLogin, passwordText, savedCredentialState, isLoggingIn)

    fun changeServer(value: String) {
        serverUrl = value
    }

    fun changeIdentifier(value: String) {
        identifier = value
    }

    fun changePassword(value: String) {
        passwordText = value
    }

    fun dismissMessage() {
        message = null
    }

    fun openConnections() {
        connectionsOpen = true
    }

    fun closeConnections() {
        connectionsOpen = false
    }

    fun consumeFocusRequest() {
        focusRequest = null
    }

    /**
     * Switches to another saved identity.
     *
     * Clearing the password field and forgetting any window authorization is the whole job: switching is
     * not a credential event, so nothing is read, nothing is deleted, and no prompt is raised. The
     * authorization happens later, when the saved password is actually needed (§7.1).
     */
    fun select(login: SavedLogin) {
        serverUrl = login.serverUrl
        identifier = login.identifier
        passwordText = ""
        message = null
        connectionsOpen = false
        focusRequest = null
        windowUnlocked = windowUnlocked - loginIdOf(login.serverUrl, login.identifier)
    }

    /** "Forget the password": drops the credential, keeps the login (§6.3). */
    fun forgetPassword(login: SavedLogin) {
        container.vault.delete(VaultKind.Connection, login.serverUrl, login.identifier)
        container.profiles.setHasSavedCredential(login.serverUrl, login.identifier, false)
        windowUnlocked = windowUnlocked - loginIdOf(login.serverUrl, login.identifier)
        message = null
        revision++
    }

    /** "Delete login record": drops the credential *and* this login, and no other account on it (§6.3). */
    fun deleteLogin(login: SavedLogin) {
        container.vault.delete(VaultKind.Connection, login.serverUrl, login.identifier)
        container.profiles.remove(login.serverUrl, login.identifier)
        windowUnlocked = windowUnlocked - loginIdOf(login.serverUrl, login.identifier)
        revision++
        if (serverUrl == login.serverUrl && identifier == login.identifier) {
            serverUrl = ""
            identifier = ""
            passwordText = ""
        }
    }

    /**
     * The single entry point of the sign-in button.
     *
     * The button has no mode: this is the decision table of §5.1, and the button's label is derived from
     * the same table, so there is no second path that could fail to fall back to the first.
     */
    fun submit(activity: FragmentActivity) {
        when (val plan = decision) {
            // A sign-in is already in flight: the click is ignored rather than queued.
            null -> Unit

            is LoginDecision.MissingFields -> {
                message = UiMessage(R.string.login_missing_fields)
                focusRequest = if (plan.server) LoginField.Server else LoginField.Identifier
            }

            is LoginDecision.RequirePassword -> {
                message = when (plan.gap) {
                    CredentialGap.Absent -> UiMessage(R.string.login_password_required)
                    CredentialGap.Unavailable -> UiMessage(R.string.login_saved_password_unavailable)
                    CredentialGap.Invalidated -> UiMessage(R.string.login_saved_password_invalidated)
                }
                focusRequest = LoginField.Password
            }

            is LoginDecision.ManualPassword -> signInWithTypedPassword(activity, plan.password)

            LoginDecision.UnlockSavedCredential -> signInWithSavedPassword(activity)
        }
    }

    /** Path one: the password typed into the form is used, and offered for saving on success (§5.2). */
    private fun signInWithTypedPassword(activity: FragmentActivity, credential: CharArray) {
        val login = selectedLogin
        passwordText = ""
        message = null
        isLoggingIn = true
        viewModelScope.launch {
            try {
                val result = container.session.login(
                    serverUrl = login.normalizedServerUrl,
                    identifier = login.normalizedIdentifier,
                    password = credential,
                ) {
                    rememberLogin(login)
                    if (rememberCredential) {
                        storeCredential(activity, login, credential)
                    }
                }
                report(result)
            } finally {
                credential.fill('\u0000')
                isLoggingIn = false
            }
        }
    }

    /** Path two: the stored password is unsealed after authorization, then used once (§5.2). */
    private fun signInWithSavedPassword(activity: FragmentActivity) {
        val record = storedRecord() ?: return
        val mode = container.unlockMode(VaultKind.Connection) ?: return
        val login = SelectedLogin(record.serverUrl, record.account)
        message = null
        isLoggingIn = true
        viewModelScope.launch {
            when (val outcome = unseal(record, activity, mode)) {
                is VaultOperation.Success -> {
                    if (mode == VaultUnlockMode.DeviceUnlockWindow) {
                        windowUnlocked = windowUnlocked + login.id
                    }
                    submitUnsealed(record, login, outcome.value)
                }

                // A dismissed prompt is silent: no error, no change to the record (§7.2).
                VaultOperation.Cancelled -> Unit

                is VaultOperation.Failed -> {
                    if (outcome.failure == UnlockFailure.KeyInvalidated) {
                        // The record is marked, never deleted: it stays visible with the reason (§7.4).
                        container.vault.markInvalidated(record)
                        revision++
                    }
                    message = unlockFailureMessage(outcome.failure)
                }
            }
            isLoggingIn = false
        }
    }

    /**
     * Signs in with a password that came out of the vault.
     *
     * A rejected password leaves the stored one exactly as it was, whether the server rejected it or the
     * request never got an answer (§7.3): deletion is an explicit user action, never a side effect of a
     * failed sign-in.
     */
    private suspend fun submitUnsealed(record: VaultRecord, login: SelectedLogin, credential: CharArray) {
        try {
            when (val result = container.session.login(record.serverUrl, record.account, credential) {}) {
                is ApiResult.Success -> {
                    rememberLogin(login)
                    container.vault.markUsed(record, System.currentTimeMillis())
                    revision++
                }

                is ApiResult.Problem -> message = loginProblemMessage(result)
                is ApiResult.Transport -> message = loginTransportMessage(result)
            }
        } finally {
            credential.fill('\u0000')
        }
    }

    /**
     * Unseals the stored password, reusing a live device-unlock window when there is one.
     *
     * The fall-through is the point: an unattended read is an optimization, never a gate. If the window
     * has expired the read fails harmlessly and the user simply confirms again.
     */
    private suspend fun unseal(
        record: VaultRecord,
        activity: FragmentActivity,
        mode: VaultUnlockMode,
    ): VaultOperation<CharArray> {
        if (mode == VaultUnlockMode.DeviceUnlockWindow && selectedLogin.id in windowUnlocked) {
            val unattended = container.vaultAccess.loadWithoutPrompt(record)
            if (unattended is VaultOperation.Success) {
                return unattended
            }
        }
        return container.vaultAccess.load(
            mode = mode,
            record = record,
            activity = activity,
            title = promptTitle(),
            subtitle = promptSubtitle(record.serverUrl),
            negativeButton = promptCancel(),
        )
    }

    /**
     * Stores the password after the sign-in succeeded, and only then (§8).
     *
     * A failure here is reported as "signed in, but the password was not saved" — never as a failed
     * sign-in — and an existing record is left alone rather than replaced by something the user did not
     * ask for (D9).
     */
    private suspend fun storeCredential(activity: FragmentActivity, login: SelectedLogin, credential: CharArray) {
        val mode = container.unlockMode(VaultKind.Connection)
        if (mode == null) {
            message = UiMessage(R.string.login_credential_not_saved)
            return
        }
        val outcome = try {
            container.vaultAccess.save(
                kind = VaultKind.Connection,
                mode = mode,
                serverUrl = login.normalizedServerUrl,
                account = login.normalizedIdentifier,
                password = credential,
                activity = activity,
                title = saveTitle(),
                subtitle = saveSubtitle(login.normalizedServerUrl),
                negativeButton = promptCancel(),
                nowEpochMillis = System.currentTimeMillis(),
            )
        } catch (_: VaultKeyInvalidatedException) {
            VaultOperation.Failed(UnlockFailure.KeyInvalidated)
        }
        revision++
        when (outcome) {
            is VaultOperation.Success ->
                container.profiles.setHasSavedCredential(login.normalizedServerUrl, login.normalizedIdentifier, true)

            VaultOperation.Cancelled -> message = UiMessage(R.string.login_credential_not_saved)

            is VaultOperation.Failed -> message = UiMessage(
                R.string.login_credential_not_saved_reason,
                listOf(unlockFailureLabel(getApplication<Application>(), outcome.failure)),
            )
        }
    }

    /** Records the login itself — never a secret — and keeps the credential projection truthful. */
    private fun rememberLogin(login: SelectedLogin) {
        val existing = container.profiles
            .all()
            .firstOrNull { it.serverUrl == login.normalizedServerUrl && it.identifier == login.normalizedIdentifier }
        container.profiles.upsert(
            SavedLogin(
                serverUrl = login.normalizedServerUrl,
                identifier = login.normalizedIdentifier,
                lastUsedEpochMillis = System.currentTimeMillis(),
                displayName = existing?.displayName,
                hasSavedCredential = container.vault
                    .record(VaultKind.Connection, login.normalizedServerUrl, login.normalizedIdentifier) != null,
            ),
        )
        revision++
    }

    /** The vault record for the identity in the form, if one exists. Reads no password. */
    private fun storedRecord(): VaultRecord? {
        val login = selectedLogin
        if (!login.isComplete) {
            return null
        }
        return container.vault.record(VaultKind.Connection, login.normalizedServerUrl, login.normalizedIdentifier)
    }

    private fun loginIdOf(serverUrl: String, identifier: String): String = SelectedLogin(serverUrl, identifier).id

    private fun report(result: ApiResult<*>) {
        when (result) {
            is ApiResult.Success -> Unit
            is ApiResult.Problem -> message = loginProblemMessage(result)
            is ApiResult.Transport -> message = loginTransportMessage(result)
        }
    }

    /**
     * Release builds retain the localised, user-safe sentence. Debug builds additionally expose the
     * HTTP verdict so a rejected credential is distinguishable from throttling or a server failure
     * while testing Android against a real host.
     */
    private fun loginProblemMessage(result: ApiResult.Problem): UiMessage {
        val trace = result.traceId?.let { "; traceId=$it" }.orEmpty()
        return problemMessage(result.code).withDebugDetail("HTTP ${result.status}; problem=${result.code}$trace")
    }

    /** A transport result is also used for malformed responses and non-JSON 429/5xx responses. */
    private fun loginTransportMessage(result: ApiResult.Transport): UiMessage =
        UiMessage(R.string.error_connectivity).withDebugDetail(result.detail)

    private fun promptTitle() = getApplication<Application>().getString(R.string.vault_unlock_title)

    private fun promptSubtitle(target: String) =
        getApplication<Application>().getString(R.string.vault_unlock_subtitle, target)

    private fun promptCancel() = getApplication<Application>().getString(R.string.common_cancel)

    private fun saveTitle() = getApplication<Application>().getString(R.string.vault_save_connection_title)

    private fun saveSubtitle(target: String) =
        getApplication<Application>().getString(R.string.vault_save_connection_subtitle, target)
}
