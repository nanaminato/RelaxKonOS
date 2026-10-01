package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class EventAlertBrowserTest {
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = EventAlertRepository(gateway, session)
    private fun alert(id: String) = OperationalAlert(id, "deployment.operation_failed", "error", "open", "failure", 1, 1,
        "applicationDeployment", "app", null, 0, "e", null, null, null)
    private suspend fun owner(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        gateway.onEventSummary = { ApiResult.Success(EventAlertSummary(2, 0, 0, "error", 1)) }
        return session.state.value as SessionState.Active
    }
    @Test fun `cursor paging deduplicates and stops a repeating cursor`() = runTest {
        val owner = owner(); val cursors = mutableListOf<String?>()
        gateway.onAlerts = { cursor, _ -> cursors += cursor; ApiResult.Success(OperationalAlertPage(
            if (cursor == null) listOf(alert("a")) else listOf(alert("a"), alert("b")), "next")) }
        val browser = EventAlertBrowser(repository, backgroundScope)
        browser.activate(owner); runCurrent(); browser.more(); runCurrent()
        assertEquals(listOf(null, "next"), cursors); assertEquals(listOf("a", "b"), browser.state.value.alerts.map { it.id })
        assertNull(browser.state.value.nextCursor); browser.stop()
    }
    @Test fun `new filters discard old cursor and old rows`() = runTest {
        val owner = owner(); val queries = mutableListOf<Pair<String?, AlertQuery>>()
        gateway.onAlerts = { cursor, query -> queries += cursor to query; ApiResult.Success(OperationalAlertPage(listOf(alert(query.status?.wire ?: "all")), "next")) }
        val browser = EventAlertBrowser(repository, backgroundScope)
        browser.activate(owner); runCurrent()
        browser.filters(false, AlertQuery(status = AlertStatusFilter.Resolved), EventQuery()); runCurrent()
        assertEquals(null to AlertQuery(status = AlertStatusFilter.Resolved), queries.last())
        assertEquals(listOf("resolved"), browser.state.value.alerts.map { it.id }); browser.stop()
    }
    @Test fun `failed next page preserves current rows and retry cursor`() = runTest {
        val owner = owner(); gateway.onAlerts = { cursor, _ -> if (cursor == null) ApiResult.Success(OperationalAlertPage(listOf(alert("a")), "next")) else ApiResult.Transport(null) }
        val browser = EventAlertBrowser(repository, backgroundScope); browser.activate(owner); runCurrent(); browser.more(); runCurrent()
        assertEquals("a", browser.state.value.alerts.single().id); assertEquals("next", browser.state.value.nextCursor)
        assertTrue(browser.state.value.readResult is ApiResult.Transport); browser.stop()
    }
    @Test fun `leaving page cancels late reads and prevents foreground polling`() = runTest {
        val owner = owner(); var reads = 0
        gateway.onAlerts = { _, _ -> reads++; ApiResult.Success(OperationalAlertPage(listOf(alert("a")), null)) }
        val browser = EventAlertBrowser(repository, backgroundScope); browser.activate(owner); runCurrent(); browser.stop()
        advanceTimeBy(60_000); runCurrent()
        assertEquals(1, reads); assertFalse(browser.state.value.active); assertTrue(browser.state.value.alerts.isEmpty())
    }
}
