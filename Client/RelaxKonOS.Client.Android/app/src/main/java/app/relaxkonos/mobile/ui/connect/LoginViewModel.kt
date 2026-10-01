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
import app.relaxkonos.mobile.core.auth.loginId
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.EndpointDiscoveryResult
import app.relaxkonos.mobile.core.net.ServerEndpointDiscovery
import app.relaxkonos.mobile.security.BiometricCapability
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultDiagnostics
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
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/** The field a rejected click pointed at. One-shot: the screen focuses it, then clears the request. */
enum class LoginField { Server, Identifier, Password }

/** The address field's non-secret endpoint discovery feedback. */
enum class EndpointDiscoveryState { Idle, Checking, Found, InvalidAddress, Unavailable }

/**
 * Sign-in state for the single-form login screen.
 *
 * Four independent facts are held apart, exactly as the design requires
 * (`LoginCredentials.Design.md` §2, §4): who is being signed in as
 * ([selectedLogin]), whether a password is stored and usable ([savedCredentialState]), whether this
 * device may unseal it ([vaultUnlockMode]), and what the user typed *this time* ([passwordText]).
 * Nothing here is derived from the password field, and the field is never filled from the vault.
 *
 * One password is ever in flight. It is converted to a `CharArray` the moment the click is decided, the
 * text field is cleared immediately, and the array is zeroed once the request has finished — whichever
 * way it finished. Nothing derived from it is persisted.
 */
class LoginViewModel(application: Application) : AndroidViewModel(application) {
    init { app.relaxkonos.mobile.core.net.ServerCertificateTrust.initialize(application) }
    var certificateReview by mutableStateOf<app.relaxkonos.mobile.core.net.CertificateReview?>(null)
        private set
    private var certificateAnswer: kotlinx.coroutines.CompletableDeferred<Boolean>? = null
    fun answerCertificate(accept: Boolean) { certificateAnswer?.complete(accept) }
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container

    private var revision by mutableStateOf(0)
    private val initialDirectLogin = container.profiles.all().firstOrNull { it.directServerUrl != null }

    var serverUrl by mutableStateOf(initialDirectLogin?.directServerUrl.orEmpty())
    var identifier by mutableStateOf(initialDirectLogin?.identifier.orEmpty())

    /** Only ever what was typed this time. A saved password is never written here (§3, §6.2). */
    var passwordText by mutableStateOf("")

    /** Exact base64 JSON payload produced by the already-enrolled Windows controller. */
    var ownerDevicePairingCode by mutableStateOf("")

    var rememberCredential by mutableStateOf(true)
    var isLoggingIn by mutableStateOf(false)
    var message by mutableStateOf<UiMessage?>(null)
    var connectionsOpen by mutableStateOf(false)

    /**
     * The host name of the managed login currently in the form, or `null` for a direct one.
     *
     * A managed login's stable identity is an installation id, so there is no server address to fill in:
     * its local tunnel address exists only once the SSH tunnel is open. The form therefore names the host
     * and says where the tunnel is established, instead of showing an invented address or a blank field.
     */
    var managedHostName by mutableStateOf<String?>(null)
        private set

    var endpointDiscoveryState by mutableStateOf(EndpointDiscoveryState.Idle)
        private set

    var focusRequest by mutableStateOf<LoginField?>(null)
        private set

    /**
     * Identities whose saved password was authorized in this process under the device-unlock window
     * (D3). While the Keystore key is inside its five-minute window, signing in again needs no second
     * confirmation; per-use mode never consults this set, because there every unseal must be authorized
     * on its own (§4.3).
     */
    private var windowUnlocked: Set<String> = emptySet()
    private var discoveryJob: Job? = null
    private var hostLookupJob: Job? = null

    /** The identity the form currently describes, i.e. the `(Service, Username)` pair (§2.1). */
    val selectedLogin: SelectedLogin get() = SelectedLogin.direct(serverUrl, identifier)

    /** How this device can unseal a credential right now, or `null` when it cannot unseal anything. */
    val vaultUnlockMode: VaultUnlockMode? get() = container.unlockMode(VaultKind.Connection)

