package app.relaxkonos.mobile.servercenter

import kotlinx.coroutines.CancellationException

/** Owns one adopted login tunnel; network cleanup always runs outside the ownership lock. */
internal class LoginTunnelOwnership(
    private val matchesActiveConnection: (ServerConnectionIdentity) -> Boolean,
    private val isSignedOut: () -> Boolean,
    private val onCleanupFailure: (Exception) -> Unit,
) {
    private val gate = Any()
    private var owned: (() -> Unit)? = null

    fun adopt(identity: ServerConnectionIdentity, close: () -> Unit) {
        val (previous, accepted) = synchronized(gate) {
            if (matchesActiveConnection(identity)) {
                val previous = owned
                owned = close
                previous to true
            } else {
                val previous = if (isSignedOut()) owned.also { owned = null } else null
                previous to false
            }
        }
        try { release(previous) }
        finally { if (!accepted) release(close) }
    }

    fun releaseIfSignedOut() {
        val previous = synchronized(gate) {
            if (isSignedOut()) owned.also { owned = null } else null
        }
        release(previous)
    }

    private fun release(close: (() -> Unit)?) {
        try { close?.invoke() }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (error: Exception) { onCleanupFailure(error) }
    }
}
