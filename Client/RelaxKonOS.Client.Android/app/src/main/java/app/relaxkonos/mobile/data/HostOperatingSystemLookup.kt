package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.security.model.SavedLogin
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

/**
 * Fills in the host operating system of saved connections that have never been asked.
 *
 * A row is stored before this app has heard anything from the server, and an answer may have been
 * unavailable when it was stored — an old record, or a host that was offline. So the list resolves the
 * missing ones when it is opened, keeps whatever came back, and shows the matching mark
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §6.3).
 *
 * Three rules keep this from turning a dialog into a scan:
 *
 * - an entry whose answer is already stored is never asked again — including one stored as
 *   [app.relaxkonos.mobile.core.net.HostOperatingSystemKind.Unknown], which is a final answer and not a
 *   missing one;
 * - a managed login is never asked: its address is a loopback tunnel that exists only while its host is
 *   open, and opening hosts is not something a list may do;
 * - a failure is not an answer. A transport or contract failure leaves the entry unasked so the next
 *   open can try again, rather than freezing "could not ask" into the record forever.
 */
class HostOperatingSystemLookup(
    private val profiles: ConnectionProfileStore,
    private val gateway: RelaxKonGateway,
) {
    /**
     * Resolves every unasked entry in [logins], concurrently, and returns how many answers were stored.
     *
     * The count is what lets a screen skip a needless recomposition when, as usual, every row already
     * knew its mark.
     */
    suspend fun resolve(logins: List<SavedLogin>): Int {
        val pending = logins.mapNotNull { login ->
            val serverUrl = login.directServerUrl ?: return@mapNotNull null
            if (login.hostOperatingSystem != null) return@mapNotNull null
            login to serverUrl
        }
        if (pending.isEmpty()) return 0

        // Opening a list is not a reason to have a socket per saved server open at once. The limit is
        // on *this* work only: the answers are independent, so ordering between them means nothing.
        val permits = Semaphore(MAXIMUM_PARALLEL_LOOKUPS)
        return coroutineScope {
            pending
                .map { (login, serverUrl) ->
                    async {
                        permits.withPermit {
                            val answer = gateway.hostOperatingSystem(serverUrl)
                            answer is ApiResult.Success &&
                                profiles.setHostOperatingSystem(login.serviceId, login.identifier, answer.value)
                        }
                    }
                }
                .awaitAll()
                .count { it }
        }
    }

    private companion object {
        const val MAXIMUM_PARALLEL_LOOKUPS = 4
    }
}