    val logins: List<SavedLogin> get() = revision.let { container.profiles.all() }

    val hasLogins: Boolean get() = logins.isNotEmpty()

    /** Paired Windows controllers are independent of password login records and stay discoverable here. */
    val pairedOwnerDeviceServiceIds: List<String>
        get() = revision.let {
            container.ownerDeviceKeys.registrations().map { it.serviceId }.distinct().sorted()
        }

    val hasConnectionEntries: Boolean get() = hasLogins || pairedOwnerDeviceServiceIds.isNotEmpty()

    /**
     * Whether the debug-only plaintext store may stand in for the vault.
     *
     * Three conditions, all necessary: a debug build (`AppContainer` builds no store otherwise), a
     * device with **no lock screen at all** — the one case where `AndroidKeyStore` cannot create a
     * user-authentication key, so the vault genuinely cannot exist — and the fingerprint master switch
     * left on, because turning it off means "no password is stored at all". It is never a shortcut on a
     * device that could use the vault.
     */
    val debugFallbackAvailable: Boolean
        get() = container.debugCredentials != null &&
            container.appearance.fingerprintEnabled &&
            container.biometricCapability() == BiometricCapability.None

    /** Whether the debug store holds the password for the identity currently in the form. */
    private fun debugCredentialExists(): Boolean {
        val login = selectedLogin
        return login.isComplete &&
            container.hasDebugCredential(login.serviceId, login.normalizedIdentifier)
    }

    /**
     * The saved-credential line for one identity, and the single place the debug fallback is folded in.
     *
     * Both the form and the connection list read this, so the row label and the field's status line can
     * never disagree about whether a password is stored. A usable vault record always wins, and an
     * invalidated one is never papered over: see [app.relaxkonos.mobile.core.auth.credentialState].
     */
    fun savedCredentialStatus(serviceId: String, identifier: String): CredentialStatus {
        val mode = container.unlockMode(VaultKind.Connection)
        val vaultState = credentialState(container.vault.record(VaultKind.Connection, serviceId, identifier), mode)
        val fromDebugStore = debugFallbackAvailable &&
            container.hasDebugCredential(serviceId, identifier) &&
            vaultState != SavedCredentialState.Available &&
            vaultState != SavedCredentialState.Invalidated
        return credentialStatusOf(credentialState(vaultState, fromDebugStore), mode, fromDebugFallback = fromDebugStore)
    }

    /** Whether the credential the sign-in button will use comes out of the debug store. */
    private val usingDebugFallback: Boolean
        get() = debugFallbackAvailable && debugCredentialExists() &&
            credentialStatus == CredentialStatus.SavedInDebugBuild

    /** Whether the identity in the form has a saved password, and whether it can be used right now. */
    val savedCredentialState: SavedCredentialState
        get() = revision.let {
            credentialState(
                credentialState(storedRecord(), container.unlockMode(VaultKind.Connection)),
                usingDebugFallback,
            )
        }

    /** The line rendered beside the password field. Never the password, only the fact. */
    val credentialStatus: CredentialStatus
        get() = revision.let { savedCredentialStatus(selectedLogin.serviceId, selectedLogin.normalizedIdentifier) }

    /** What the sign-in button will do on this click. */
    val decision: LoginDecision?
        get() = decideLogin(selectedLogin, passwordText, savedCredentialState, isLoggingIn)

    /** A key is bound to the normalized server identity, never to an editable display string. */
    val ownerDeviceSignInAvailable: Boolean
        get() = directConnection()?.let(container.ownerDevices::isPaired) == true

    fun changeServer(value: String) {
        discoveryJob?.cancel()
        // Typing an address means the form now describes a direct server, so it stops being a managed
        // selection: the host name and the "establish a tunnel" notice must not outlive that.
        managedHostName = null
        serverUrl = value
        endpointDiscoveryState = EndpointDiscoveryState.Idle
    }

    fun changeIdentifier(value: String) {
        identifier = value
    }

    fun changePassword(value: String) {
        passwordText = value
    }

    fun changeOwnerDevicePairingCode(value: String) {
        ownerDevicePairingCode = value
        message = null
    }

    fun dismissMessage() {
        message = null
    }

