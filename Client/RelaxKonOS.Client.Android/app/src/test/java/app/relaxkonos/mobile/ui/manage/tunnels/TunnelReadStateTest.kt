package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TunnelFacts
import org.junit.Assert.*
import org.junit.Test

class TunnelReadStateTest {
    private val facts = ApiResult.Success(TunnelFacts(emptyList(), emptyList(), 1))
    private val runtime = ApiResult.Success(TunnelRuntime("managed", TunnelRuntimeMode.Managed,
        TunnelRuntimeState.NotInstalled, null, null, "", null, null, false))
    private val frps = ApiResult.Success(ManagedFrps("0.0.0.0", 7000, emptyList(), null, null,
        false, false, false, "127.0.0.1", null, null, false, ManagedFrpsState.NotConfigured,
        0, null, "", null))

    @Test fun frpsRejectionIsVisibleWhenClientReadsSucceed() {
        val rejected = ApiResult.Problem(403, "tunnel.forbidden", null)
        val state = TunnelsState().withReadResults(facts, runtime, rejected, 2)
        assertEquals("tunnel.forbidden", state.problemCode)
        assertTrue(state.uncertain)
        assertSame(rejected, state.frps)
    }

    @Test fun frpsTransportFailureRetainsDraftAndRequiresRecovery() {
        val draft = ManagedFrpsDraft()
        val state = TunnelsState(frpsDraft = draft, initialFrps = draft)
            .withReadResults(facts, runtime, ApiResult.Transport(null), 2)
        assertTrue(state.uncertain)
        assertNull(state.problemCode)
        assertSame(draft, state.frpsDraft)
        assertSame(draft, state.initialFrps)
    }

    @Test fun successfulRefreshClearsOldReadFailure() {
        val state = TunnelsState(problemCode = "tunnel.forbidden", uncertain = true)
            .withReadResults(facts, runtime, frps, 3)
        assertFalse(state.uncertain)
        assertNull(state.problemCode)
        assertEquals(3L, state.frpsAtMillis)
    }
}
