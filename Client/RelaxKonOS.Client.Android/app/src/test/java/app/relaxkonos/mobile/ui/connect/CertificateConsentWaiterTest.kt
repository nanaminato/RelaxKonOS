package app.relaxkonos.mobile.ui.connect

import app.relaxkonos.mobile.core.net.CertificateReview
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class CertificateConsentWaiterTest {
    private val review = CertificateReview("https://test.invalid", "test", "test", "A".repeat(64), null, "from", "until")

    @Test fun replacementRejectsPreviousWaitAndOldButtonCannotAnswerTheNewPrompt() = runTest {
        val waiter = CertificateConsentWaiter()
        val first = async { waiter.ask(review) }
        runCurrent()
        val old = requireNotNull(waiter.current)
        val second = async { waiter.ask(review) }
        runCurrent()
        val current = requireNotNull(waiter.current)
        assertNotSame(old, current)
        assertFalse(first.await())
        waiter.answer(old, true)
        assertFalse(second.isCompleted)
        assertSame(current, waiter.current)
        waiter.answer(current, true)
        assertTrue(second.await())
        assertNull(waiter.current)
    }

    @Test fun cancelledOldFinallyCannotClearAReplacementAlreadyStarted() = runTest {
        val waiter = CertificateConsentWaiter()
        val first = async { waiter.ask(review) }
        runCurrent()
        first.cancel()
        val second = async(start = CoroutineStart.UNDISPATCHED) { waiter.ask(review) }
        val current = requireNotNull(waiter.current)
        runCurrent()
        assertTrue(first.isCancelled)
        assertSame(current, waiter.current)
        waiter.answer(current, false)
        assertFalse(second.await())
        assertNull(waiter.current)
    }
}