    fun openConnections() {
        connectionsOpen = true
        refreshHostOperatingSystems()
    }

    /** Clears the previous session's form state before selecting a login or opening the picker. */
    fun switchLogin(activity: FragmentActivity, login: SavedLogin?) {
        clearSessionInput()
        if (login == null) openConnections() else connectSavedLogin(activity, login)
    }

    fun clearSessionInput() {
        discoveryJob?.cancel()
        hostLookupJob?.cancel()
        passwordText = ""
        ownerDevicePairingCode = ""
        windowUnlocked = emptySet()
        message = null
        focusRequest = null
        endpointDiscoveryState = EndpointDiscoveryState.Idle
        connectionsOpen = false
        revision++
    }

    fun closeConnections() {
        connectionsOpen = false
        // The list is gone, so an answer nobody will see is not worth a socket.
        hostLookupJob?.cancel()
    }

    /**
     * Refreshes every directly reachable host when the picker opens, including previously known marks.
     * The list stays usable while looking up, and failures retain the last observed mark.
     */
    private fun refreshHostOperatingSystems() {
        hostLookupJob?.cancel()
        hostLookupJob = viewModelScope.launch {
            val stored = container.hostOperatingSystems.resolve(
                container.profiles.all().map { it.serviceId } + pairedOwnerDeviceServiceIds,
            )
            if (stored > 0) revision++
        }
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
        val directUrl = login.directServerUrl
        if (directUrl == null) {
            selectManaged(login)
            return
        }
        managedHostName = null
        serverUrl = directUrl
        identifier = login.identifier
        passwordText = ""
        message = null
        connectionsOpen = false
        focusRequest = null
        windowUnlocked = windowUnlocked - loginIdOf(login.serviceId, login.identifier)
    }

    /**
     * Opens a saved login directly. A password-backed record continues through the normal decision
     * table; an absent or unusable password leaves this selected identity in the form for editing.
     */
    fun connectSavedLogin(activity: FragmentActivity, login: SavedLogin) {
        if (isLoggingIn) return
        select(login)
        if (login.directServerUrl != null) {
            submit(activity)
        }
    }

    /** Selects a paired Windows 10/11 owner device and starts its nonce-signature sign-in. */
    fun connectOwnerDevice(activity: FragmentActivity, serviceId: String) {
        if (isLoggingIn) return
        val connection = runCatching {
            app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules.direct(serviceId)
        }.getOrNull()
        if (connection == null || !container.ownerDevices.isPaired(connection)) {
            message = UiMessage(R.string.owner_device_not_paired)
            return
        }
        managedHostName = null
        serverUrl = connection.effectiveBaseUrl
        identifier = ""
        passwordText = ""
        connectionsOpen = false
        message = null
        focusRequest = null
        signInWithOwnerDevice(activity)
    }

    /**
     * Selects a managed login: resolves its host through the server centre and explains what comes next.
     *
     * The host lookup is by installation id, never by address text, so two installations that happen to
     * share an IP are never merged. Nothing is filled into the address field on purpose — a managed login
     * has no persistable server address, and its loopback address only exists after
     * [app.relaxkonos.mobile.servercenter.ManagedLoginTunnelRules.bind] has a tunnel to bind.
     *
     * A missing host record is reported as exactly that, rather than as a generic "address unavailable",
     * which would misread "this device has no host record" as a network problem.
     */
    private fun selectManaged(login: SavedLogin) {
        val host = container.managedLogins.hostFor(login.serviceId)
        managedHostName = host?.displayName
        serverUrl = ""
        identifier = login.identifier
        passwordText = ""
        connectionsOpen = false
        focusRequest = null
        windowUnlocked = windowUnlocked - loginIdOf(login.serviceId, login.identifier)
        message = if (host == null) {
            UiMessage(R.string.login_managed_host_missing)
        } else {
            UiMessage(R.string.login_managed_tunnel_required, listOf(host.displayName))
        }
    }

