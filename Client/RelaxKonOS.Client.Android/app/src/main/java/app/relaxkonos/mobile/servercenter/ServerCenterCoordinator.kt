package app.relaxkonos.mobile.servercenter

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * Process-owned navigation state for the server centre.
 *
 * It owns no persisted SSH password or deployment request.  Passwords accepted by a successful
 * SSH handshake remain only in process memory while Server Centre is open, so a verified host can
 * be re-checked without asking again.  They are never written to a target, navigation argument or
 * credential store.
 */
class ServerCenterCoordinator(
    private val targets: ServerHostTargetStore,
    private val connections: ServerCenterConnectionResolver,
    private val hostKeys: ServerHostKeyTrustStore,
    private val installOperations: ServerInstallOperationIndex,
) {
    var isOpen by mutableStateOf(false)
        private set

    /** The SSH browser is a child of Server Centre, never a sixth top-level destination. */
    var sshFilesHostId by mutableStateOf<String?>(null)
        private set

    /** In-memory only credentials for hosts verified during this Server Centre session. */
    private val verifiedSessionPasswords = mutableMapOf<String, CharArray>()

    var workspaceRevision by mutableIntStateOf(0)
        private set

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

    fun openSshFiles(hostId: String, password: CharArray) {
        require(targets.find(hostId) != null) { "Unknown host target '$hostId'." }
        if (sshFilesHostId != null) closeSshFiles()
        rememberVerifiedPassword(hostId, password)
        workspaceRevision++
        sshFilesHostId = hostId
    }

    fun workspacePasswordCopy(): CharArray? = sshFilesHostId?.let(::verifiedPasswordCopy)

    var onWorkspaceClosed: () -> Unit = {}

    fun closeSshFiles() {
        onWorkspaceClosed()
        workspaceRevision++
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
            if (sshFilesHostId == hostId) closeSshFiles()
            verifiedSessionPasswords.remove(hostId)?.fill('\u0000')
            revision++
        }
        return removed
    }

    /** A read-only SSH handshake. No remote command is run until the launcher workflow begins. */
    suspend fun verifySsh(hostId: String, credential: SshCredential): ServerCenterSshVerification {
        val target = targets.find(hostId)
            ?: return ServerCenterSshVerification.Failed
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
        when (rejected.trust) {
            ServerHostKeyTrust.Unknown -> ServerCenterSshVerification.NeedsTrust(rejected.observation)
            ServerHostKeyTrust.Changed -> ServerCenterSshVerification.KeyChanged(rejected.observation)
            ServerHostKeyTrust.Trusted -> ServerCenterSshVerification.Failed
        }
    } catch (_: Exception) {
        // The raw SSH exception may disclose usernames, paths, or library details; it stays out of UI.
        ServerCenterSshVerification.Failed
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
    data class KeyChanged(val observation: ServerCenterHostKeyObservation) : ServerCenterSshVerification
    data object Failed : ServerCenterSshVerification
}
