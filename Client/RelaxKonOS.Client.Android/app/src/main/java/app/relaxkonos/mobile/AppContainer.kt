package app.relaxkonos.mobile

import android.app.Application
import android.content.Context
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.RelaxKonApi
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.data.ConnectionProfileStore
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import app.relaxkonos.mobile.data.ElevationCoordinator
import app.relaxkonos.mobile.data.ElevationRepository
import app.relaxkonos.mobile.data.FileProfileStorage
import app.relaxkonos.mobile.data.FilesRepository
import app.relaxkonos.mobile.data.RecentOperationJournal
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.security.AndroidBiometricCapabilityDetector
import app.relaxkonos.mobile.security.BiometricCapability
import app.relaxkonos.mobile.security.BiometricCapabilityDetector
import app.relaxkonos.mobile.security.BiometricUnlock
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.FileVaultStorage
import app.relaxkonos.mobile.security.VaultAccess
import app.relaxkonos.mobile.security.VaultDiagnostics
import app.relaxkonos.mobile.security.VaultKeyManager
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.security.unlockModeFor
import app.relaxkonos.mobile.ui.theme.AppearancePreferences
import app.relaxkonos.mobile.ui.theme.AppearanceState
import app.relaxkonos.mobile.ui.common.UiMessage

/**
 * Composition root.
 *
 * Everything that must outlive a screen lives here: the in-memory session, the two credential vaults,
 * the connection profiles and the elevation coordinator. Screens receive this object and never build
 * their own gateways, so there is exactly one access token and one definition of "unlockable".
 *
 * It is owned by [RelaxKonApplication] rather than by an activity because a locale change or a
 * configuration change recreates the activity, and neither should cost the user a new fingerprint
 * confirmation.
 */
class AppContainer(context: Context) {
    private val appContext = context.applicationContext

    val preferences = AppearancePreferences(appContext)

    /** Observable appearance settings; the shell reads colour mode and language through this. */
    val appearance = AppearanceState(preferences)

    val biometrics: BiometricCapabilityDetector = AndroidBiometricCapabilityDetector(appContext)

    val keyManager = VaultKeyManager()

    val vault = CredentialVault(FileVaultStorage(appContext.noBackupFilesDir), keyManager)

    val vaultAccess = VaultAccess(vault, keyManager, BiometricUnlock())

    val profiles = ConnectionProfileStore(FileProfileStorage(appContext.noBackupFilesDir))

    /**
     * A user-visible notice that survives the sign-in-to-shell composition switch.
     *
     * Saving a credential happens while the login screen is still mounted, but its outcome is known
     * immediately before the successful session switches that screen out. Keeping the notice at the
     * process-owned composition root prevents a real "signed in, but password was not saved" result
     * from being lost with the login ViewModel.
     */
    var pendingNotice by mutableStateOf<UiMessage?>(null)
        private set

    fun showNotice(notice: UiMessage) {
        pendingNotice = notice
    }

    fun dismissNotice() {
        pendingNotice = null
    }

    /**
     * Reconciles the stored credential projection against the vault, once per process.
     *
     * The vault owns the answer to "is a password saved"; the projection in the profile file is only a
     * convenience for lists. Reconciling here means a projection left behind by an interrupted save, or
     * written by an older build, cannot outlive the record it describes
     * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §2.2, §4.2). Reading the vault is not a biometric
     * operation: only the payload is encrypted, and it stays encrypted.
     */
    init {
        val stored = vault.records(VaultKind.Connection).map { it.serverUrl to it.account }.toSet()
        profiles.reconcileCredentialProjection { serverUrl, identifier -> (serverUrl to identifier) in stored }
    }

    val gateway: RelaxKonGateway = RelaxKonApi(clientVersion = BuildConfig.VERSION_NAME)

    val session = AuthSession(gateway)

    val elevations = ElevationRepository(gateway, session, vault)

    val files = FilesRepository(gateway, session, elevations)

    val system = SystemRepository(gateway, session)

    /** Small, memory-only success journal shown on the expanded home layout. */
    val recentOperations = RecentOperationJournal()

    /** Pending elevation prompts; a screen renders the dialog and answers it. */
    val elevationPrompts = ElevationCoordinator()

    /** What the server said it can do, or an empty set before sign-in. */
    val capabilities: Set<String>
        get() = (session.state.value as? SessionState.Active)?.capabilities.orEmpty()

    val activeSession: SessionState.Active?
        get() = session.state.value as? SessionState.Active

    /**
     * How [kind] can be unlocked right now, or `null` when it must stay disabled.
     *
     * The fingerprint master switch gates both vaults, and [unlockModeFor] applies the product rules:
     * the elevation vault only ever accepts strong per-use biometrics (D1), while the connection
     * vault may fall back to the device-unlock window (D2, D3).
     *
     * The probe runs on every call on purpose. Caching it would let a "usable" answer survive the
     * user removing their fingerprints or turning the master switch off.
     */
    fun unlockMode(kind: VaultKind): VaultUnlockMode? {
        val mode = if (!appearance.fingerprintEnabled) {
            null
        } else {
            unlockModeFor(kind, biometrics.detect(), deviceUnlockWindowEnabled = true)
        }
        // Only transitions are logged: the probe above deliberately runs on every call, so logging
        // every answer would bury the one line that changed. Nothing about the answer is cached.
        val previous = lastUnlockModes[kind]
        val seen = lastUnlockModes.containsKey(kind)
        lastUnlockModes[kind] = mode
        if (!seen || previous != mode) {
            VaultDiagnostics.trace(
                "unlock.mode",
                "${kind.name} => ${mode ?: "null"} (fingerprintEnabled=${appearance.fingerprintEnabled})",
            )
        }
        return mode
    }

    /** Last `unlockMode` answer per vault, for log de-duplication only. Never read as a decision. */
    private val lastUnlockModes = mutableMapOf<VaultKind, VaultUnlockMode?>()

    /** The live authenticator tier, for the account and security page. */
    fun biometricCapability(): BiometricCapability = biometrics.detect()

    /**
     * The account name of the stored host administrator credential, if any.
     *
     * It is only ever an account name; the password itself stays sealed inside the elevation vault and
     * is released exclusively through an authorized [VaultAccess.load].
     */
    fun savedAdministratorAccount(): String? =
        vault.records(VaultKind.Elevation).firstOrNull()?.account

    /**
     * The answer supplier handed to the repositories.
     *
     * The elevation dialog is its only implementation, which is what makes "nothing is elevated
     * without an explicit user answer" structural rather than conventional
     * (`RelaxKonOS.Mobile.V1.Design.md` §5.3.8).
     */
    val elevationAnswers = ElevationAnswerProvider { capability, target ->
        elevationPrompts.request(capability, target, savedAdministratorAccount())
    }
}

/**
 * Holds the process-wide [AppContainer] so a recreated activity reuses the same session and vaults.
 *
 * It also installs the vault's diagnostic sink, and only in a debug build: the Keystore behaviour
 * behind the credential vaults cannot be reproduced in a JVM test, so a real device needs a way to
 * report which platform refusal it hit. A release build leaves the sink unset, so the security
 * package never reaches for `android.util.Log` at all.
 */
class RelaxKonApplication : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        if (BuildConfig.DEBUG) {
            VaultDiagnostics.sink = { event, detail ->
                Log.d(VaultDiagnostics.TAG, if (detail == null) event else "$event: $detail")
            }
        }
        container = AppContainer(this)
    }
}