    /** "Forget the password": drops the credential, keeps the login (§6.3). */
    fun forgetPassword(login: SavedLogin) {
        container.vault.delete(VaultKind.Connection, login.serviceId, login.identifier)
        container.forgetDebugCredential(login.serviceId, login.identifier)
        container.profiles.setHasSavedCredential(login.serviceId, login.identifier, false)
        windowUnlocked = windowUnlocked - loginIdOf(login.serviceId, login.identifier)
        message = null
        revision++
    }

    /** "Delete login record": drops the credential *and* this login, and no other account on it (§6.3). */
    fun deleteLogin(login: SavedLogin) {
        container.vault.delete(VaultKind.Connection, login.serviceId, login.identifier)
        container.forgetDebugCredential(login.serviceId, login.identifier)
        container.profiles.remove(login.serviceId, login.identifier)
        windowUnlocked = windowUnlocked - loginIdOf(login.serviceId, login.identifier)
        revision++
        if (selectedLogin.serviceId == login.serviceId && identifier == login.identifier) {
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
     *
     * The address is resolved first, but resolution is an **optimization and never a gate**. A probe
     * cannot see everything the request can — an endpoint that answers `OPTIONS` oddly, a proxy, a
     * captive portal, a stored address whose scheme or port no longer matches the server — and treating
     * its verdict as a precondition meant the credentials the user had just typed were never sent at
     * all, which reads exactly like "the correct password does not work". The verdict is kept and used
     * to choose the sentence a transport failure shows; the request itself is always attempted.
     */
    fun submit(activity: FragmentActivity) {
        val entered = serverUrl
        if (entered.isBlank()) {
            submitResolved(activity)
            return
        }

        discoveryJob?.cancel()
        endpointDiscoveryState = EndpointDiscoveryState.Checking
        discoveryJob = viewModelScope.launch {
            resolve(entered)
            if (ServerEndpointDiscovery.candidates(entered).any { app.relaxkonos.mobile.core.net.ServerCertificateTrust.review(it) != null }) return@launch
            submitResolved(activity)
        }
    }

    /** Enrols this Android Keystore key with the one-time invitation created on the Windows host. */
    fun pairOwnerDevice(activity: FragmentActivity) {
        if (isLoggingIn) return
        val payload = ownerDevicePairingCode
        if (payload.isBlank()) {
            message = UiMessage(R.string.owner_device_pairing_code_required)
            return
        }
        isLoggingIn = true
        message = null
        viewModelScope.launch {
            try {
                when (val paired = container.ownerDevices.pair(payload)) {
                    is ApiResult.Success -> {
                        managedHostName = null
                        serverUrl = paired.value.effectiveBaseUrl
                        endpointDiscoveryState = EndpointDiscoveryState.Found
                        ownerDevicePairingCode = ""
                        signInWithOwnerDevice(activity, paired.value)
                    }
                    is ApiResult.Problem -> message = loginProblemMessage(paired)
                    is ApiResult.Transport -> message = loginTransportMessage(paired)
                }
            } catch (_: IllegalArgumentException) {
                message = UiMessage(R.string.owner_device_pairing_invalid)
            } catch (error: Exception) {
                message = UiMessage(R.string.error_generic)
                    .withDebugDetail(error.message ?: error::class.java.simpleName)
            } finally {
                isLoggingIn = false
            }
        }
    }

    /** Signs the server nonce with the private key that Android Keystore keeps on this device. */
    fun signInWithOwnerDevice(activity: FragmentActivity) {
        val connection = directConnection()
        if (connection == null) {
            message = UiMessage(R.string.login_missing_fields)
            focusRequest = LoginField.Server
            return
        }
        if (!container.ownerDevices.isPaired(connection)) {
            message = UiMessage(R.string.owner_device_not_paired)
            return
        }
        isLoggingIn = true
        message = null
        viewModelScope.launch {
            try {
                signInWithOwnerDevice(activity, connection)
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                message = UiMessage(R.string.error_generic)
                    .withDebugDetail(error.message ?: error::class.java.simpleName)
            } finally {
                isLoggingIn = false
            }
        }
    }

    private suspend fun signInWithOwnerDevice(activity: FragmentActivity, connection: app.relaxkonos.mobile.servercenter.ServerConnectionIdentity) {
        when (val result = container.session.loginWithOwnerDevice(connection, activity)) {
            is ApiResult.Success -> {
                // Keep the normalized server address in the ordinary connection list. It records no
                // secret and lets the paired-device button remain discoverable after process death.
                rememberLogin(SelectedLogin.resolved(connection, result.value.userName))
            }
            is ApiResult.Problem -> message = loginProblemMessage(result)
            is ApiResult.Transport -> message = loginTransportMessage(result)
        }
    }

    /**
     * Probes the address after it loses focus, purely to give early feedback.
     *
     * It raises no message of its own: leaving a field is not an action yet, and the click is where an
     * answer belongs. A newer edit always cancels the older probe.
     */
    fun discoverServerEndpoint() {
        val entered = serverUrl
        if (entered.isBlank() || isLoggingIn) return

        discoveryJob?.cancel()
        endpointDiscoveryState = EndpointDiscoveryState.Checking
        discoveryJob = viewModelScope.launch { resolve(entered) }
    }

    /**
     * One probe, applied only while the field still holds the text that was probed.
     *
     * A stale verdict overwriting a newer address would send the request somewhere the user did not
     * type, so the guard is not cosmetic.
     */
    private suspend fun resolve(entered: String) {
        var result = ServerEndpointDiscovery.discover(entered)
        val review = ServerEndpointDiscovery.candidates(entered).firstNotNullOfOrNull {
            app.relaxkonos.mobile.core.net.ServerCertificateTrust.review(it)
        }
        if (review != null && serverUrl == entered) {
            val answer = kotlinx.coroutines.CompletableDeferred<Boolean>()
            certificateAnswer = answer
            certificateReview = review
            try {
                if (answer.await() && serverUrl == entered) {
                    if (runCatching { app.relaxkonos.mobile.core.net.ServerCertificateTrust.trust(review) }.isSuccess)
                        result = ServerEndpointDiscovery.discover(entered)
                    else message = UiMessage(R.string.login_server_unavailable)
                }
            } finally { certificateReview = null; certificateAnswer = null }
        }
        if (serverUrl == entered) {
            when (result) {
                is EndpointDiscoveryResult.Found -> {
                    serverUrl = result.serverUrl
                    endpointDiscoveryState = EndpointDiscoveryState.Found
                }

                EndpointDiscoveryResult.InvalidAddress -> endpointDiscoveryState = EndpointDiscoveryState.InvalidAddress

                EndpointDiscoveryResult.Unavailable -> endpointDiscoveryState = EndpointDiscoveryState.Unavailable
            }
        }
        VaultDiagnostics.trace(
            "login.discovery",
            when (result) {
                is EndpointDiscoveryResult.Found -> "found"
                EndpointDiscoveryResult.InvalidAddress -> "invalid-address"
                EndpointDiscoveryResult.Unavailable -> "unavailable"
            },
        )
    }

    private fun submitResolved(activity: FragmentActivity) {
        val plan = decision
        // The decision is the first thing worth having in a log: "the click did nothing" and "the click
        // signed in with something else" are different bugs, and only this line tells them apart.
        VaultDiagnostics.trace(
            "login.decision",
            when (plan) {
                null -> "ignored, a sign-in is already in flight"
                is LoginDecision.ManualPassword -> "manual password"
                LoginDecision.UnlockSavedCredential -> "stored credential"
                is LoginDecision.RequirePassword -> "password required (${plan.gap})"
                is LoginDecision.MissingFields -> "missing fields"
            },
        )
        when (plan) {
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
                    connection = login.connectionIdentity,
                    identifier = login.normalizedIdentifier,
                    password = credential,
                ) {
                    rememberLogin(login)
                    if (rememberCredential) {
                        storeCredential(activity, login, credential)
                    }
                }
                report(result)
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                // AuthSession has already published the successful server session in its finally
                // block. Keep a local persistence failure from being swallowed with the sign-in
                // screen, without ever exposing its raw detail in a release build.
                val failure = UiMessage(R.string.error_generic)
                    .withDebugDetail(error.message ?: error::class.java.simpleName)
                if (container.session.state.value is app.relaxkonos.mobile.core.auth.SessionState.Active) {
                    container.showNotice(failure)
                } else {
                    message = failure
                }
            } finally {
                credential.fill('\u0000')
                isLoggingIn = false
            }
        }
    }

