package app.relaxkonos.mobile.servercenter

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.security.UnlockFailure
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultOperation
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultUnlockMode

/**
 * Process-owned navigation state for the server centre.
 *
 * It owns no plaintext password of its own, and the two places an SSH password can live are kept
 * apart on purpose:
 *
 * - **this session's cache** ([verifiedSessionPasswords]) exists only while Server Centre is open, so
 *   a host verified once can be re-checked silently. Closing Server Centre zeroes it;
 * - **the device vault** ([credentials], `VaultKind.Ssh`), which is written only after an explicit
 *   opt-in and a successful handshake, and read only after a fingerprint or screen-lock confirmation.
 *
 * Neither is ever written to a target, a navigation argument or a deployment request.
 */
class ServerCenterCoordinator(
    private val targets: ServerHostTargetStore,
    private val connections: ServerCenterConnectionResolver,
    private val hostKeys: ServerHostKeyTrustStore,
    private val installOperations: ServerInstallOperationIndex,
    private val credentials: ServerCenterSshCredentialStore,
    private val unlockModeOf: (VaultKind) -> VaultUnlockMode?,
) {
    var isOpen by mutableStateOf(false)
        private set

    /** The SSH browser is a child of Server Centre, never a sixth top-level destination. */
    var sshFilesHostId by mutableStateOf<String?>(null)
        private set

    /** In-memory only credentials for hosts verified during this Server Centre session. */
    private val verifiedSessionPasswords = mutableMapOf<String, CharArray>()

    var revision by mutableIntStateOf(0)
        private set

    fun open() {
        isOpen = true
        revision++
    }

    fun close() {
        isOpen = false
        closeSshFiles()
        verifiedSessionPasswords.values.forEach { it.fill('\u0000') }
        verifiedSessionPasswords.clear()
    }

    /** Remembers a password only after a trusted handshake, and only until Server Centre closes. */
    fun rememberVerifiedPassword(hostId: String, password: CharArray) {
        require(targets.find(hostId) != null) { "Unknown host target '$hostId'." }
        verifiedSessionPasswords.remove(hostId)?.fill('\u0000')
        verifiedSessionPasswords[hostId] = password.copyOf()
    }

    /** The caller owns and must clear the returned copy. */
    fun verifiedPasswordCopy(hostId: String): CharArray? = verifiedSessionPasswords[hostId]?.copyOf()

    /** Whether this host was already verified in this Server Centre session, so no prompt is needed. */
    fun hasSessionPassword(hostId: String): Boolean = verifiedSessionPasswords.containsKey(hostId)

    fun openSshFiles(hostId: String, password: CharArray) {
        require(targets.find(hostId) != null) { "Unknown host target '$hostId'." }
        rememberVerifiedPassword(hostId, password)
        sshFilesHostId = hostId
    }

    fun workspacePasswordCopy(): CharArray? = sshFilesHostId?.let(::verifiedPasswordCopy)

    fun closeSshFiles() {
        sshFilesHostId = null
    }

    fun hosts(): List<ServerHostTarget> {
        // Reading revision establishes a Compose observation point after a successful mutation.
        revision
        return targets.all()
    }

    fun addHost(host: String, port: Int, userName: String, displayName: String?): ServerHostTarget {
        val target = saveHost(ServerHostTargetRules.create(host, port, userName, displayName, System.currentTimeMillis()))
        return target
    }

    /** Persists a target only after the caller has completed its SSH verification workflow. */
    fun saveHost(target: ServerHostTarget): ServerHostTarget {
        val saved = targets.upsert(target)
        revision++
        return saved
    }

    /**
     * Persists an authoritative SSH-side status receipt.  The view must still label this as a
     * timestamped cache: it is evidence from the probe, never a claim that API health is live now.
     * A missing or untrusted installation id does not erase an existing managed identity.
     */
    fun recordVerifiedSnapshot(hostId: String, snapshot: ServerHostSnapshot): ServerHostTarget? {
        val target = targets.find(hostId) ?: return null
        val verified = ServerHostVerifiedState(
            installed = snapshot.installed,
            mode = snapshot.mode,
            installationId = snapshot.installationId,
            version = snapshot.version,
            listenUrl = snapshot.listenUrl,
            healthy = snapshot.healthy,
            verifiedAtEpochMillis = System.currentTimeMillis(),
        )
        val updated = ServerHostTargetRules.applyVerifiedState(target, verified, System.currentTimeMillis())
        targets.upsert(updated)
        revision++
        return updated
    }

    /** Removes only device-local management metadata; it never uninstalls the server or clears credentials. */
    fun removeHost(hostId: String): Boolean {
        if (targets.find(hostId) == null) return false
        installOperations.forgetHost(hostId)
        val removed = targets.remove(hostId)
        if (removed) {
            verifiedSessionPasswords.remove(hostId)?.fill('\u0000')
            revision++
        }
        return removed
    }

    // ---- Saved SSH credentials (the device vault) ------------------------------------------
    // The vault record is the durable half of "the password is saved"; this session's cache is the
    // other. Every method here reads metadata only — a plaintext password leaves the vault through
    // [unsealCredential] alone.

    /** The vault record for one host, if the user ever chose to save its SSH password. */
    fun savedCredential(target: ServerHostTarget): VaultRecord? =
        credentials.record(target.sshHost, target.sshPort, target.sshUserName)

    fun savedCredential(hostId: String): VaultRecord? = targets.find(hostId)?.let(::savedCredential)

    /** How [VaultKind.Ssh] can be unlocked here and now, or `null` when this device cannot protect it. */
    fun unlockMode(): VaultUnlockMode? = unlockModeOf(VaultKind.Ssh)

    /**
     * The explicit "forget the saved password" action. It removes the credential only: host metadata,
     * the pinned host key and the in-session password all stay, because being logged in is not what
     * the user asked to undo.
     */
    fun forgetSavedCredential(hostId: String): Boolean {
        val record = savedCredential(hostId) ?: return false
        credentials.forget(record)
        revision++
        return true
    }

    /**
     * Writes a password that was just proven correct into the vault.
     *
     * `null` means this device cannot protect a saved password at all (no fingerprint, no lock
     * screen); the caller must report "not saved" rather than pretend otherwise. SSH has no plaintext
     * fallback (`ServerCenter.md` §3).
     *
     * [password] is handed over: this method zeroes it, so the caller must pass a throwaway copy.
     */
    suspend fun saveCredential(
        target: ServerHostTarget,
        password: CharArray,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
    ): VaultOperation<VaultRecord>? {
        val mode = unlockModeOf(VaultKind.Ssh) ?: return null
        val secret = SshCredential(SshCredentialKind.Password, password, null)
        val outcome = try {
            credentials.save(
                mode = mode,
                host = target.sshHost,
                port = target.sshPort,
                userName = target.sshUserName,
                credential = secret,
                activity = activity,
                title = title,
                subtitle = subtitle,
                negativeButton = negativeButton,
                nowEpochMillis = System.currentTimeMillis(),
            )
        } finally {
            // The vault already encoded the plaintext into its ciphertext; the plaintext is done.
            secret.clear()
        }
        if (outcome is VaultOperation.Success) revision++
        return outcome
    }

    /**
     * Unseals the saved SSH password for one host.
     *
     * In device-window mode an unattended read is attempted first: inside the still-open window the
     * Keystore key can be used again without a prompt. That read is an optimization, never a gate —
     * an expired window or a locked device simply falls through to the authorized path. A permanently
     * invalidated alias marks the whole SSH domain unreadable (it never deletes anything).
     */
    suspend fun unsealCredential(
        target: ServerHostTarget,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
    ): VaultOperation<SshCredential> {
        val record = savedCredential(target) ?: return VaultOperation.Failed(UnlockFailure.Unavailable)
        val mode = unlockModeOf(VaultKind.Ssh) ?: return VaultOperation.Failed(UnlockFailure.Unavailable)
        if (mode == VaultUnlockMode.DeviceUnlockWindow) {
            val unattended = credentials.loadWithoutPrompt(record)
            if (unattended is VaultOperation.Success) return unattended
        }
        val outcome = credentials.load(mode, record, activity, title, subtitle, negativeButton)
        if (outcome is VaultOperation.Failed && outcome.failure == UnlockFailure.KeyInvalidated) {
            credentials.markAllInvalidated()
            revision++
        }
        return outcome
    }

    /** A read-only SSH handshake. No remote command is run until the launcher workflow begins. */
    suspend fun verifySsh(hostId: String, credential: SshCredential): ServerCenterSshVerification {
        val target = targets.find(hostId)
            ?: return ServerCenterSshVerification.Failed(SshFailureReason.Unexpected)
        return verifySsh(target, credential)
    }

    /**
     * Verifies an as-yet-unsaved target during "add and verify".  Keeping this separate from
     * [addHost] is important: an unreachable host or wrong password must not create a durable
     * management record.
     */
    suspend fun verifySsh(target: ServerHostTarget, credential: SshCredential): ServerCenterSshVerification = try {
        connections.connect(target, credential, connections.prepareHostKeyGuard(target)).use { session ->
            ServerCenterSshVerification.Trusted(session.observedHostKey?.fingerprint)
        }
    } catch (rejected: ServerCenterHostKeyRejectedException) {
        val observation = rejected.observation
        when (rejected.trust) {
            ServerHostKeyTrust.Unknown -> ServerCenterSshVerification.NeedsTrust(observation)

            // 变更时必须把被取代的那条记录一并交给界面（判定与展示规则见 [SshHostKeyReview]）。
            // 若记录在握手间隙被清掉，那这次就不是「变更」而是首次核对，如实降级——不伪造旧指纹。
            ServerHostKeyTrust.Changed ->
                hostKeys.find(observation.host, observation.port, observation.algorithm)
                    ?.let { ServerCenterSshVerification.KeyChanged(observation, it) }
                    ?: ServerCenterSshVerification.NeedsTrust(observation)

            // 传输层只在守卫未接受密钥时才抛本异常，因此这里按构造不可达。宁可如实归为「无法归类」，
            // 也不要顺手把它当成一次指纹确认请求。
            ServerHostKeyTrust.Trusted ->
                ServerCenterSshVerification.Failed(SshFailureRules.classify(rejected))
        }
    } catch (error: Exception) {
        // The raw SSH exception may disclose usernames, paths, or library details; only its
        // classified reason reaches the UI, and the full detail stays in the debug-only sink.
        ServerCenterSshVerification.Failed(SshFailureRules.classify(error))
    } finally {
        credential.clear()
    }

    /** Only called from the explicit fingerprint-confirmation action. */
    fun trustHostKey(target: ServerHostTarget, observation: ServerCenterHostKeyObservation) {
        val endpoint = ServerCenterSshEndpoint.create(target.sshHost, target.sshPort, target.sshUserName)
        require(endpoint.host == ServerHostTrustRules.normalizeHost(observation.host) && endpoint.port == observation.port) {
            "The observed key does not belong to the selected host."
        }
        hostKeys.trust(endpoint, observation, System.currentTimeMillis())
        revision++
    }
}

sealed interface ServerCenterSshVerification {
    data class Trusted(val fingerprint: String?) : ServerCenterSshVerification
    data class NeedsTrust(val observation: ServerCenterHostKeyObservation) : ServerCenterSshVerification
    /**
     * 之前固定的密钥与本次握手不一致。
     *
     * [previous] 是那条被取代的固定记录，必须随之交给界面：用户要判断「这是我把系统重装或重建了」
     * 还是「这个地址现在被另一台机器占用」，只能靠并排看到旧指纹。它由 [ServerCenterCoordinator.verifySsh]
     * 在判定为变更时一并读出，因此恒非空——读不到旧记录时那本来就不是「变更」而是首次核对。
     */
    data class KeyChanged(
        val observation: ServerCenterHostKeyObservation,
        val previous: ServerHostKeyRecord,
    ) : ServerCenterSshVerification

    /**
     * 握手失败。原因必须随结果一起交给界面：把「手机连不上主机」和「密码被拒」压成同一句话，
     * 用户只能逐个猜测（归类规则见 [SshFailureRules]）。
     */
    data class Failed(val reason: SshFailureReason) : ServerCenterSshVerification
}
