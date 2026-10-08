package app.relaxkonos.mobile.ui.files

import app.relaxkonos.mobile.core.net.ApiResult
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class FileMutationRequestTest {
    @Test fun `unexpected write failure becomes unknown without replay or private detail`() = runTest {
        var calls = 0
        val result = fileMutationRequest<Unit> { calls++; throw IllegalStateException("private request data") }
        assertEquals(ApiResult.Transport(null), result)
        assertEquals(1, calls)
        val confirmed = ApiResult.Success(Unit)
        assertSame(confirmed, fileMutationRequest { confirmed })
        val refused = ApiResult.Problem(403, "elevation-required", null)
        assertSame(refused, fileMutationRequest<Unit> { refused })
    }

    @Test fun `cancellation propagates without replay`() = runTest {
        var calls = 0
        val cancellation = CancellationException("cancelled")
        try {
            fileMutationRequest<Unit> { calls++; throw cancellation }
            fail("Cancellation must propagate")
        } catch (actual: CancellationException) {
            assertSame(cancellation, actual)
        }
        assertEquals(1, calls)
    }
}