    /**
     * Path two: a stored password is used, after authorization when it comes out of the vault (§5.2).
     *
     * Three things have to hold here, and each of them used to be a way to lose the sign-in button
     * altogether:
     *
     * - the credential may come from the debug plaintext store instead of the vault, which needs no
     *   authorization because nothing protects it;
     * - a click that finds no credential at all must say so rather than returning silently;
     * - the busy flag is cleared in a `finally`, so a failure anywhere in this path cannot leave every
     *   field and the button disabled for the rest of the process.
     */
    private fun signInWithSavedPassword(activity: FragmentActivity) {
        val login = selectedLogin
        val record = storedRecord()
        val mode = container.unlockMode(VaultKind.Connection)
        message = null
        isLoggingIn = true
        viewModelScope.launch {
            try {
                val credential = if (record != null && mode != null) {
                    when (val outcome = unseal(record, activity, mode)) {
                        is VaultOperation.Success -> {
                            if (mode == VaultUnlockMode.DeviceUnlockWindow) {
                                windowUnlocked = windowUnlocked + login.id
                            }
                            outcome.value
                        }

                        // A dismissed prompt is silent: no error, no change to the record (§7.2).
                        VaultOperation.Cancelled -> return@launch

                        is VaultOperation.Failed -> {
                            if (outcome.failure == UnlockFailure.KeyInvalidated) {
                                // One Keystore alias protects the entire connection vault. A dead alias
                                // means every one of its records is unreadable, so mark all of them —
                                // never delete.
                                container.vault.markAllInvalidated(VaultKind.Connection)
                                revision++
                            }
                            VaultDiagnostics.trace("login.unseal", "failed=${outcome.failure}")
                            message = unlockFailureMessage(outcome.failure)
                            return@launch
                        }
                    }
                } else {
                    // No usable vault record, so the debug store is what the button was pointing at.
                    // It has no authorization step — anyone holding the phone can read it — which is
                    // exactly why it exists only in a debug build on a device with no lock screen.
                    val revealed = container.debugCredential(login.serviceId, login.normalizedIdentifier)
                    if (revealed == null) {
                        VaultDiagnostics.trace("login.unseal", "no stored credential to unseal")
                        message = UiMessage(R.string.login_saved_password_unavailable)
                        focusRequest = LoginField.Password
                        return@launch
                    }
                    revealed
                }
                submitUnsealed(login, record, credential)
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                // Nothing in this path is supposed to throw, but a prompt the platform refuses to show
                // or a profile file that cannot commit must not leave the form disabled forever.
                VaultDiagnostics.failure("login.stored-path.failed", error)
                message = UiMessage(R.string.error_generic)
                    .withDebugDetail(error.message ?: error::class.java.simpleName)
            } finally {
                isLoggingIn = false
            }
        }
    }

