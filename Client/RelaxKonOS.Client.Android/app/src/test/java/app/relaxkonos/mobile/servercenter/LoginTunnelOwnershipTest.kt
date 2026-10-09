package app.relaxkonos.mobile.servercenter

import org.junit.Assert.*
import org.junit.Test
import kotlinx.coroutines.CancellationException

class LoginTunnelOwnershipTest {
    @Test fun rejectedCandidateIsReleasedEvenWhenPreviousCleanupPropagatesCancellation() {
        var active: ServerConnectionIdentity? = first
        val owner = LoginTunnelOwnership({ it == active }, { active == null }, { fail("Cancellation must not be mapped to a failure") })
        val cancelled = CancellationException("test cancellation")
        var rejectedClosed = 0
        owner.adopt(first) { throw cancelled }
        active = null
        assertTrue(runCatching { owner.adopt(second) { rejectedClosed++ } }.exceptionOrNull() === cancelled)
        assertEquals(1, rejectedClosed)
        owner.releaseIfSignedOut()
        assertEquals(1, rejectedClosed)
    }
    private val profile = SshLoginTunnelProfile.create("test.invalid", 22, "test", "http://127.0.0.1:5000")
    private val first = profile.resolve(12000)
    private val second = profile.resolve(12001)

    @Test fun oldCleanupFailureIsReportedWithoutLosingTheNewOwner() {
        var active: ServerConnectionIdentity? = first
        val errors = mutableListOf<Exception>()
        val owner = LoginTunnelOwnership({ it == active }, { active == null }, errors::add)
        val failure = IllegalStateException("test cleanup failure")
        var closed = 0
        owner.adopt(first) { throw failure }
        active = second
        owner.adopt(second) { closed++ }
        assertEquals(listOf(failure), errors)
        owner.releaseIfSignedOut()
        assertEquals(0, closed)
        active = null
        owner.releaseIfSignedOut()
        owner.releaseIfSignedOut()
        assertEquals(1, closed)
    }

    @Test fun staleAdoptionCannotReplaceAnActiveConnectionWithTheSameStableIdAndDifferentPort() {
        var active: ServerConnectionIdentity? = second
        val owner = LoginTunnelOwnership({ it == active }, { active == null }, { fail("Unexpected cleanup failure") })
        var currentClosed = 0
        var staleClosed = 0
        owner.adopt(second) { currentClosed++ }
        owner.adopt(first) { staleClosed++ }
        assertEquals(first.serviceId, second.serviceId)
        assertEquals(1, staleClosed)
        assertEquals(0, currentClosed)
        active = null
        owner.releaseIfSignedOut()
        assertEquals(1, currentClosed)
    }

    @Test fun adoptionAfterSignOutReleasesBothThePreviousAndRejectedCandidate() {
        var active: ServerConnectionIdentity? = first
        val errors = mutableListOf<Exception>()
        val owner = LoginTunnelOwnership({ it == active }, { active == null }, errors::add)
        val failure = IllegalStateException("test close failure")
        var rejectedClosed = 0
        owner.adopt(first) { throw failure }
        active = null
        owner.adopt(second) { rejectedClosed++ }
        assertEquals(1, rejectedClosed)
        assertEquals(listOf(failure), errors)
        owner.releaseIfSignedOut()
        assertEquals(1, rejectedClosed)
    }
}
