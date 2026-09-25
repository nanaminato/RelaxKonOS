package app.relaxkonos.mobile.servercenter

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * Process-owned navigation state for the server centre.
 *
 * This deliberately owns no SSH password or deployment request.  It survives a composable being
 * removed, while secrets remain confined to the SSH credential store and a later operation
 * coordinator can safely outlive the individual list/detail screens.
 */
class ServerCenterCoordinator(
    private val targets: ServerHostTargetStore,
    private val connections: ServerCenterConnectionResolver,
    private val hostKeys: ServerHostKeyTrustStore,
) {
    var isOpen by mutableStateOf(false)
        private set

    var revision by mutableIntStateOf(0)
        private set

    fun open() {
        isOpen = true
        revision++
    }

    fun close() {
        isOpen = false
    }

    fun hosts(): List<ServerHostTarget> {
        // Reading revision establishes a Compose observation point after a successful mutation.
        revision
        return targets.all()
    }

    fun addHost(host: String, port: Int, userName: String, displayName: String?) {
        targets.upsert(ServerHostTargetRules.create(host, port, userName, displayName, System.currentTimeMillis()))
        revision++
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
    suspend fun verifySsh(hostId: String, credential: SshCredential): ServerCenterSshVerification = try {
        connections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
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
