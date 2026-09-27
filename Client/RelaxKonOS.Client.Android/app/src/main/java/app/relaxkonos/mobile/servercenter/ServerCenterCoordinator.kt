package app.relaxkonos.mobile.servercenter

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * Process-owned navigation state for the server centre.
 *
 * It owns no persisted SSH password or deployment request.  A verified workspace may retain one
 * transient in-memory password until that workspace closes; it is never written to a target,
 * navigation argument or credential store.
 */
class ServerCenterCoordinator(
    private val targets: ServerHostTargetStore,
    private val connections: ServerCenterConnectionResolver,
    private val hostKeys: ServerHostKeyTrustStore,
) {
    var isOpen by mutableStateOf(false)
        private set

    /** The SSH browser is a child of Server Centre, never a sixth top-level destination. */
    var sshFilesHostId by mutableStateOf<String?>(null)
        private set

    /** In-memory only credential for the verified SSH workspace; cleared when that workspace closes. */
    private var sshWorkspacePassword: CharArray? = null

    var revision by mutableIntStateOf(0)
        private set

    fun open() {
        isOpen = true
        revision++
    }

    fun close() {
        isOpen = false
        closeSshFiles()
    }

    fun openSshFiles(hostId: String, password: CharArray) {
        require(targets.find(hostId) != null) { "Unknown host target '$hostId'." }
        sshWorkspacePassword?.fill('\u0000')
        sshWorkspacePassword = password.copyOf()
        sshFilesHostId = hostId
    }

    fun workspacePasswordCopy(): CharArray? = sshWorkspacePassword?.copyOf()

    fun closeSshFiles() {
        sshFilesHostId = null
        sshWorkspacePassword?.fill('\u0000')
        sshWorkspacePassword = null
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
        val removed = targets.remove(hostId)
        if (removed) revision++
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
