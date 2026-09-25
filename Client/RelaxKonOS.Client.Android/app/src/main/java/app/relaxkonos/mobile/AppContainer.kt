package app.relaxkonos.mobile

import android.app.Application
import android.content.Context
import android.os.Build
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.RelaxKonApi
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.data.AndroidUploadDocuments
import app.relaxkonos.mobile.data.BitmapFactoryImageDecoder
import app.relaxkonos.mobile.data.ConnectionProfileStore
import app.relaxkonos.mobile.data.DownloadStore
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import app.relaxkonos.mobile.data.ElevationCoordinator
import app.relaxkonos.mobile.data.ElevationRepository
import app.relaxkonos.mobile.data.FileProfileStorage
import app.relaxkonos.mobile.data.FilesRepository
import app.relaxkonos.mobile.data.ImageDecoder
import app.relaxkonos.mobile.data.ImagePreviewCache
import app.relaxkonos.mobile.data.PREVIEW_CACHE_DIRECTORY
import app.relaxkonos.mobile.data.RecentOperationJournal
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.data.UploadCoordinator
import app.relaxkonos.mobile.data.UploadResumeJournal
import app.relaxkonos.mobile.data.UploadSourceStager
import app.relaxkonos.mobile.security.AndroidBiometricCapabilityDetector
import app.relaxkonos.mobile.security.AndroidBiometricUnlock
import app.relaxkonos.mobile.security.BiometricCapability
import app.relaxkonos.mobile.security.BiometricCapabilityDetector
import app.relaxkonos.mobile.security.BiometricUnlock
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.DebugCredentialStore
import app.relaxkonos.mobile.security.FileDebugCredentialStorage
import app.relaxkonos.mobile.security.FileVaultStorage
import app.relaxkonos.mobile.security.VaultAccess
import app.relaxkonos.mobile.security.VaultDiagnostics
import app.relaxkonos.mobile.security.VaultKeyManager
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.security.unlockModeFor
import app.relaxkonos.mobile.servercenter.DefaultServerCenterConnectionResolver
import app.relaxkonos.mobile.servercenter.FileHostKeyStorage
import app.relaxkonos.mobile.servercenter.FileHostTargetStorage
import app.relaxkonos.mobile.servercenter.JschServerCenterSshTransportFactory
import app.relaxkonos.mobile.servercenter.ServerCenterConnectionResolver
import app.relaxkonos.mobile.servercenter.ServerCenterSshCredentialStore
import app.relaxkonos.mobile.servercenter.ServerHostKeyTrustStore
import app.relaxkonos.mobile.servercenter.ServerHostTargetStore
import app.relaxkonos.mobile.ui.theme.AppearancePreferences
import app.relaxkonos.mobile.ui.theme.AppearanceState
import app.relaxkonos.mobile.ui.common.UiMessage
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

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

    /**
     * Scope for work that must outlive every screen.
     *
     * Only an upload uses it today, and that is the point: a multi-gigabyte transfer cannot be tied to
     * the page that started it, and it has to survive that page being destroyed.
     */
    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    val preferences = AppearancePreferences(appContext)

    /** Observable appearance settings; the shell reads colour mode and language through this. */
    val appearance = AppearanceState(preferences)

    val biometrics: BiometricCapabilityDetector = AndroidBiometricCapabilityDetector(appContext)

    val keyManager = VaultKeyManager()

    val vault = CredentialVault(FileVaultStorage(appContext.noBackupFilesDir), keyManager)

    val vaultAccess = VaultAccess(vault, keyManager, AndroidBiometricUnlock())

    val profiles = ConnectionProfileStore(FileProfileStorage(appContext.noBackupFilesDir))

    /**
     * 服务器中心的宿主管理资料与主机密钥信任资料。
     *
     * 两者都落在 `noBackupFilesDir` 且不与 Workspace 同步：宿主目标只描述本机对某台宿主的管辖关系，
     * 固定主机密钥也只对本机有意义。它们都不含凭据——SSH 凭据另走 [VaultKind.Ssh] 保险箱域，
     * 与登录、提权凭据互不复用。
     */
    val serverHostTargets = ServerHostTargetStore(FileHostTargetStorage(appContext.noBackupFilesDir))

    val sshHostKeyTrust = ServerHostKeyTrustStore(FileHostKeyStorage(appContext.noBackupFilesDir))

    val sshCredentials = ServerCenterSshCredentialStore(vault, vaultAccess)

    /**
     * 连接解析器：把宿主目标与 SSH 凭据变成一条会话，并维护 loopback 隧道的稳定身份。
     * 传输由内置 JSch 提供，因此不需要外部 SSH App。
     */
    val serverCenterConnections: ServerCenterConnectionResolver = DefaultServerCenterConnectionResolver(
        hostKeyTrust = sshHostKeyTrust,
        hostTargets = serverHostTargets,
        transportFactory = JschServerCenterSshTransportFactory(),
    )

    /**
     * Plaintext fallback for a device that cannot host a Keystore key at all.
     *
     * `null` in every release build, which is what keeps it from being a shipping feature: with no
     * instance there is no code path that could write the file. It is offered to the sign-in screen
     * only while the device reports no lock screen at all, i.e. exactly when the vault is impossible —
     * never as a shortcut on a device that could use the vault (`DebugCredentialStore`).
     */
    val debugCredentials: DebugCredentialStore? =
        if (BuildConfig.DEBUG) DebugCredentialStore(FileDebugCredentialStorage(appContext.noBackupFilesDir)) else null

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
        // The debug store counts as a credential here too: it is a real, readable password for this
        // device, and a list claiming "no password saved" while one sits in a file would be lying
        // about what is on disk.
        profiles.reconcileCredentialProjection { serverUrl, identifier ->
            (serverUrl to identifier) in stored || hasDebugCredential(serverUrl, identifier)
        }
    }

    val gateway: RelaxKonGateway = RelaxKonApi(clientVersion = BuildConfig.VERSION_NAME)

    val session = AuthSession(gateway)

    val elevations = ElevationRepository(gateway, session, vault)

    val files = FilesRepository(gateway, session, elevations)

    /** Resolves where a downloaded file lands on this device; see `DownloadStore`. */
    val downloads = DownloadStore(appContext)

    /**
     * Where the images the user looked at are cached.
     *
     * It lives in `cacheDir` on purpose: the platform may empty it, and a preview is only ever a copy
     * of a file that is still on the host (`ImagePreviewCache`).
     */
    val imagePreviews = ImagePreviewCache(File(appContext.cacheDir, PREVIEW_CACHE_DIRECTORY))

    /** Turns a cached preview file into a bitmap, subsampled to what the screen can actually show. */
    val imageDecoder: ImageDecoder = BitmapFactoryImageDecoder()

    val system = SystemRepository(gateway, session)

    /**
     * Where an unfinished upload is remembered.
     *
     * Kept apart from the credential vault and deliberately not encrypted: it holds a session id, a
     * target path and a source URI, and no credential at all. Putting it in `noBackupFilesDir` also
     * keeps it off a restored device, where the sessions it names are long gone.
     */
    private val uploadJournal = UploadResumeJournal(File(appContext.noBackupFilesDir, UPLOAD_JOURNAL_FILE))

    /** Staging area for a document a provider cannot read from an offset. Emptied by the platform, as a cache should be. */
    private val uploadStager = UploadSourceStager(UploadSourceStager.defaultCacheRoot(appContext.cacheDir))

    /**
     * Opens the documents the picker returns.
     *
     * Exposed rather than hidden inside [uploads] because the single-request route needs the same
     * document description — name, length, a stream — and both routes must agree on what was picked.
     */
    val uploadDocuments = AndroidUploadDocuments(appContext)

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
     * The debug-only plaintext credential for one identity, or `null` when there is none.
     *
     * Always `null` in a release build, where [debugCredentials] is `null`. The returned array belongs
     * to the caller, which zeroes it exactly like an unsealed vault record.
     */
    fun debugCredential(serverUrl: String, identifier: String): CharArray? =
        debugCredentials?.reveal(serverUrl, identifier)

    /** Whether the debug store holds a password for one identity. Never a security decision. */
    fun hasDebugCredential(serverUrl: String, identifier: String): Boolean =
        debugCredentials?.exists(serverUrl, identifier) == true

    /** Drops the debug-only credential, for the explicit "forget password" and delete actions. */
    fun forgetDebugCredential(serverUrl: String, identifier: String) {
        debugCredentials?.delete(serverUrl, identifier)
    }

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

    /**
     * Runs resumable uploads for the lifetime of the process.
     *
     * Declared last because it depends on [elevationAnswers], which is itself declared here: an
     * initialiser that ran earlier would read a property that has not been assigned yet.
     *
     * App-scoped on purpose: a multi-gigabyte transfer must not be tied to the screen that started it,
     * and the foreground service that keeps it alive only renders progress — it does not own the work.
     */
    val uploads = UploadCoordinator(
        gateway = gateway,
        session = session,
        elevations = elevations,
        journal = uploadJournal,
        stager = uploadStager,
        documents = uploadDocuments,
        elevationAnswers = elevationAnswers,
        serverKey = ::uploadServerKey,
        scope = appScope,
    )

    /**
     * The identity an unfinished upload belongs to: server, account, workspace and this device.
     *
     * Same shape as the desktop client's session key, and for the same reason: a session id is only
     * meaningful to the identity that opened it, so an entry from another server — or another account on
     * the same server — must never be offered as something to continue.
     */
    private fun uploadServerKey(): String? {
        val active = session.state.value as? SessionState.Active ?: return null
        val device = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
        return "${active.serverUrl}|${active.userName}|${active.workspaceName}|$device"
    }

    /**
     * Keeps the unfinished-upload list in step with the signed-in identity.
     *
     * Signing in drops entries belonging to another server and any cache copy nothing owns; signing out
     * empties the list, because an entry that cannot be sent to anywhere is not something to offer. This
     * runs here rather than in a screen so it happens even when the user never opens Files.
     */
    init {
        appScope.launch {
            session.state.collect { state ->
                if (state is SessionState.Active) uploads.forgetOtherServers() else uploads.restore()
            }
        }
    }

    private companion object {
        const val UPLOAD_JOURNAL_FILE = "upload-resume.txt"
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