    /**
     * Signs in with a stored password.
     *
     * A rejected password leaves the stored one exactly as it was, whether the server rejected it or the
     * request never got an answer (§7.3): deletion is an explicit user action, never a side effect of a
     * failed sign-in.
     */
    private suspend fun submitUnsealed(login: SelectedLogin, record: VaultRecord?, credential: CharArray) {
        try {
            val result = container.session.login(login.connectionIdentity, login.normalizedIdentifier, credential) {}
            VaultDiagnostics.trace(
                "login.result",
                when (result) {
                    is ApiResult.Success -> "success (stored credential)"
                    is ApiResult.Problem -> "problem HTTP ${result.status} code=${result.code}"
                    is ApiResult.Transport -> "transport (stored credential)"
                },
            )
            when (result) {
                is ApiResult.Success -> {
                    rememberLogin(login)
                    // Only a vault record carries a "last used" stamp; the debug store has none.
                    record?.let { container.vault.markUsed(it, System.currentTimeMillis()) }
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
            subtitle = promptSubtitle(record.serviceId),
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
            // The vault cannot exist on this device. A debug build can fall back to the plaintext store,
            // which is the entire reason it exists; the notice says out loud that nothing protects it.
            // Every other build — and every device that could host a key — keeps the honest refusal.
            if (debugFallbackAvailable) {
                container.debugCredentials?.save(login.serviceId, login.normalizedIdentifier, credential)
                container.profiles.setHasSavedCredential(login.serviceId, login.normalizedIdentifier, true)
                VaultDiagnostics.trace("login.debug-store", "saved unencrypted (debug build, device has no lock screen)")
                revision++
                container.showNotice(UiMessage(R.string.login_credential_saved_debug))
                return
            }
            container.showNotice(UiMessage(R.string.login_credential_not_saved))
            return
        }
        val outcome = try {
            container.vaultAccess.save(
                kind = VaultKind.Connection,
                mode = mode,
                serviceId = login.serviceId,
                account = login.normalizedIdentifier,
                password = credential,
                activity = activity,
                title = saveTitle(),
                subtitle = saveSubtitle(login.serviceId),
                negativeButton = promptCancel(),
                nowEpochMillis = System.currentTimeMillis(),
            )
        } catch (_: VaultKeyInvalidatedException) {
            VaultOperation.Failed(UnlockFailure.KeyInvalidated)
        }
        revision++
        when (outcome) {
            is VaultOperation.Success ->
                container.profiles.setHasSavedCredential(login.serviceId, login.normalizedIdentifier, true)

            VaultOperation.Cancelled -> container.showNotice(UiMessage(R.string.login_credential_not_saved))

            is VaultOperation.Failed -> container.showNotice(
                UiMessage(
                    R.string.login_credential_not_saved_reason,
                    listOf(unlockFailureLabel(getApplication<Application>(), outcome.failure)),
                ),
            )
        }
    }

    /** Records the login itself — never a secret — and keeps the credential projection truthful. */
    private fun rememberLogin(login: SelectedLogin) {
        val existing = container.profiles
            .all()
            .firstOrNull { it.serviceId == login.serviceId && it.identifier == login.normalizedIdentifier }
        container.profiles.upsert(
            SavedLogin(
                serviceId = login.serviceId,
                identifier = login.normalizedIdentifier,
                lastUsedEpochMillis = System.currentTimeMillis(),
                displayName = existing?.displayName,
                hasSavedCredential = container.vault
                    .record(VaultKind.Connection, login.serviceId, login.normalizedIdentifier) != null,
                // Signing in is not a reason to forget what the host already told us about itself.
                hostOperatingSystem = existing?.hostOperatingSystem,
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
        return container.vault.record(VaultKind.Connection, login.serviceId, login.normalizedIdentifier)
    }

    private fun loginIdOf(serviceId: String, identifier: String): String = loginId(serviceId, identifier)

    private fun directConnection() = if (managedHostName == null && serverUrl.isNotBlank()) {
        runCatching { app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules.direct(serverUrl) }.getOrNull()
    } else {
        null
    }

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

    /**
     * A transport result is also used for malformed responses and non-JSON 429/5xx responses.
     *
     * When the address probe could not reach a login endpoint either, that is the more useful sentence:
     * it says "wrong address" instead of "network problem". The probe chooses the sentence, never
     * whether the request is sent — see [submit].
     */
    private fun loginTransportMessage(result: ApiResult.Transport): UiMessage = when (endpointDiscoveryState) {
        EndpointDiscoveryState.Unavailable -> UiMessage(R.string.login_server_unavailable)
        EndpointDiscoveryState.InvalidAddress -> UiMessage(R.string.login_server_invalid)
        else -> UiMessage(R.string.error_connectivity)
    }.withDebugDetail(result.detail)

    private fun promptTitle() = getApplication<Application>().getString(R.string.vault_unlock_title)

    private fun promptSubtitle(target: String) =
        getApplication<Application>().getString(R.string.vault_unlock_subtitle, target)

    private fun promptCancel() = getApplication<Application>().getString(R.string.common_cancel)

    private fun saveTitle() = getApplication<Application>().getString(R.string.vault_save_connection_title)

    private fun saveSubtitle(target: String) =
        getApplication<Application>().getString(R.string.vault_save_connection_subtitle, target)
}
